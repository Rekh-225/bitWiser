using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// AI brain for the 3-unit firefighting team.
///
/// Patches applied in this version:
///
///   PATCH 3 (CRITICAL) — Truck/ground-unit refill decision fix:
///            IsOccupied() returns true for water tiles for ground units,
///            so !IsOccupied() was always false — the truck NEVER called
///            RefillWithWater. Fixed by using IsTeammateOn() at the refill
///            decision point in all three locations.
///
///   PATCH 3b (CRITICAL) — A* water-tile pathfinding fix:
///            The same IsOccupied bug blocked the A* pathfinder from routing
///            ground units TO water tiles (or through a multi-cell lake).
///            Additionally, DetectWalls was incorrectly flagging water tiles
///            as permanent map walls. Both fixed via:
///              - New IsEffectivelyOccupied() that treats water as walkable
///                during water-seeking pathfinding (allowWaterEntry flag).
///              - DetectWalls now skips recording water tiles as walls.
///
///   PATCH 6 — Don't forget water if teammate is blocking and it is the
///            only known water source. Only remove + redirect if alternatives exist.
///
///   Unchanged from A1:
///   FIX 1 — Water bouncing: verify tile is unoccupied before RefillWithWater;
///            if stuck 8+ ticks at water, forget that source and find another.
///   FIX 2 — Cluster-aware fire scoring: nearby fires add a bonus so a dense
///            cluster beats a lone fire even if slightly farther away.
///   FIX 3 — Path blocked by teammate: after 5 stuck ticks while pursuing,
///            release the claim and find a fresh target.
///   FIX 4 — Drone/truck back-and-forth: top-off only when critically low
///            AND water is very close AND no visible fire is in attack range.
///            Units never abandon a fire unless the tank is truly empty.
/// </summary>
public class BotAI
{
    private readonly GameState _state;

    private const int FireAttackRange    = 1;
    private const int ClaimStaleTicks    = 60;
    private const int StuckThreshold     = 8;
    private const int BlockedCellTicks   = 30;

private const int DroneOnWayRefill   = 0;
private const int TruckOnWayRefill   = 4;
private const int OnWayRefillMaxDist = 25;

private const int DroneCriticalWater = 0;
private const int TruckCriticalWater = 3;

    private const int DroneSectorMinY    = 2;
    private const int DroneSectorMaxY    = 85;
    private const int TruckSectorMinY    = 85;
    private const int TruckSectorMaxY    = 170;
    private const int FighterSectorMinY  = 170;
    private const int FighterSectorMaxY  = 260;


    // Hard critical: if water is this low, refill even if fire exists.

    public BotAI(GameState state) { _state = state; }

    public string Decide(UnitData unit) => DecideDetailed(unit).Operation;

    // ═══════════════════════════════════════════════════════════════════════
    //  MAIN ENTRY
    // ═══════════════════════════════════════════════════════════════════════
    public UnitDecision DecideDetailed(UnitData unit)
    {
        bool isDrone   = IsDroneUnit(unit.UnitType);
        bool isTruck   = unit.UnitType.Contains("truck",   StringComparison.OrdinalIgnoreCase);
        bool isFighter = unit.UnitType.Contains("fighter", StringComparison.OrdinalIgnoreCase);

        var plan = GetOrCreatePlan(unit);

        DetectWalls(unit, plan);

        int sight = isDrone ? 16 : isTruck ? 8 : 2;
        _state.RecordSightingArea(unit.Position.X, unit.Position.Y, sight);

        UpdateStuck(unit, plan);

        var state = PickState(unit, plan, isDrone, isTruck, isFighter);
        _state.ActiveUnitStates[unit.Id] = state;

        var d = Execute(unit, state, plan, isDrone, isTruck, isFighter);

        // Stuck rescue: force a random valid move
        if (d.Operation == "NOP" && plan.StuckTicks > 3)
        {
            var moves = new[] { "Up", "Down", "Left", "Right" };
            var rng   = new Random(HashCode.Combine(unit.Id, _state.Tick));
            foreach (var m in moves.OrderBy(_ => rng.Next()))
            {
                var (nx, ny) = MoveTarget(unit.Position, m);
                if (InBounds(nx, ny) &&
                    !_state.IsOutOfBounds(nx, ny) &&
                    !IsCellTemporarilyBlocked(nx, ny, allowWaterEntry: false) &&
                    !IsOccupied(nx, ny, unit))
                {
                    d.Operation = m;
                    d.Intent    = "Stuck rescue";
                    break;
                }
            }
        }

        plan.LastOperation = d.Operation;
        return d;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WALL DETECTION
    //
    //  PATCH 3b fix: skip water tiles. Before this fix, every time the truck
    //  tried to step onto a water tile and "failed" (because IsOccupied=true
    //  made A* route it adjacent instead of onto the tile), DetectWalls would
    //  see "didn't move after issuing Up/Right/etc." and permanently block the
    //  cell — even though it's a valid water source, not a wall.
    // ═══════════════════════════════════════════════════════════════════════
    private void DetectWalls(UnitData unit, UnitPlan plan)
{
    bool didNotMove = unit.Position.X == plan.LastPosition.X &&
                      unit.Position.Y == plan.LastPosition.Y;

    if (!didNotMove || !IsMoveOp(plan.LastOperation)) return;

    // Important:
    // Do not mark wall after one failed tick.
    // Movement/server updates can delay position changes.
    if (plan.StuckTicks < 4) return;

    var (wx, wy) = MoveTarget(plan.LastPosition, plan.LastOperation);
    string key = GameState.Key(wx, wy);

    bool hitFire = _state.KnownFires.ContainsKey(key);
    bool hitWater = _state.KnownWaters.ContainsKey(key);
    bool hitUnit = _state.LatestUnits.Any(u => u.Id != unit.Id &&
                                               u.Position.X == wx &&
                                               u.Position.Y == wy);

    // Do not block fire/water/unit cells as walls.
    // They are dynamic or special targets.
    if (hitFire || hitWater || hitUnit)
        return;

    int blockUntil = _state.Tick + BlockedCellTicks;
    switch (plan.LastOperation)
    {
        case "Up":
            plan.BlockUpUntil = blockUntil;
            break;
        case "Down":
            plan.BlockDownUntil = blockUntil;
            break;
        case "Left":
            plan.BlockLeftUntil = blockUntil;
            break;
        case "Right":
            plan.BlockRightUntil = blockUntil;
            break;
    }

    _state.BlockCell(wx, wy, duration: BlockedCellTicks);
}

    // ═══════════════════════════════════════════════════════════════════════
    //  STATE SELECTION
    // ═══════════════════════════════════════════════════════════════════════
    private UnitState PickState(UnitData unit, UnitPlan plan,
                            bool isDrone, bool isTruck, bool isFighter)
{
    bool hasVisibleFire = unit.SeenFires.Any(f => !f.IsEmpty && f.Hp > 0);
    bool hasKnownFire   = _state.KnownFires.Values.Any(f => f.Hp > 0);
    bool hasAnyFire     = hasVisibleFire || hasKnownFire;




    // ___________________________________________ DIRECT TAKE WATER 
if (!isFighter && _state.KnownWaters.Count > 0)
{
    int maxCap = isDrone ? 5 : isTruck ? 20 : 100;
    bool tankFull = unit.CurrentWaterLevel >= maxCap;

    bool besideFire = unit.SeenFires.Any(f =>
        !f.IsEmpty && f.Hp > 0 &&
        GameState.Dist(unit.Position, f) <= FireAttackRange);

    // IMPORTANT:
    // If fire is nearby and we still have water, use ALL remaining water first.
    // Do not refill at water=2 or water=1 while standing beside fire.
    if (besideFire && unit.CurrentWaterLevel > 0)
        return UnitState.Extinguishing;

    // If completely empty, go refill.
    if (unit.CurrentWaterLevel <= 0)
        return UnitState.SeekingWater;

    // If already committed to water, keep going until full,
    // unless a fire is directly beside us, handled above.
    if (_state.ActiveUnitStates.TryGetValue(unit.Id, out var lastState))
    {
        if (lastState == UnitState.SeekingWater && !tankFull)
            return UnitState.SeekingWater;
    }

    // Start seeking water when low, but NOT if beside fire.
    int criticalThreshold = isDrone ? 0: isTruck ? 0  : 0;

    if (unit.CurrentWaterLevel <= criticalThreshold)
        return UnitState.SeekingWater;
}
    // ─────────────────────────────────────────────
    // FIREFIGHTER
    // Firefighter has infinite water.
    // Fire exists    => fight/pursue.
    // No fire exists => explore undiscovered areas.
    // ─────────────────────────────────────────────
    if (isFighter)
    {
        if (unit.SeenFires.Any(f => !f.IsEmpty && f.Hp > 0 &&
                                    GameState.Dist(unit.Position, f) <= FireAttackRange))
            return UnitState.Extinguishing;

        if (hasAnyFire)
            return UnitState.Pursuing;

        return UnitState.Seeking;
    }

    // ─────────────────────────────────────────────
    // TRUCK / DRONE WATER SAFETY
    // They cannot fight with 0 water.
    // ─────────────────────────────────────────────
    if (unit.CurrentWaterLevel <= 0)
        return UnitState.SeekingWater;

    int maxWater = isDrone ? 5 : isTruck ? 20 : 100;
    bool isFull = unit.CurrentWaterLevel >= maxWater;

    int criticalWater = isDrone ? DroneCriticalWater : TruckCriticalWater;
    int refillWater   = isDrone ? DroneOnWayRefill   : TruckOnWayRefill;

    bool knowsWater = _state.KnownWaters.Count > 0;

    // ─────────────────────────────────────────────
    // HARD LOW-WATER RULE
    // This fixes:
    // Truck water=1 but still pursuing fire.
    // If truck/drone is critically low and knows water, refill.
    // Exception: if already adjacent to fire, allow one extinguish action.
    // ─────────────────────────────────────────────
    bool fireAdjacent = unit.SeenFires.Any(f =>
        !f.IsEmpty && f.Hp > 0 &&
        GameState.Dist(unit.Position, f) <= FireAttackRange);

    if (knowsWater && unit.CurrentWaterLevel <= criticalWater && !fireAdjacent)
        return UnitState.SeekingWater;

    // ─────────────────────────────────────────────
    // NO FIRE: top off instead of patrolling.
    // This fixes truck with low water wandering while water is known.
    // ─────────────────────────────────────────────
    if (!hasAnyFire && !isFull && knowsWater)
        return UnitState.SeekingWater;

    // ─────────────────────────────────────────────
    // LOW WATER WITH FIRE:
    // Refill if water is reasonably close/on the way.
    // ─────────────────────────────────────────────
    if (!isFull && knowsWater && unit.CurrentWaterLevel <= refillWater && !fireAdjacent)
    {
        var closestWater = _state.GetClosestWater(unit.Position);
        if (closestWater != null)
        {
            int dw = GameState.Dist(unit.Position, closestWater);

            int df = int.MaxValue;

            if (hasVisibleFire)
            {
                df = unit.SeenFires
                    .Where(f => !f.IsEmpty && f.Hp > 0)
                    .Min(f => GameState.Dist(unit.Position, f));
            }
            else if (hasKnownFire)
            {
                df = _state.KnownFires.Values
                    .Where(f => f.Hp > 0)
                    .Min(f => GameState.Dist(unit.Position, f.Position));
            }

            if (dw <= OnWayRefillMaxDist || dw <= df + 8)
                return UnitState.SeekingWater;
        }
    }

    // ─────────────────────────────────────────────
    // FIRE LOGIC
    // At this point truck/drone have enough water to fight.
    // ─────────────────────────────────────────────
    if (fireAdjacent)
        return UnitState.Extinguishing;

    if (hasVisibleFire)
        return UnitState.Pursuing;

    if (hasKnownFire)
        return UnitState.Pursuing;

    // ─────────────────────────────────────────────
    // DEFAULT
    // ─────────────────────────────────────────────
    if (isDrone) return UnitState.Scouting;
    if (isTruck) return UnitState.Seeking;

    return UnitState.Seeking;
}
    // ═══════════════════════════════════════════════════════════════════════
    //  STATE EXECUTION
    // ═══════════════════════════════════════════════════════════════════════
    private UnitDecision Execute(UnitData unit, UnitState state, UnitPlan plan,
                                  bool isDrone, bool isTruck, bool isFighter)
    {
        var d = new UnitDecision { UnitId = unit.Id, State = state };

        switch (state)
        {
            // ── SEEK WATER ────────────────────────────────────────────────
            // ── SEEK WATER ────────────────────────────────────────────────
case UnitState.SeekingWater:
{
    var water = FindReachableWater(unit);
    if (water == null)
        return DoExplore(unit, plan, isDrone, isTruck, isFighter, d, "Water-search");

    int dist = GameState.Dist(unit.Position, water);
    d.Target = water;
    d.TargetDistance = dist;

    // 1. If water is refillable now, refill immediately.
    if (dist <= 1 && !IsTeammateOn(water.X, water.Y, unit))
{
    d.Operation = "RefillWithWater";
    d.Intent = $"FAST REFILL at ({water.X},{water.Y})";
    return d;
}

    // 2. If chosen water tile is blocked by teammate, do NOT wait.
    // Just move around and try again next tick.
    if (dist <= 1 && IsTeammateOn(water.X, water.Y, unit))
    {
        d.Operation = RandomFreeMove(unit, allowWaterEntry: true);
        d.Intent = $"Water occupied at ({water.X},{water.Y}), circling once";
        return d;
    }

    // 3. Move directly toward water. No extra thinking.
    d.Operation = MoveToward(unit, plan, water, allowWaterEntry: true);

    // 4. If pathfinder fails, try one simple random legal move.
    if (d.Operation == "NOP")
    {
        d.Operation = RandomFreeMove(unit, allowWaterEntry: true);
        d.Intent = $"Path to water blocked, trying another step near ({water.X},{water.Y})";
        return d;
    }

    d.Intent = $"DIRECT → water ({water.X},{water.Y}) d={dist}";
    return d;
}

            // ── EXTINGUISH ────────────────────────────────────────────────
            case UnitState.Extinguishing:
            {
                if (!isFighter && unit.CurrentWaterLevel <= 0)
    return Execute(unit, UnitState.SeekingWater, plan, isDrone, isTruck, isFighter);
                var fire = unit.SeenFires
                    .Where(f => !f.IsEmpty && f.Hp > 0 &&
                                GameState.Dist(unit.Position, f) <= FireAttackRange)
                    .OrderBy(f => f.Hp)
                    .FirstOrDefault();

                if (fire == null)
                    return DoExplore(unit, plan, isDrone, isTruck, isFighter, d, "Lost fire");

                d.Operation      = "ExtinguishFire";
                d.Target         = fire;
                d.TargetDistance = GameState.Dist(unit.Position, fire);
                d.TargetHp       = fire.Hp;
                d.Intent         = $"EXTINGUISH ({fire.X},{fire.Y}) hp={fire.Hp}";
                return d;
            }

            // ── PURSUE FIRE ───────────────────────────────────────────────
            case UnitState.Pursuing:
            {
                if (!isFighter && unit.CurrentWaterLevel <= 0)
    return Execute(unit, UnitState.SeekingWater, plan, isDrone, isTruck, isFighter);
                var visibleFire = unit.SeenFires
                    .Where(f => !f.IsEmpty && f.Hp > 0)
                    .OrderBy(f => GameState.Dist(unit.Position, f))
                    .ThenBy(f => f.Hp)
                    .FirstOrDefault();

                if (visibleFire != null)
{
    int vfd = GameState.Dist(unit.Position, visibleFire);
    d.Target = visibleFire;
    d.TargetDistance = vfd;
    d.TargetHp = visibleFire.Hp;

    // ABSOLUTE PRIORITY:
    // If fire is already shootable and we have water, shoot.
    // Do not think about refill, path, wall, or anything else.
    if (vfd <= FireAttackRange && (isFighter || unit.CurrentWaterLevel > 0))
    {
        d.Operation = "ExtinguishFire";
        d.Intent = $"SHOOT FIRST visible fire ({visibleFire.X},{visibleFire.Y}) hp={visibleFire.Hp}";
        return d;
    }

    // Only refill if fire is NOT currently shootable.
    var refillPos = ShouldRefillOnTheWay(unit, visibleFire, isDrone, isTruck);
    if (refillPos != null)
    {
        int distToWater = GameState.Dist(unit.Position, refillPos);

        if (distToWater <= 1 && !IsTeammateOn(refillPos.X, refillPos.Y, unit))
        {
            d.Operation = "RefillWithWater";
            d.Intent = $"Quick refill en route to ({visibleFire.X},{visibleFire.Y})";
            return d;
        }

        d.Operation = MoveToward(unit, plan, refillPos, allowWaterEntry: true);
        d.Intent = $"→ refill ({refillPos.X},{refillPos.Y}) d={distToWater} then fire";
        return d;
    }

    if (vfd <= 3)
    {
        var attackTile = GetClosestAttackTile(unit, visibleFire);

        d.Target = attackTile;
        d.TargetDistance = GameState.Dist(unit.Position, attackTile);
        d.Operation = MoveToward(unit, plan, attackTile);
        d.Intent = $"AGGRESSIVE visible attack tile → ({attackTile.X},{attackTile.Y}) for fire ({visibleFire.X},{visibleFire.Y})";
        return d;
    }

    var approach = GetFireApproachTarget(unit, visibleFire);

    d.Target = approach;
    d.TargetDistance = GameState.Dist(unit.Position, approach);
    d.Operation = MoveToward(unit, plan, approach);
    d.Intent = $"→ visible fire approach ({approach.X},{approach.Y}) for fire ({visibleFire.X},{visibleFire.Y})";
    return d;
}

                var fire = PickFire(unit, plan, isDrone, isTruck, isFighter);
                if (fire == null)
                    return DoExplore(unit, plan, isDrone, isTruck, isFighter, d, "No remembered fire");

                int fd = GameState.Dist(unit.Position, fire.Position);
                d.Target = fire.Position; d.TargetDistance = fd; d.TargetHp = fire.Hp;

                if (plan.StuckTicks >= 6)
{
    ReleaseClaim(plan);
    plan.StuckTicks = 0;

    // If truck/drone is low, refill instead of fighting.
    if (!isFighter &&
        unit.CurrentWaterLevel <= (isDrone ? DroneOnWayRefill : TruckOnWayRefill) &&
        _state.KnownWaters.Count > 0)
    {
        return Execute(unit, UnitState.SeekingWater, plan, isDrone, isTruck, isFighter);
    }

    // Do NOT randomly explore. Try another free attack position.
    var retryApproach = GetFireApproachTarget(unit, fire.Position);

    d.Target = retryApproach;
    d.TargetDistance = GameState.Dist(unit.Position, retryApproach);
    d.Operation = MoveToward(unit, plan, retryApproach);
    d.Intent = $"Retry fire approach → ({retryApproach.X},{retryApproach.Y}) for fire ({fire.Position.X},{fire.Position.Y})";
    return d;
}
                if (fd <= FireAttackRange)
{
    d.Operation = "ExtinguishFire";
    d.Intent    = $"REMEMBERED IN RANGE ({fire.Position.X},{fire.Position.Y}) hp={fire.Hp}";
    return d;
}
                var refillPos2 = ShouldRefillOnTheWay(unit, fire.Position, isDrone, isTruck);
if (refillPos2 != null)
{
    int distToWater = GameState.Dist(unit.Position, refillPos2);
    
    // ── DIRECT REFILL when adjacent ──
    if (distToWater <= 1 && !IsTeammateOn(refillPos2.X, refillPos2.Y, unit))
    {
        d.Operation = "RefillWithWater";
        d.Intent    = $"Quick refill en route to ({fire.Position.X},{fire.Position.Y})";
        return d;
    }
    
    // ── MOVE TO WATER ──
    d.Operation = MoveToward(unit, plan, refillPos2, allowWaterEntry: true);
    d.Intent    = $"→ refill ({refillPos2.X},{refillPos2.Y}) d={distToWater} then fire";
    return d;
}



// AGGRESSIVE: if remembered fire is close, go directly to an attack tile.
// This prevents slow over-thinking around remembered fires.
if (fd <= 4)
{
    var attackTile = GetClosestAttackTile(unit, fire.Position);

    d.Target = attackTile;
    d.TargetDistance = GameState.Dist(unit.Position, attackTile);
    d.Operation = MoveToward(unit, plan, attackTile);
    d.Intent = $"AGGRESSIVE remembered attack tile → ({attackTile.X},{attackTile.Y}) for fire ({fire.Position.X},{fire.Position.Y})";
    return d;
}

var rememberedApproach = GetFireApproachTarget(unit, fire.Position);

d.Target = rememberedApproach;
d.TargetDistance = GameState.Dist(unit.Position, rememberedApproach);
d.Operation = MoveToward(unit, plan, rememberedApproach);
d.Intent = $"→ remembered fire approach ({rememberedApproach.X},{rememberedApproach.Y}) for fire ({fire.Position.X},{fire.Position.Y})";
return d;
            }

            // ── EXPLORE ───────────────────────────────────────────────────
            case UnitState.Scouting:
                return DoExplore(unit, plan, isDrone, isTruck, isFighter, d, "Scouting");

            case UnitState.Seeking:
                return DoExplore(unit, plan, isDrone, isTruck, isFighter, d, "Patrolling");

            // ── LEGACY SUPPORT STATE: explore instead ─────────────────────
            case UnitState.Supporting:
                return DoExplore(unit, plan, isDrone, isTruck, isFighter, d, "Exploring");
        }

        d.Operation = "NOP"; d.Intent = "Idle";
        return d;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EN-ROUTE REFILL CHECK
    // ═══════════════════════════════════════════════════════════════════════
    private Pos? ShouldRefillOnTheWay(UnitData unit, Pos fire, bool isDrone, bool isTruck)
{
    if (!isDrone && !isTruck) return null;
    if (_state.KnownWaters.Count == 0) return null;

    int maxWater = isDrone ? 5 : 20;
    int critical = isDrone ? DroneCriticalWater : TruckCriticalWater;
    int refill   = isDrone ? DroneOnWayRefill   : TruckOnWayRefill;

    if (unit.CurrentWaterLevel >= maxWater)
        return null;

    Pos? best = null;
    int bestScore = int.MaxValue;

    int distToFire = GameState.Dist(unit.Position, fire);

    foreach (var w in _state.KnownWaters.Values)
    {
        if (_state.IsOutOfBounds(w.Position.X, w.Position.Y)) continue;

        int dw = GameState.Dist(unit.Position, w.Position);
        int wf = GameState.Dist(w.Position, fire);

        // Critical water: go to nearest known water, even if not perfect.
        if (unit.CurrentWaterLevel <= critical)
        {
            if (dw < bestScore)
            {
                bestScore = dw;
                best = w.Position;
            }

            continue;
        }

        // Normal low water: water should be close or reasonably on the way.
        if (unit.CurrentWaterLevel <= refill)
        {
            int total = dw + wf;

            if (dw <= OnWayRefillMaxDist || total <= distToFire + 12)
            {
                if (total < bestScore)
                {
                    bestScore = total;
                    best = w.Position;
                }
            }
        }
    }

    return best;
}

    // ═══════════════════════════════════════════════════════════════════════
    //  EXPLORATION
    // ═══════════════════════════════════════════════════════════════════════
    private UnitDecision DoExplore(UnitData unit, UnitPlan plan,
                                    bool isDrone, bool isTruck, bool isFighter,
                                    UnitDecision d, string label)
    {
        var wp = GetExploreTarget(unit, plan, isDrone, isTruck, isFighter);
        d.Target        = wp;
        d.TargetDistance = GameState.Dist(unit.Position, wp);
        d.Operation     = MoveToward(unit, plan, wp);
        d.Intent        = $"{label} → ({wp.X},{wp.Y})";
        return d;
    }

    private Pos GetExploreTarget(UnitData unit, UnitPlan plan,
                                  bool isDrone, bool isTruck, bool isFighter)
    {
        int sightDist  = isDrone ? 16 : isTruck ? 8 : 2;
        int sectorMinY = isDrone   ? DroneSectorMinY   :
                         isTruck   ? TruckSectorMinY   : FighterSectorMinY;
        int sectorMaxY = isDrone   ? DroneSectorMaxY   :
                         isTruck   ? TruckSectorMaxY   : FighterSectorMaxY;

        if (plan.ExploreTarget != null && plan.StuckTicks < 4)
        {
            bool outOfBounds = _state.IsOutOfBounds(plan.ExploreTarget.X, plan.ExploreTarget.Y);
            bool outOfSector = plan.ExploreTarget.Y < sectorMinY ||
                               plan.ExploreTarget.Y > sectorMaxY;

            if (!outOfBounds && !outOfSector)
            {
                int coldness = _state.GetColdness(plan.ExploreTarget.X, plan.ExploreTarget.Y);
                if (coldness > 30 &&
                    GameState.Dist(unit.Position, plan.ExploreTarget) > sightDist / 2)
                    return plan.ExploreTarget;
            }
            plan.ExploreTarget = null;
        }

        if (plan.CurrentReservation != null)
        {
            _state.UnreserveTarget(plan.CurrentReservation.X, plan.CurrentReservation.Y);
            plan.CurrentReservation = null;
        }
        plan.StuckTicks = 0;

        var coldest = _state.GetColdestCell(unit.Position, unit.Id, sectorMinY, sectorMaxY);

        var rnd  = new Random(HashCode.Combine(unit.Id, plan.LastReservationUpdate));
        int maxX = Math.Max(260, _state.MaxX == int.MinValue ? 260 : _state.MaxX + 40);
        coldest.X = Math.Clamp(coldest.X + rnd.Next(-2, 3), 2, maxX - 2);
        coldest.Y = Math.Clamp(coldest.Y + rnd.Next(-2, 3), sectorMinY, sectorMaxY);

        _state.ReserveTarget(coldest.X, coldest.Y, unit.Id);
        plan.CurrentReservation    = coldest;
        plan.LastReservationUpdate = _state.Tick;
        plan.ExploreTarget         = coldest;
        return coldest;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FIRE TARGETING — cluster-aware scoring
    // ═══════════════════════════════════════════════════════════════════════
    private KnownFire? PickFire(UnitData unit, UnitPlan plan,
                                 bool isDrone, bool isTruck, bool isFighter)
    {
        if (_state.KnownFires.Count == 0) { ReleaseClaim(plan); return null; }

        if (plan.AssignedFireKey != null)
        {
            if (!_state.KnownFires.ContainsKey(plan.AssignedFireKey) ||
                _state.Tick - plan.AssignedFireTick > ClaimStaleTicks)
                ReleaseClaim(plan);
        }

        if (plan.AssignedFireKey != null && _state.KnownFires.ContainsKey(plan.AssignedFireKey))
            return _state.KnownFires[plan.AssignedFireKey];

        KnownFire? pick      = null;
        double     bestScore = double.MaxValue;

        foreach (var fire in _state.KnownFires.Values)
        {
            if (fire.Hp <= 0) continue;

            int    dist         = GameState.Dist(unit.Position, fire.Position);
            int    age          = _state.Tick - fire.LastSeenTick;
            int    clusterBonus = CountNearbyFires(fire.Position, radius: 2) * 30;
            double score        = dist * 10.0 + fire.Hp / 10.0 + age * 0.5 - clusterBonus;

            if (score < bestScore) { bestScore = score; pick = fire; }
        }

        if (pick != null)
        {
            _state.TryClaimFire(FK(pick), unit.Id);
            plan.AssignedFireKey  = FK(pick);
            plan.AssignedFireTick = _state.Tick;
        }

        return pick;
    }

    private int CountNearbyFires(Pos pos, int radius)
    {
        int count = 0;
        foreach (var f in _state.KnownFires.Values)
        {
            if (f.Hp <= 0 || (f.Position.X == pos.X && f.Position.Y == pos.Y)) continue;
            if (Math.Abs(f.Position.X - pos.X) + Math.Abs(f.Position.Y - pos.Y) <= radius)
                count++;
        }
        return count;
    }

    private void ReleaseClaim(UnitPlan plan)
    {
        if (plan.AssignedFireKey == null) return;
        _state.ReleaseFire(plan.AssignedFireKey);
        plan.AssignedFireKey = null;
    }

    private static string FK(KnownFire f) => GameState.Key(f.Position.X, f.Position.Y);

    // ═══════════════════════════════════════════════════════════════════════
    //  WATER FINDING
    // ═══════════════════════════════════════════════════════════════════════
    private Pos? FindReachableWater(UnitData unit)
{
    foreach (var w in _state.KnownWaters.Values.ToList())
    {
        if (_state.IsOutOfBounds(w.Position.X, w.Position.Y))
        {
            _state.KnownWaters.Remove(GameState.Key(w.Position.X, w.Position.Y));
        }
    }

    return _state.KnownWaters.Values
        .Where(w => !_state.IsOutOfBounds(w.Position.X, w.Position.Y))
        .OrderBy(w => GameState.Dist(unit.Position, w.Position))
        .FirstOrDefault()
        ?.Position;
}

    // ═══════════════════════════════════════════════════════════════════════
    //  MOVEMENT — A* with wall & occupancy checks
    //
    //  allowWaterEntry (PATCH 3b):
    //    When true, water tiles are treated as walkable during A* search.
    //    This is required for ground units navigating to water sources,
    //    especially multi-cell lakes where every cell is a water tile and
    //    the default IsOccupied() check makes the entire lake impassable.
    //    Only teammate presence blocks entry into a water tile.
    // ═══════════════════════════════════════════════════════════════════════
    private string MoveToward(UnitData unit, UnitPlan plan, Pos target,
                               bool allowWaterEntry = false)
    {
        if (GameState.Dist(unit.Position, target) == 0) return "NOP";

        int sx = unit.Position.X, sy = unit.Position.Y;

        var pq = new PriorityQueue<(int X, int Y, string FirstMove, int Cost), int>();

        int[,] gScore = new int[350, 350];
        for (int i = 0; i < 350; i++)
            for (int j = 0; j < 350; j++)
                gScore[i, j] = int.MaxValue;

        gScore[sx, sy] = 0;

        var initDirs = new[]
        {
            ("Up",    sx, sy - 1),
            ("Down",  sx, sy + 1),
            ("Left",  sx - 1, sy),
            ("Right", sx + 1, sy)
        };

        foreach (var (dir, nx, ny) in initDirs)
        {
            if (!InBounds(nx, ny) || _state.IsOutOfBounds(nx, ny)) continue;
            if (IsCellTemporarilyBlocked(nx, ny, allowWaterEntry)) continue;
            if (IsEffectivelyOccupied(nx, ny, unit, allowWaterEntry)) continue;

            gScore[nx, ny] = 1;
            int h = Math.Abs(target.X - nx) + Math.Abs(target.Y - ny);
            pq.Enqueue((nx, ny, dir, 1), 1 + h);
        }

        string bestMove = "NOP";
        int    bestDist = GameState.Dist(unit.Position, target);
        int    limit    = 0;

        while (pq.Count > 0 && limit++ < 20_000)
        {
            var (cx, cy, firstMove, cost) = pq.Dequeue();

            if (cost > gScore[cx, cy]) continue;

            int dist = Math.Abs(target.X - cx) + Math.Abs(target.Y - cy);
            if (dist < bestDist)
            {
                bestDist = dist; bestMove = firstMove;
                if (dist == 0) break;

                // Early-exit only when we cannot enter the target tile.
                // When allowWaterEntry=true, the water tile is walkable so A*
                // will naturally reach dist==0 above — don't break early.
                if (!allowWaterEntry && dist == 1 && IsOccupied(target.X, target.Y, unit))
                    break;
            }

            foreach (var (nx, ny) in new[]
                { (cx, cy-1), (cx, cy+1), (cx-1, cy), (cx+1, cy) })
            {
                if (!InBounds(nx, ny) || _state.IsOutOfBounds(nx, ny)) continue;
                if (IsCellTemporarilyBlocked(nx, ny, allowWaterEntry)) continue;
                if (IsEffectivelyOccupied(nx, ny, unit, allowWaterEntry)) continue;

                int ng = cost + 1;
                if (ng >= gScore[nx, ny]) continue;

                gScore[nx, ny] = ng;
                int h = Math.Abs(target.X - nx) + Math.Abs(target.Y - ny);
                pq.Enqueue((nx, ny, firstMove, ng), ng + h);
            }
        }

        if (bestMove != "NOP") return bestMove;

        // Greedy fallback
        string fb = "NOP"; int fbDist = int.MaxValue;
        foreach (var (dir, nx, ny) in initDirs)
        {
            if (!InBounds(nx, ny) || _state.IsOutOfBounds(nx, ny)) continue;
            if (IsCellTemporarilyBlocked(nx, ny, allowWaterEntry)) continue;
            if (IsEffectivelyOccupied(nx, ny, unit, allowWaterEntry)) continue;
            int d2 = Math.Abs(target.X - nx) + Math.Abs(target.Y - ny);
            if (d2 < fbDist) { fbDist = d2; fb = dir; }
        }

        if (fb == "NOP" || plan.StuckTicks > StuckThreshold)
        {
            plan.ExploreTarget = null; plan.StuckTicks = 0;
            var rng = new Random(HashCode.Combine(unit.Id, _state.Tick));
            var ok  = initDirs.Where(m => InBounds(m.Item2, m.Item3) &&
                                          !_state.IsOutOfBounds(m.Item2, m.Item3) &&
                                          !IsCellTemporarilyBlocked(m.Item2, m.Item3, allowWaterEntry) &&
                                          !IsEffectivelyOccupied(m.Item2, m.Item3, unit, allowWaterEntry))
                               .OrderBy(_ => rng.Next()).FirstOrDefault();
            return ok.Item1 ?? "NOP";
        }

        return fb;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  OCCUPANCY HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Standard occupancy check. Fires block everyone. Water blocks ground
    /// units. Other units block all.
    /// NOTE: Do NOT use this for refill decisions — use IsTeammateOn instead.
    /// </summary>
    private bool IsOccupied(int x, int y, UnitData unit)
{
    string key = GameState.Key(x, y);

    // Ground units avoid fire. Drone can fly through fire.
    if (_state.KnownFires.ContainsKey(key) && !IsDroneUnit(unit.UnitType))
        return true;

    // Ground units treat water as blocked unless allowWaterEntry is used.
    if (!IsDroneUnit(unit.UnitType) && _state.KnownWaters.ContainsKey(key))
        return true;

    return _state.LatestUnits.Any(u => u.Id != unit.Id &&
                                       u.Position.X == x &&
                                       u.Position.Y == y);
}

    /// <summary>
    /// PATCH 3b — A* occupancy check with optional water-tile passability.
    /// When allowWaterEntry=true (used only when navigating to a water source),
    /// water tiles are NOT treated as walls — only teammate presence blocks them.
    /// This fixes the multi-cell lake problem where every tile around the water
    /// was IsOccupied=true, making refill impossible for ground units.
    /// </summary>
    private bool IsEffectivelyOccupied(int x, int y, UnitData unit, bool allowWaterEntry)
    {
        if (allowWaterEntry && _state.KnownWaters.ContainsKey(GameState.Key(x, y)))
            return IsTeammateOn(x, y, unit);
        return IsOccupied(x, y, unit);
    }

    private bool IsCellTemporarilyBlocked(int x, int y, bool allowWaterEntry)
    {
        if (!_state.IsCellBlocked(x, y))
            return false;

        return !(allowWaterEntry && _state.KnownWaters.ContainsKey(GameState.Key(x, y)));
    }

    /// <summary>
    /// PATCH 3 — Returns true only when a different unit physically occupies
    /// (x, y). Used at refill decision points instead of IsOccupied(), which
    /// incorrectly returns true for water tiles for ground units.
    /// </summary>
    private bool IsTeammateOn(int x, int y, UnitData unit) =>
        _state.LatestUnits.Any(u => u.Id != unit.Id &&
                                    u.Position.X == x && u.Position.Y == y);

    private static bool InBounds(int x, int y) =>
        x >= 0 && y >= 0 && x < 350 && y < 350;

    // ═══════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════
    private void UpdateStuck(UnitData unit, UnitPlan plan)
    {
        bool moved = unit.Position.X != plan.LastPosition.X ||
                     unit.Position.Y != plan.LastPosition.Y;
        if (moved) plan.StuckTicks = 0; else plan.StuckTicks++;
        plan.LastPosition = ClonePos(unit.Position);
    }

    private UnitPlan GetOrCreatePlan(UnitData unit)
    {
        if (!_state.UnitPlans.TryGetValue(unit.Id, out var plan))
        {
            plan = new UnitPlan
            {
                Origin       = ClonePos(unit.Position),
                LastPosition = ClonePos(unit.Position)
            };
            _state.UnitPlans[unit.Id] = plan;
        }
        return plan;
    }

    private static bool IsMoveOp(string op) => op is "Up" or "Down" or "Left" or "Right";

    private static bool IsDroneUnit(string t) =>
        t.Contains("copter", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("drone",  StringComparison.OrdinalIgnoreCase) ||
        t.Contains("heli",   StringComparison.OrdinalIgnoreCase) ||
        t.Contains("uav",    StringComparison.OrdinalIgnoreCase);

    private static Pos ClonePos(Pos p) =>
        new() { IsEmpty = p.IsEmpty, X = p.X, Y = p.Y, Hp = p.Hp };

    private static (int x, int y) MoveTarget(Pos from, string op) => op switch
    {
        "Up"    => (from.X,     from.Y - 1),
        "Down"  => (from.X,     from.Y + 1),
        "Left"  => (from.X - 1, from.Y),
        "Right" => (from.X + 1, from.Y),
        _       => (from.X,     from.Y)
    };


    private Pos GetFireApproachTarget(UnitData unit, Pos firePos)
{
    var candidates = new List<Pos>();

    // First priority: direct attack cells, distance 1.
    candidates.Add(new Pos { X = firePos.X,     Y = firePos.Y - 1 });
    candidates.Add(new Pos { X = firePos.X,     Y = firePos.Y + 1 });
    candidates.Add(new Pos { X = firePos.X - 1, Y = firePos.Y     });
    candidates.Add(new Pos { X = firePos.X + 1, Y = firePos.Y     });

    // Second priority: backup staging cells, distance 2.
    // This helps truck avoid dancing behind drone/fighter.
    candidates.Add(new Pos { X = firePos.X,     Y = firePos.Y - 2 });
    candidates.Add(new Pos { X = firePos.X,     Y = firePos.Y + 2 });
    candidates.Add(new Pos { X = firePos.X - 2, Y = firePos.Y     });
    candidates.Add(new Pos { X = firePos.X + 2, Y = firePos.Y     });

    candidates.Add(new Pos { X = firePos.X - 1, Y = firePos.Y - 1 });
    candidates.Add(new Pos { X = firePos.X + 1, Y = firePos.Y - 1 });
    candidates.Add(new Pos { X = firePos.X - 1, Y = firePos.Y + 1 });
    candidates.Add(new Pos { X = firePos.X + 1, Y = firePos.Y + 1 });

    Pos? best = null;
    int bestScore = int.MaxValue;

    foreach (var p in candidates)
    {
        if (!InBounds(p.X, p.Y)) continue;
        if (_state.IsOutOfBounds(p.X, p.Y)) continue;
        if (IsOccupied(p.X, p.Y, unit)) continue;

        int distToFire = GameState.Dist(p, firePos);

        // Must be useful. Distance 1 can extinguish.
        // Distance 2 is only a staging tile.
        if (distToFire > 2) continue;

        int score = GameState.Dist(unit.Position, p);

        // Prefer actual attack cells.
        if (distToFire == 1)
    score -= 50;
else
    score += 25;

        // Avoid crowding teammates.
        // Wolf-pack spacing:
// Never choose the exact same tile, but do not over-avoid nearby teammates.
// Close teammates near a fire are usually GOOD because damage stacks faster.
foreach (var teammate in _state.LatestUnits.Where(u => u.Id != unit.Id))
{
    int td = GameState.Dist(teammate.Position, p);

    if (td == 0) score += 1000;   // impossible / occupied
    else if (td == 1) score += 8; // mild spacing penalty
    else if (td == 2) score += 2; // tiny spacing penalty
}

// Truck should avoid sitting directly on top of the drone,
// but still cooperate aggressively in the same fire cluster.
if (unit.UnitType.Contains("truck", StringComparison.OrdinalIgnoreCase))
{
    var drone = _state.LatestUnits.FirstOrDefault(u => IsDroneUnit(u.UnitType));
    if (drone != null && GameState.Dist(drone.Position, p) <= 1)
        score += 40;
}

        if (score < bestScore)
        {
            bestScore = score;
            best = p;
        }
    }

    if (best != null)
    return best;

for (int radius = 3; radius <= 6; radius++)
{
    Pos? fallback = null;
    int fallbackScore = int.MaxValue;

    for (int dx = -radius; dx <= radius; dx++)
    {
        int dy = radius - Math.Abs(dx);

        var candidatess = new[]
        {
            new Pos { X = firePos.X + dx, Y = firePos.Y + dy },
            new Pos { X = firePos.X + dx, Y = firePos.Y - dy }
        };

        foreach (var p in candidatess)
        {
            if (!InBounds(p.X, p.Y)) continue;
            if (_state.IsOutOfBounds(p.X, p.Y)) continue;
            if (IsOccupied(p.X, p.Y, unit)) continue;

            int score = GameState.Dist(unit.Position, p);

            if (score < fallbackScore)
            {
                fallbackScore = score;
                fallback = p;
            }
        }
    }

    if (fallback != null)
        return fallback;
}

// Absolute emergency fallback: stay where we are instead of targeting fire.
return unit.Position;
}
private Pos GetClosestAttackTile(UnitData unit, Pos firePos)
{
    var candidates = new[]
    {
        new Pos { X = firePos.X,     Y = firePos.Y - 1 },
        new Pos { X = firePos.X,     Y = firePos.Y + 1 },
        new Pos { X = firePos.X - 1, Y = firePos.Y     },
        new Pos { X = firePos.X + 1, Y = firePos.Y     }
    };

    Pos? best = null;
    int bestScore = int.MaxValue;

    foreach (var p in candidates)
    {
        if (!InBounds(p.X, p.Y)) continue;
        if (_state.IsOutOfBounds(p.X, p.Y)) continue;

        // Attack tile must not be fire, water for ground units, or occupied.
        if (IsOccupied(p.X, p.Y, unit)) continue;

        int score = GameState.Dist(unit.Position, p);

        foreach (var teammate in _state.LatestUnits.Where(u => u.Id != unit.Id))
        {
            int td = GameState.Dist(teammate.Position, p);

            // Never pick same tile.
            if (td == 0) score += 1000;

            // Small crowd penalty only. Do not over-avoid teammates near fire.
            else if (td == 1) score += 4;
        }

        if (score < bestScore)
        {
            bestScore = score;
            best = p;
        }
    }

    // If every attack tile is blocked, push toward the fire.
    // MoveToward will stop adjacent because fire itself is occupied.
     return best ?? GetFireApproachTarget(unit, firePos);
}
private string RandomFreeMove(UnitData unit, bool allowWaterEntry = false)
{
    var dirs = new[]
    {
        ("Up",    unit.Position.X,     unit.Position.Y - 1),
        ("Down",  unit.Position.X,     unit.Position.Y + 1),
        ("Left",  unit.Position.X - 1, unit.Position.Y),
        ("Right", unit.Position.X + 1, unit.Position.Y)
    };

    var rng = new Random(HashCode.Combine(unit.Id, _state.Tick));

    foreach (var (dir, nx, ny) in dirs.OrderBy(_ => rng.Next()))
    {
        if (!InBounds(nx, ny)) continue;
        if (_state.IsOutOfBounds(nx, ny)) continue;
        if (IsEffectivelyOccupied(nx, ny, unit, allowWaterEntry)) continue;

        return dir;
    }

    return "NOP";
}
}
