// using System.Text.Json;
// using Grpc.Core;
// using Grpc.Net.Client;
// using FireRa.Service.Grpc;

// const string teamName = "Bitwiser";
// const string serverAddress = "http://10.4.4.59:5001";

// object stateLock = new();

// List<UnitState> latestUnits = new();
// Dictionary<string, KnownFire> knownFires = new();
// Dictionary<string, KnownWater> knownWaters = new();

// int counter = 1;
// int tick = 0;

// var handler = new HttpClientHandler
// {
//     ServerCertificateCustomValidationCallback =
//         HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
// };

// using var channel = GrpcChannel.ForAddress(serverAddress, new GrpcChannelOptions
// {
//     HttpHandler = handler
// });

// var client = new FireRaService.FireRaServiceClient(channel);

// Console.WriteLine("Registering team...");

// var helloReply = await client.SayHelloAsync(new HelloRequest
// {
//     TeamName = teamName
// });

// Console.WriteLine($"Server replied: {helloReply.Message}");

// using var stream = client.CommunicateWithStreams();

// var readTask = Task.Run(async () =>
// {
//     await foreach (var msg in stream.ResponseStream.ReadAllAsync())
//     {
//         if (msg.Operation == "UnitsFromServer")
//         {
//             var parsedUnits = ParseUnits(msg.ExtraJson);

//             lock (stateLock)
//             {
//                 latestUnits = parsedUnits;

//                 foreach (var unit in parsedUnits)
//                 {
//                     foreach (var fire in unit.SeenFires)
//                     {
//                         string key = Key(fire.X, fire.Y);

//                         knownFires[key] = new KnownFire
//                         {
//                             Position = fire,
//                             Hp = fire.Hp,
//                             LastSeenTick = tick
//                         };
//                     }

//                     foreach (var water in unit.SeenWaters)
//                     {
//                         string key = Key(water.X, water.Y);

//                         knownWaters[key] = new KnownWater
//                         {
//                             Position = water,
//                             LastSeenTick = tick
//                         };
//                     }
//                 }

//                 CleanupOldMemory();

//                 if (tick % 4 == 0)
//                     DrawStatus();
//             }
//         }
//         else if (msg.Operation == "InformationFromServer")
//         {
//             Console.WriteLine($"INFO: {msg.ExtraJson}");
//         }
//     }
// });

// while (true)
// {
//     tick++;

//     List<UnitState> unitsSnapshot;

//     lock (stateLock)
//     {
//         unitsSnapshot = latestUnits.ToList();
//     }

//     foreach (var unit in unitsSnapshot)
//     {
//         string operation;

//         lock (stateLock)
//         {
//             operation = Decide(unit);
//         }

//         var command = new CommandMessage
//         {
//             TeamName = teamName,
//             Counter = counter++,
//             UnitId = (uint)unit.Id,
//             Operation = operation,
//             ExtraJson = ""
//         };

//         await stream.RequestStream.WriteAsync(command);
//     }

//     await Task.Delay(250);
// }

// string Decide(UnitState unit)
// {
//     bool isDrone = unit.UnitType.Contains("copter", StringComparison.OrdinalIgnoreCase);
//     bool isTruck = unit.UnitType.Contains("truck", StringComparison.OrdinalIgnoreCase);
//     bool isFighter = unit.UnitType.Contains("fighter", StringComparison.OrdinalIgnoreCase);

//     int waterThreshold = isDrone ? 1 : isTruck ? 4 : 0;

//     // 1. If unit has no/low water, go refill.
//     // Firefighter has effectively infinite water in the rules, so it does not refill.
//     if (!isFighter && unit.CurrentWaterLevel <= waterThreshold)
//     {
//         var nearestWater = knownWaters.Values
//             .OrderBy(w => Dist(unit.Position, w.Position))
//             .FirstOrDefault();

//         if (nearestWater != null)
//         {
//             if (Dist(unit.Position, nearestWater.Position) <= 1)
//                 return "RefillWithWater";

//             return MoveToward(unit.Position, nearestWater.Position);
//         }

//         // No water known: keep exploring, especially drone.
//         return Explore(unit);
//     }

//     // 2. If visible fire is adjacent, extinguish.
//     var adjacentFire = unit.SeenFires
//         .Where(f => Dist(unit.Position, f) <= 1)
//         .OrderBy(f => f.Hp)
//         .FirstOrDefault();

//     if (adjacentFire != null)
//         return "ExtinguishFire";

//     // 3. Choose target by role.
//     KnownFire? target = null;

//     if (knownFires.Count > 0)
//     {
//         if (isFighter)
//         {
//             // Ranking priority #1 is extinguished fires.
//             // Firefighter tries to finish lowest HP known fire.
//             target = knownFires.Values
//                 .OrderBy(f => f.Hp)
//                 .ThenBy(f => Dist(unit.Position, f.Position))
//                 .FirstOrDefault();
//         }
//         else if (isTruck)
//         {
//             // Truck has best damage, so minimize travel time.
//             target = knownFires.Values
//                 .OrderBy(f => Dist(unit.Position, f.Position))
//                 .ThenBy(f => f.Hp)
//                 .FirstOrDefault();
//         }
//         else if (isDrone)
//         {
//             // Drone should mainly scout.
//             // Only attack fires nearby so it does not waste scouting time/water.
//             target = knownFires.Values
//                 .Where(f => Dist(unit.Position, f.Position) <= 10)
//                 .OrderBy(f => Dist(unit.Position, f.Position))
//                 .FirstOrDefault();
//         }
//     }

//     if (target != null)
//     {
//         if (Dist(unit.Position, target.Position) <= 1)
//             return "ExtinguishFire";

//         return MoveToward(unit.Position, target.Position);
//     }

//     // 4. No fire target: explore.
//     return Explore(unit);
// }

// string Explore(UnitState unit)
// {
//     bool isDrone = unit.UnitType.Contains("copter", StringComparison.OrdinalIgnoreCase);
//     bool isTruck = unit.UnitType.Contains("truck", StringComparison.OrdinalIgnoreCase);

//     string[] dronePattern =
//     {
//         "Up","Up","Up","Up","Up","Up","Up","Up",
//         "Right","Right","Right","Right","Right","Right",
//         "Down","Down","Down","Down","Down","Down","Down","Down",
//         "Right","Right","Right","Right","Right","Right",
//         "Up","Up","Up","Up","Up","Up","Up","Up",
//         "Left","Left","Left","Left","Left","Left","Left","Left","Left","Left"
//     };

//     // Truck: stay closer but still sweep.
//     string[] truckPattern =
//     {
//         "Right","Right","Right","Right",
//         "Up","Up","Up",
//         "Left","Left","Left","Left",
//         "Up","Up","Up",
//         "Right","Right","Right","Right",
//         "Down","Down"
//     };

//     string[] fighterPattern =
//     {
//         "Left","Left","Left","Left",
//         "Up","Up","Up",
//         "Right","Right","Right","Right",
//         "Up","Up","Up",
//         "Left","Left","Left","Left",
//         "Down","Down"
//     };

//     if (isDrone)
//         return dronePattern[tick % dronePattern.Length];

//     if (isTruck)
//         return truckPattern[tick % truckPattern.Length];

//     return fighterPattern[tick % fighterPattern.Length];
// }

// static string MoveToward(Pos from, Pos to)
// {
//     int dx = to.X - from.X;
//     int dy = to.Y - from.Y;

//     // Prefer the larger distance axis.
//     if (Math.Abs(dx) >= Math.Abs(dy))
//     {
//         if (dx > 0) return "Right";
//         if (dx < 0) return "Left";
//     }

//     if (dy > 0) return "Down";
//     if (dy < 0) return "Up";

//     return "ExtinguishFire";
// }

// static int Dist(Pos a, Pos b)
// {
//     return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
// }

// static string Key(int x, int y)
// {
//     return $"{x},{y}";
// }

// void CleanupOldMemory()
// {
//     // Fires disappear after being extinguished. If we have not seen them for a while, forget them.
//     var oldFires = knownFires
//         .Where(kv => tick - kv.Value.LastSeenTick > 120)
//         .Select(kv => kv.Key)
//         .ToList();

//     foreach (var key in oldFires)
//         knownFires.Remove(key);

//     var oldWaters = knownWaters
//         .Where(kv => tick - kv.Value.LastSeenTick > 400)
//         .Select(kv => kv.Key)
//         .ToList();

//     foreach (var key in oldWaters)
//         knownWaters.Remove(key);
// }

// void DrawStatus()
// {
//     Console.WriteLine();
//     Console.WriteLine("===== STATUS =====");
//     Console.WriteLine($"Time: {DateTime.Now:HH:mm:ss}");
//     Console.WriteLine($"Tick: {tick}");
//     Console.WriteLine($"Known fires: {knownFires.Count}");
//     Console.WriteLine($"Known waters: {knownWaters.Count}");
//     Console.WriteLine();

//     foreach (var u in latestUnits)
//     {
//         string nextMove = Decide(u);

//         Console.WriteLine(
//             $"{u.Id} {u.UnitType} pos=({u.Position.X},{u.Position.Y}) " +
//             $"water={u.CurrentWaterLevel} hp={u.CurrentHP} " +
//             $"seenFires={u.SeenFires.Count} seenWaters={u.SeenWaters.Count} " +
//             $"next={nextMove}"
//         );
//     }

//     Console.WriteLine();

//     foreach (var f in knownFires.Values.OrderBy(f => f.Hp).ThenBy(f => f.LastSeenTick).Take(10))
//     {
//         Console.WriteLine($"FIRE ({f.Position.X},{f.Position.Y}) HP={f.Hp} lastSeen={f.LastSeenTick}");
//     }

//     Console.WriteLine();

//     foreach (var w in knownWaters.Values.Take(5))
//     {
//         Console.WriteLine($"WATER ({w.Position.X},{w.Position.Y}) lastSeen={w.LastSeenTick}");
//     }
// }

// // Flexible JSON parser.
// // This avoids the common problem where fire/water positions parse as (0,0)
// // because their shape differs from your C# class.
// static List<UnitState> ParseUnits(string json)
// {
//     var result = new List<UnitState>();

//     using var doc = JsonDocument.Parse(json);

//     foreach (var unitEl in doc.RootElement.EnumerateArray())
//     {
//         var unit = new UnitState
//         {
//             Id = GetInt(unitEl, "Id"),
//             UnitType = GetString(unitEl, "UnitType"),
//             Owner = GetString(unitEl, "Owner"),
//             Position = ExtractPosition(unitEl.GetProperty("Position")),
//             CurrentWaterLevel = GetInt(unitEl, "CurrentWaterLevel"),
//             CurrentHP = GetInt(unitEl, "CurrentHP")
//         };

//         if (unitEl.TryGetProperty("SeenFires", out var firesEl) && firesEl.ValueKind == JsonValueKind.Array)
//         {
//             foreach (var fireEl in firesEl.EnumerateArray())
//             {
//                 var pos = ExtractPositionFlexible(fireEl);
//                 pos.Hp = ExtractHpFlexible(fireEl);
//                 unit.SeenFires.Add(pos);
//             }
//         }

//         if (unitEl.TryGetProperty("SeenWaters", out var watersEl) && watersEl.ValueKind == JsonValueKind.Array)
//         {
//             foreach (var waterEl in watersEl.EnumerateArray())
//             {
//                 var pos = ExtractPositionFlexible(waterEl);
//                 unit.SeenWaters.Add(pos);
//             }
//         }

//         result.Add(unit);
//     }

//     return result;
// }

// static Pos ExtractPositionFlexible(JsonElement el)
// {
//     // Case 1:
//     // { "Position": { "X": 1, "Y": 2 } }
//     if (el.TryGetProperty("Position", out var posEl))
//         return ExtractPosition(posEl);

//     // Case 2:
//     // { "Cell": { "X": 1, "Y": 2 } }
//     if (el.TryGetProperty("Cell", out var cellEl))
//         return ExtractPosition(cellEl);

//     // Case 3:
//     // { "Location": { "X": 1, "Y": 2 } }
//     if (el.TryGetProperty("Location", out var locEl))
//         return ExtractPosition(locEl);

//     // Case 4:
//     // { "X": 1, "Y": 2 }
//     return ExtractPosition(el);
// }

// static Pos ExtractPosition(JsonElement el)
// {
//     return new Pos
//     {
//         IsEmpty = GetBool(el, "IsEmpty"),
//         X = GetInt(el, "X"),
//         Y = GetInt(el, "Y")
//     };
// }

// static int ExtractHpFlexible(JsonElement el)
// {
//     string[] possibleNames =
//     {
//         "CurrentHP",
//         "CurrentHp",
//         "HP",
//         "Hp",
//         "Health",
//         "health"
//     };

//     foreach (var name in possibleNames)
//     {
//         if (el.TryGetProperty(name, out var hpEl) && hpEl.ValueKind == JsonValueKind.Number)
//             return hpEl.GetInt32();
//     }

//     return 1000;
// }

// static int GetInt(JsonElement el, string name)
// {
//     if (el.TryGetProperty(name, out var v))
//     {
//         if (v.ValueKind == JsonValueKind.Number)
//             return v.GetInt32();

//         if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int parsed))
//             return parsed;
//     }

//     return 0;
// }

// static bool GetBool(JsonElement el, string name)
// {
//     if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True)
//         return true;

//     return false;
// }

// static string GetString(JsonElement el, string name)
// {
//     if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
//         return v.GetString() ?? "";

//     return "";
// }

// class UnitState
// {
//     public int Id { get; set; }
//     public string UnitType { get; set; } = "";
//     public string Owner { get; set; } = "";
//     public Pos Position { get; set; } = new();
//     public List<Pos> SeenWaters { get; set; } = new();
//     public List<Pos> SeenFires { get; set; } = new();
//     public int CurrentWaterLevel { get; set; }
//     public int CurrentHP { get; set; }
// }

// class Pos
// {
//     public bool IsEmpty { get; set; }
//     public int X { get; set; }
//     public int Y { get; set; }

//     // Used for fire HP when this Pos represents a fire.
//     public int Hp { get; set; } = 1000;
// }

// class KnownFire
// {
//     public Pos Position { get; set; } = new();
//     public int Hp { get; set; } = 1000;
//     public int LastSeenTick { get; set; }
// }

// class KnownWater
// {
//     public Pos Position { get; set; } = new();
//     public int LastSeenTick { get; set; }
// }