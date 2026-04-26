using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using FireRa.Service.Grpc;

const string teamName = "Bitwiser";
const string serverAddress = "http://10.4.4.59:5001";
const string manualCommandPath = "manual-commands.json";
const int TargetOutgoingCommandsPerSecond = 24;
const int CommandsPerSendWindow = 1;

var gameState = new GameState();
var brain = new BotAI(gameState);
var renderer = new ConsoleRenderer(gameState);
var dashboard = new DashboardWriter(gameState);

int counter = 1;
int commandUnitCursor = 0;

DateTime lastCommandSentAt = DateTime.MinValue;
DateTime lastRenderAt = DateTime.MinValue;
DateTime manualModeStartedAt = DateTime.UtcNow;
DateTime lastRateReportAt = DateTime.UtcNow;
long lastCommandedSnapshotVersion = -1;

long outgoingCommandTotal = 0;
long outgoingWakeupTotal = 0;
long incomingMessageTotal = 0;
long incomingAckTotal = 0;
long incomingUnitsTotal = 0;

long lastOutgoingCommandTotal = 0;
long lastIncomingMessageTotal = 0;
long lastIncomingAckTotal = 0;
long lastIncomingUnitsTotal = 0;

var latestDecisions = new Dictionary<int, UnitDecision>();
var processedManualSequences = new Dictionary<int, long>();

var minCommandInterval = TimeSpan.FromMilliseconds(1000.0 / TargetOutgoingCommandsPerSecond);
var renderInterval = TimeSpan.FromMilliseconds(100);

var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback =
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
};

using var channel = GrpcChannel.ForAddress(serverAddress, new GrpcChannelOptions
{
    HttpHandler = handler
});

var client = new FireRaService.FireRaServiceClient(channel);

Console.WriteLine("Registering team...");
var helloReply = await client.SayHelloAsync(new HelloRequest
{
    TeamName = teamName
});
Console.WriteLine($"Server replied: {helloReply.Message}");
Console.WriteLine($"[DEBUG] Hello response status: {helloReply?.Message ?? "NULL"}");

using var stream = client.CommunicateWithStreams();
Console.WriteLine("[DEBUG] Stream opened, waiting for messages...");

// ----------------- INCOMING STREAM READ TASK -----------------
var readTask = Task.Run(async () =>
{
    try
    {
        int messageCount = 0;
        int unitsMessageCount = 0;
        await foreach (var msg in stream.ResponseStream.ReadAllAsync())
        {
            messageCount++;
            System.Threading.Interlocked.Increment(ref incomingMessageTotal);

            if (msg.Operation != "ACK" && messageCount % 50 == 1)
                Console.WriteLine($"[DEBUG] Received message #{messageCount}: Operation={msg.Operation}");

            if (msg.Operation == "UnitsFromServer")
            {
                var parsedUnits = ParseUnits(msg.ExtraJson);
                unitsMessageCount++;
                System.Threading.Interlocked.Increment(ref incomingUnitsTotal);

                if (unitsMessageCount % 25 == 1)
                    Console.WriteLine($"[DEBUG] UnitsFromServer: {parsedUnits.Count} units received");

                lock (gameState.StateLock)
                {
                    gameState.ProcessUnitsFromServer(parsedUnits);
                }
            }
            else if (msg.Operation == "InformationFromServer")
            {
                Console.WriteLine($"INFO: {msg.ExtraJson}");

                lock (gameState.StateLock)
                {
                    if (msg.ExtraJson.Contains("Out of Bounds", StringComparison.OrdinalIgnoreCase) ||
                        msg.ExtraJson.Contains("Invalid Move", StringComparison.OrdinalIgnoreCase))
                    {
                        // Optional future improvement:
                        // Parse this message and mark blocked / out-of-bounds cells.
                    }
                }
            }
            else if (msg.Operation == "ACK")
            {
                System.Threading.Interlocked.Increment(ref incomingAckTotal);

                // Optional debug:
                // Console.WriteLine($"ACK received for counter {msg.Counter}");
            }
            else
            {
                Console.WriteLine($"[DEBUG] Unknown operation: {msg.Operation}");
            }
        }
        Console.WriteLine("[DEBUG] Stream closed by server after " + messageCount + " messages");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(
            $"\n!!! BACKGROUND READ TASK CRASHED !!!\n{ex.Message}\n{ex.StackTrace}\n"
        );
        Console.ResetColor();
    }
});

// ----------------- MAIN GAME LOOP -----------------
while (true)
{
    List<UnitData> unitsSnapshot;
    long snapshotVersion;

    lock (gameState.StateLock)
    {
        unitsSnapshot = gameState.LatestUnits.ToList();
        snapshotVersion = gameState.SnapshotVersion;
    }

    bool canSendCommand =
        DateTime.UtcNow - lastCommandSentAt >= minCommandInterval;
    bool hasFreshSnapshot = snapshotVersion > lastCommandedSnapshotVersion;

    if (canSendCommand && unitsSnapshot.Count == 0)
    {
        var wakeUpCommand = new CommandMessage
        {
            TeamName = teamName,
            Counter = counter++,
            UnitId = 0,
            Operation = "NOP",
            ExtraJson = ""
        };

        await stream.RequestStream.WriteAsync(wakeUpCommand);
        System.Threading.Interlocked.Increment(ref outgoingCommandTotal);
        System.Threading.Interlocked.Increment(ref outgoingWakeupTotal);

        if (counter % 20 == 1)
            Console.WriteLine($"[DEBUG] Sending NOP wake-up (counter={counter}) - Tick {gameState.Tick}");
        lastCommandSentAt = DateTime.UtcNow;
    }
    else if (canSendCommand && unitsSnapshot.Count > 0)
    {
        var manualControl = ReadManualControl(manualCommandPath);
        ManualCommand? manualCommand = manualControl.ManualMode
            ? TryPickManualCommand(
                manualControl,
                unitsSnapshot,
                processedManualSequences,
                manualModeStartedAt
            )
            : null;

        if (hasFreshSnapshot)
        {
            foreach (var unit in unitsSnapshot)
            {
                UnitDecision decision;

                if (manualCommand != null && manualCommand.UnitId == unit.Id)
                {
                    decision = new UnitDecision
                    {
                        UnitId = unit.Id,
                        Operation = manualCommand.Operation,
                        Intent = $"Manual control by {manualCommand.Controller}"
                    };

                    if (manualCommand.Operation is "Up" or "Down" or "Left" or "Right")
                    {
                        decision.Target = PredictManualTarget(
                            unit.Position,
                            manualCommand.Operation
                        );
                    }

                    processedManualSequences[manualCommand.UnitId] = manualCommand.Sequence;
                }
                else if (manualControl.ManualMode)
                {
                    decision = new UnitDecision
                    {
                        UnitId = unit.Id,
                        Operation = "NOP",
                        Intent = $"Manual mode ON by {manualControl.UpdatedBy}; waiting for human command"
                    };
                }
                else
                {
                    lock (gameState.StateLock)
                    {
                        decision = brain.DecideDetailed(unit);
                    }
                }

                latestDecisions[unit.Id] = decision;
            }

            lastCommandedSnapshotVersion = snapshotVersion;
        }
        else if (manualCommand != null)
        {
            var unit = unitsSnapshot.First(u => u.Id == manualCommand.UnitId);
            var decision = new UnitDecision
            {
                UnitId = unit.Id,
                Operation = manualCommand.Operation,
                Intent = $"Manual control by {manualCommand.Controller}"
            };

            if (manualCommand.Operation is "Up" or "Down" or "Left" or "Right")
            {
                decision.Target = PredictManualTarget(
                    unit.Position,
                    manualCommand.Operation
                );
            }

            processedManualSequences[manualCommand.UnitId] = manualCommand.Sequence;
            latestDecisions[unit.Id] = decision;
        }

        int commandsToSend = manualControl.ManualMode
            ? 1
            : Math.Min(CommandsPerSendWindow, unitsSnapshot.Count);

        for (int i = 0; i < commandsToSend; i++)
        {
            var unit = PickNextCommandUnit(unitsSnapshot, ref commandUnitCursor);
            if (!latestDecisions.TryGetValue(unit.Id, out var decision))
                continue;

            if (decision.Operation == "NOP")
                continue;

            var command = new CommandMessage
            {
                TeamName = teamName,
                Counter = counter++,
                UnitId = (uint)unit.Id,
                Operation = decision.Operation,
                ExtraJson = ""
            };

            await stream.RequestStream.WriteAsync(command);
            System.Threading.Interlocked.Increment(ref outgoingCommandTotal);
        }
        lastCommandSentAt = DateTime.UtcNow;
    }

    if (DateTime.UtcNow - lastRenderAt >= renderInterval)
    {
        renderer.DrawStatus(latestDecisions);
        dashboard.Write(latestDecisions);
        lastRenderAt = DateTime.UtcNow;
    }

    if (DateTime.UtcNow - lastRateReportAt >= TimeSpan.FromSeconds(1))
    {
        var now = DateTime.UtcNow;
        double seconds = Math.Max(0.001, (now - lastRateReportAt).TotalSeconds);

        long outTotal = System.Threading.Interlocked.Read(ref outgoingCommandTotal);
        long inTotal = System.Threading.Interlocked.Read(ref incomingMessageTotal);
        long ackTotal = System.Threading.Interlocked.Read(ref incomingAckTotal);
        long unitsTotal = System.Threading.Interlocked.Read(ref incomingUnitsTotal);
        long wakeTotal = System.Threading.Interlocked.Read(ref outgoingWakeupTotal);

        int unitCount;
        long snapshotVersionForDebug;
        int tickForDebug;

        lock (gameState.StateLock)
        {
            unitCount = gameState.LatestUnits.Count;
            snapshotVersionForDebug = gameState.SnapshotVersion;
            tickForDebug = gameState.Tick;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(
            $"[RATE] out={(outTotal - lastOutgoingCommandTotal) / seconds:0.0}/s " +
            $"in={(inTotal - lastIncomingMessageTotal) / seconds:0.0}/s " +
            $"ack={(ackTotal - lastIncomingAckTotal) / seconds:0.0}/s " +
            $"units={(unitsTotal - lastIncomingUnitsTotal) / seconds:0.0}/s " +
            $"totalOut={outTotal} wake={wakeTotal} counter={counter} " +
            $"snap={snapshotVersionForDebug} tick={tickForDebug} unitsNow={unitCount}"
        );
        Console.ResetColor();

        lastOutgoingCommandTotal = outTotal;
        lastIncomingMessageTotal = inTotal;
        lastIncomingAckTotal = ackTotal;
        lastIncomingUnitsTotal = unitsTotal;
        lastRateReportAt = now;
    }

    await Task.Delay(5);
}

// ----------------- COMMAND UNIT PICKING -----------------
static UnitData PickNextCommandUnit(List<UnitData> units, ref int cursor)
{
    var ordered = units
        .OrderByDescending(CommandPriority)
        .ThenBy(u => u.Id)
        .ToList();

    if (ordered.Count == 0)
        throw new InvalidOperationException("No units available.");

    var selected = ordered[cursor % ordered.Count];
    cursor = (cursor + 1) % ordered.Count;

    return selected;
}

static int CommandPriority(UnitData unit)
{
    bool isDrone =
        unit.UnitType.Contains("copter", StringComparison.OrdinalIgnoreCase);

    bool isTruck =
        unit.UnitType.Contains("truck", StringComparison.OrdinalIgnoreCase);

    bool isFighter =
        unit.UnitType.Contains("fighter", StringComparison.OrdinalIgnoreCase);

    if (unit.SeenFires.Count > 0)
        return 100;

    if (isDrone && unit.CurrentWaterLevel <= 1)
        return 90;

    if (isTruck && unit.CurrentWaterLevel <= 4)
        return 85;

    if (isDrone)
        return 70;

    if (isTruck)
        return 60;

    return isFighter ? 45 : 30;
}

// ----------------- MANUAL CONTROL -----------------
static ManualCommandFile ReadManualControl(string path)
{
    if (!File.Exists(path))
        return new ManualCommandFile();

    try
    {
        var json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<ManualCommandFile>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }
        ) ?? new ManualCommandFile();
    }
    catch
    {
        return new ManualCommandFile();
    }
}

static ManualCommand? TryPickManualCommand(
    ManualCommandFile commandFile,
    List<UnitData> units,
    Dictionary<int, long> processedManualSequences,
    DateTime startedAt)
{
    try
    {
        if (commandFile.Commands == null)
            return null;

        var unitIds = units.Select(u => u.Id).ToHashSet();

        ManualCommand? pickedCommand = commandFile.Commands
            .Where(c => c != null)
            .Where(c => unitIds.Contains(c.UnitId))
            .Where(c => IsManualOperation(c.Operation))
            .Where(c => IsFreshManualCommand(c, startedAt))
            .Where(c =>
                !processedManualSequences.TryGetValue(c.UnitId, out var sequence) ||
                c.Sequence > sequence
            )
            .OrderByDescending(c => c.Sequence)
            .FirstOrDefault();

        return pickedCommand;
    }
    catch
    {
        return null;
    }
}

static bool IsManualOperation(string operation)
{
    return operation is
        "Up" or
        "Down" or
        "Left" or
        "Right" or
        "ExtinguishFire" or
        "RefillWithWater" or
        "NOP";
}

static bool IsFreshManualCommand(ManualCommand? command, DateTime startedAt)
{
    if (command == null)
        return false;

    if (!DateTime.TryParse(command.IssuedAt, out var issuedAt))
        return true;

    return issuedAt.ToUniversalTime() >= startedAt.AddSeconds(-2);
}

static Pos PredictManualTarget(Pos position, string operation)
{
    return operation switch
    {
        "Up" => new Pos
        {
            X = position.X,
            Y = position.Y - 1
        },

        "Down" => new Pos
        {
            X = position.X,
            Y = position.Y + 1
        },

        "Left" => new Pos
        {
            X = position.X - 1,
            Y = position.Y
        },

        "Right" => new Pos
        {
            X = position.X + 1,
            Y = position.Y
        },

        _ => position
    };
}

// ----------------- JSON PARSING HELPERS -----------------
static bool TryGetProp(JsonElement el, string name, out JsonElement result)
{
    if (el.ValueKind != JsonValueKind.Object)
    {
        result = default;
        return false;
    }

    foreach (var prop in el.EnumerateObject())
    {
        if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            result = prop.Value;
            return true;
        }
    }

    result = default;
    return false;
}

static List<UnitData> ParseUnits(string json)
{
    var result = new List<UnitData>();

    if (string.IsNullOrWhiteSpace(json))
    {
        Console.WriteLine("[DEBUG] ParseUnits: JSON is empty or null");
        return result;
    }

    try
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine($"[DEBUG] ParseUnits: Expected array, got {doc.RootElement.ValueKind}");
            return result;
        }

        foreach (var unitEl in doc.RootElement.EnumerateArray())
        {
            var unit = new UnitData
            {
                Id = GetInt(unitEl, "Id"),
                UnitType = GetString(unitEl, "UnitType"),
                Owner = GetString(unitEl, "Owner"),
                Position = ExtractPositionFlexible(unitEl),
                CurrentWaterLevel = GetInt(unitEl, "CurrentWaterLevel"),
                CurrentHP = GetInt(unitEl, "CurrentHP")
            };

            if (TryGetProp(unitEl, "SeenFires", out var firesEl) &&
                firesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var fireEl in firesEl.EnumerateArray())
                {
                    var pos = ExtractPositionFlexible(fireEl);
                    pos.Hp = ExtractHpFlexible(fireEl);
                    unit.SeenFires.Add(pos);
                }
            }

            if (TryGetProp(unitEl, "SeenWaters", out var watersEl) &&
                watersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var waterEl in watersEl.EnumerateArray())
                {
                    var pos = ExtractPositionFlexible(waterEl);
                    unit.SeenWaters.Add(pos);
                }
            }

            result.Add(unit);
        }

    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DEBUG] ParseUnits ERROR: {ex.Message}");
        Console.WriteLine($"[DEBUG] JSON preview: {json.Substring(0, Math.Min(200, json.Length))}...");
    }

    return result;
}

static Pos ExtractPositionFlexible(JsonElement el)
{
    if (TryGetProp(el, "Position", out var posEl))
        return ExtractPosition(posEl);

    if (TryGetProp(el, "Cell", out var cellEl))
        return ExtractPosition(cellEl);

    if (TryGetProp(el, "Location", out var locEl))
        return ExtractPosition(locEl);

    return ExtractPosition(el);
}

static Pos ExtractPosition(JsonElement el)
{
    return new Pos
    {
        IsEmpty = GetBool(el, "IsEmpty"),
        X = GetInt(el, "X"),
        Y = GetInt(el, "Y")
    };
}

static int ExtractHpFlexible(JsonElement el)
{
    if (TryGetProp(el, "CurrentHP", out var hpEl) ||
        TryGetProp(el, "HP", out hpEl) ||
        TryGetProp(el, "Health", out hpEl))
    {
        if (hpEl.ValueKind == JsonValueKind.Number)
            return hpEl.GetInt32();

        if (hpEl.ValueKind == JsonValueKind.String &&
            int.TryParse(hpEl.GetString(), out var parsed))
        {
            return parsed;
        }
    }

    return 1000;
}

static int GetInt(JsonElement el, string name)
{
    if (TryGetProp(el, name, out var v))
    {
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();

        if (v.ValueKind == JsonValueKind.String &&
            int.TryParse(v.GetString(), out int parsed))
        {
            return parsed;
        }
    }

    return 0;
}

static bool GetBool(JsonElement el, string name)
{
    if (TryGetProp(el, name, out var v))
    {
        if (v.ValueKind == JsonValueKind.True)
            return true;

        if (v.ValueKind == JsonValueKind.False)
            return false;

        if (v.ValueKind == JsonValueKind.String &&
            bool.TryParse(v.GetString(), out bool parsed))
        {
            return parsed;
        }
    }

    return false;
}

static string GetString(JsonElement el, string name)
{
    if (TryGetProp(el, name, out var v))
    {
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";

        return v.ToString();
    }

    return "";
}

