using System;
using System.Collections.Generic;
using System.Linq;

public class GameState
{
    public object StateLock { get; } = new();
    public List<UnitData> LatestUnits { get; set; } = new();
    public Dictionary<string, KnownFire>  KnownFires   { get; } = new();
    public Dictionary<string, KnownWater> KnownWaters  { get; } = new();
    public Dictionary<int, UnitState>     ActiveUnitStates { get; } = new();
    public Dictionary<int, UnitPlan>      UnitPlans    { get; } = new();

    // Heatmap: last tick each cell was inside any unit's sight cone
    private readonly int[,] _heatmap = new int[350, 350];

    // Temporary cell blocks with expiry tick
    private readonly Dictionary<string, int> _blockedCells = new();

    // Border tracking (for dynamic map bounds)
    public int MinX { get; set; } = int.MaxValue;
    public int MaxX { get; set; } = int.MinValue;
    public int MinY { get; set; } = int.MaxValue;
    public int MaxY { get; set; } = int.MinValue;

    // Absolute map boundaries discovered by the Drone
    public int HardMinX { get; set; } = 0;
    public int HardMaxX { get; set; } = 349;
    public int HardMinY { get; set; } = 0;
    public int HardMaxY { get; set; } = 349;

    // Target reservation: coordinate → unit ID
    private readonly Dictionary<string, int> _targetReservations = new();

    // Fire claiming: fire key → owner unit ID
    private readonly Dictionary<string, int> _fireClaims = new();

    // SnapshotVersion: incremented every time we receive a real UnitsFromServer.
    // The main loop uses this to avoid commanding on stale data.
    public long SnapshotVersion { get; set; } = 0;

    // Tick is now incremented in ProcessUnitsFromServer, NOT in the main loop.
    public int Tick { get; set; } = 0;

    // Legacy field — kept for dashboard compatibility
    public KnownFire? GlobalFireTarget { get; set; }

    // ─── Called from the read task on every UnitsFromServer ──────────────
    /// <summary>
    /// Central update: increments Tick, stores units, records sightings,
    /// verifies fires, and cleans stale memory.
    /// Must be called inside lock(StateLock).
    /// </summary>
    public void ProcessUnitsFromServer(List<UnitData> parsedUnits)
    {
        Tick++;
        SnapshotVersion++;
        LatestUnits = parsedUnits;

        foreach (var unit in parsedUnits)
        {
            RecordSighting(unit.Position.X, unit.Position.Y);

            foreach (var fire in unit.SeenFires)
            {
                if (fire.IsEmpty) continue;
                string key = Key(fire.X, fire.Y);
                KnownFires[key] = new KnownFire
                {
                    Position     = fire,
                    Hp           = fire.Hp,
                    LastSeenTick = Tick
                };
                RecordSighting(fire.X, fire.Y);
            }

            foreach (var water in unit.SeenWaters)
            {
                if (water.IsEmpty) continue;
                string key = Key(water.X, water.Y);
                KnownWaters[key] = new KnownWater
                {
                    Position     = water,
                    LastSeenTick = Tick
                };
                RecordSighting(water.X, water.Y);
            }
        }

        VerifyFires(parsedUnits);
        CleanupStaleFires(Tick);
    }

    // ─── Border ───────────────────────────────────────────────────────────
    public void UpdateBorder(int x, int y)
    {
        MinX = Math.Min(MinX, x);
        MaxX = Math.Max(MaxX, x);
        MinY = Math.Min(MinY, y);
        MaxY = Math.Max(MaxY, y);
    }

    // ─── Heatmap ──────────────────────────────────────────────────────────
    public void RecordSighting(int x, int y)
    {
        UpdateBorder(x, y);
        if (x >= 0 && y >= 0 && x < 350 && y < 350)
            _heatmap[x, y] = Tick;
    }

    /// <summary>
    /// Mark every cell inside the diamond sight cone as seen this tick.
    /// </summary>
    public void RecordSightingArea(int cx, int cy, int sightRadius)
    {
        UpdateBorder(cx, cy);
        for (int dx = -sightRadius; dx <= sightRadius; dx++)
        {
            for (int dy = -sightRadius; dy <= sightRadius; dy++)
            {
                if (Math.Abs(dx) + Math.Abs(dy) <= sightRadius)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && y >= 0 && x < 350 && y < 350)
                        _heatmap[x, y] = Tick;
                }
            }
        }
    }

    public int GetLastSeenTick(int x, int y)
    {
        if (x >= 0 && y >= 0 && x < 350 && y < 350) return _heatmap[x, y];
        return -1;
    }

    public int GetColdness(int x, int y)
    {
        int lastSeen = GetLastSeenTick(x, y);
        if (lastSeen == 0) return 1_000_000; // never seen
        return Tick - lastSeen;
    }

    // ─── Cell Blocks ──────────────────────────────────────────────────────
    public void BlockCell(int x, int y, int duration = 10)
    {
        string k = Key(x, y);
        // Never extend beyond what's already there — use max to keep the longer block
        int newExpiry = Tick + duration;
        if (!_blockedCells.TryGetValue(k, out int existing) || newExpiry > existing)
            _blockedCells[k] = newExpiry;
    }

    public bool IsCellBlocked(int x, int y)
    {
        return _blockedCells.TryGetValue(Key(x, y), out int expiry) && Tick < expiry;
    }

    /// <summary>
    /// Debugging helper: is the cell reached by the given direction blocked?
    /// </summary>
    public bool IsBlockedForDebug(Pos from, string op)
    {
        var (x, y) = op switch
        {
            "Up"    => (from.X,     from.Y - 1),
            "Down"  => (from.X,     from.Y + 1),
            "Left"  => (from.X - 1, from.Y),
            "Right" => (from.X + 1, from.Y),
            _       => (from.X,     from.Y)
        };
        return IsCellBlocked(x, y);
    }

    // ─── Exploration ──────────────────────────────────────────────────────
    /// <summary>
    /// Find the coldest (least-recently-seen) cell within an optional
    /// Y-band. Passing null for sectorMinY/sectorMaxY searches the full map.
    /// </summary>
    public Pos GetColdestCell(Pos center, int unitId,
                               int? sectorMinY = null, int? sectorMaxY = null)
    {
        int searchMaxX = Math.Max(260, MaxX == int.MinValue ? 260 : MaxX + 20);
        int searchMaxY = Math.Max(260, MaxY == int.MinValue ? 260 : MaxY + 20);

        int sMinY = sectorMinY ?? 2;
        int sMaxY = Math.Min(sectorMaxY ?? searchMaxY - 2, searchMaxY - 2);

        Pos bestCell    = center;
        int maxColdness = -1;
        int minDist     = int.MaxValue;

        for (int x = 2; x < searchMaxX; x += 2)
        {
            for (int y = sMinY; y < sMaxY; y += 2)
            {
                if (x == center.X && y == center.Y) continue;
                if (IsTargetReserved(x, y, unitId)) continue;
                if (IsCellBlocked(x, y)) continue;

                int coldness = GetColdness(x, y);
                int dist     = Dist(center, new Pos { X = x, Y = y });

                if (coldness > maxColdness ||
                    (coldness == maxColdness && dist < minDist))
                {
                    maxColdness = coldness;
                    minDist     = dist;
                    bestCell    = new Pos { X = x, Y = y };
                }
            }
        }

        // Fallback: nudge away from current position if sector fully warm
        if (bestCell.X == center.X && bestCell.Y == center.Y)
        {
            int mid = (sMinY + sMaxY) / 2;
            bestCell = new Pos
            {
                X = Math.Clamp(center.X + (Tick % 2 == 0 ? 30 : -30), 5, searchMaxX - 5),
                Y = Math.Clamp(mid,                                     sMinY, sMaxY)
            };
        }

        return bestCell;
    }

    // ─── Target Reservation ───────────────────────────────────────────────
    public void ReserveTarget(int x, int y, int unitId)
        => _targetReservations[Key(x, y)] = unitId;

    public void UnreserveTarget(int x, int y)
        => _targetReservations.Remove(Key(x, y));

    public bool IsTargetReserved(int x, int y, int unitId)
        => _targetReservations.TryGetValue(Key(x, y), out var id) && id != unitId;

    // ─── Fire Claiming ────────────────────────────────────────────────────
    public bool TryClaimFire(string fireKey, int unitId)
    {
        if (_fireClaims.TryGetValue(fireKey, out var owner) && owner != unitId)
            return false;
        _fireClaims[fireKey] = unitId;
        return true;
    }

    public void ReleaseFire(string fireKey)
        => _fireClaims.Remove(fireKey);

    public void ReleaseAllClaims(int unitId)
    {
        var keys = _fireClaims.Where(kv => kv.Value == unitId)
                               .Select(kv => kv.Key).ToList();
        foreach (var k in keys) _fireClaims.Remove(k);
    }

    public bool IsFireClaimedByOther(string fireKey, int unitId)
        => _fireClaims.TryGetValue(fireKey, out var owner) && owner != unitId;

    // ─── Fire Verification ────────────────────────────────────────────────
    /// <summary>
    /// Remove fires that are currently visible to a unit but no longer present.
    /// </summary>
   public void VerifyFires(List<UnitData> currentUnits)
    {
        var toRemove = new List<string>();

        foreach (var kvp in KnownFires)
        {
            var fire = kvp.Value;

            // Remove if HP ever dropped to zero
            if (fire.Hp <= 0)
            {
                toRemove.Add(kvp.Key);
                continue;
            }

            bool isConfirmedDead = false;
            bool isActuallySeen = false;

            foreach (var unit in currentUnits)
            {
                // Did the server explicitly send us this fire for this unit?
                var seenFire = unit.SeenFires.FirstOrDefault(f => f.X == fire.Position.X && f.Y == fire.Position.Y);
                
                if (seenFire != null && seenFire.Hp > 0)
                {
                    isActuallySeen = true;
                    // Update HP and reset the clock
                    fire.Hp = seenFire.Hp;
                    fire.LastSeenTick = Tick;
                    break; // We found it, stop checking other units
                }

                // THE FIX: Only trust a unit to declare a fire "dead" if it is within 3 tiles.
                // This prevents distant drones from accidentally erasing fires due to vision-shape differences.
                if (Dist(unit.Position, fire.Position) <= 2 && seenFire == null)
                {
                    isConfirmedDead = true;
                }
            }

            // Only remove the fire if nobody saw it this tick AND someone stood right next to it and proved it was gone
            if (!isActuallySeen && isConfirmedDead)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            KnownFires.Remove(key);
            ReleaseFire(key);
        }
    }
    /// <summary>
    /// Remove fires not seen in the last staleAfterTicks ticks.
    /// Prevents ghost-fire accumulation on unexplored tiles.
    /// </summary>
    public void CleanupStaleFires(int currentTick, int staleAfterTicks = 300)
{
    // DO NOTHING.
    // Fire database is permanent.
    // Fires are removed only when HP <= 0.
}

    // ─── Bounds ───────────────────────────────────────────────────────────
    public bool IsOutOfBounds(int x, int y)
    {
        const int margin = 0; // FIX 1: Changed from 1 to 0 so units don't get trapped on the 0-edge
        int maxX = Math.Max(420, MaxX == int.MinValue ? 420 : MaxX + 40);
        int maxY = Math.Max(420, MaxY == int.MinValue ? 420 : MaxY + 40);

        return x < margin || y < margin || x > maxX - margin || y > maxY - margin;
    }

    // ─── Water ────────────────────────────────────────────────────────────
    public Pos? GetClosestWater(Pos unitPos)
    {
        if (KnownWaters.Count == 0) return null;
        return KnownWaters.Values
            .OrderBy(w => Dist(unitPos, w.Position))
            .FirstOrDefault()?.Position;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────
    public static string Key(int x, int y) => $"{x},{y}";
    public static int Dist(Pos a, Pos b)   => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    public void CleanupOldMemory()
    {
        // Heatmap is a flat array — no cleanup needed.
        // Expired blocks are lazily ignored by IsCellBlocked.
    }
}