using System;
using System.Collections.Generic;
using System.Linq;

public class ConsoleRenderer
{
    private readonly GameState _state;

    public ConsoleRenderer(GameState state)
    {
        _state = state;
    }

    public void DrawStatus(Dictionary<int, UnitDecision> latestDecisions)
    {
        Console.WriteLine("===== FIRE CLIENT STATUS =====");
        Console.WriteLine($"Time: {DateTime.Now:HH:mm:ss} | Tick: {_state.Tick} | Units: {_state.LatestUnits.Count}");
        Console.WriteLine($"Known fires: {_state.KnownFires.Count} | Known waters: {_state.KnownWaters.Count}");
        Console.WriteLine();

        Console.WriteLine("--- UNITS ---");
        foreach (var unit in _state.LatestUnits)
        {
            latestDecisions.TryGetValue(unit.Id, out var decision);
            var state = decision?.State ?? GetUnitState(unit.Id);
            string operation = decision?.Operation ?? "NOP";
            string intent = string.IsNullOrWhiteSpace(decision?.Intent) ? "Waiting for next decision" : decision.Intent;
            string target = FormatTarget(decision);
            string stuckInfo = FormatPlanInfo(unit.Id);

            Console.WriteLine(
                $"[{unit.Id}] {ShortType(unit.UnitType),-12} pos=({unit.Position.X,3},{unit.Position.Y,3}) " +
                $"water={unit.CurrentWaterLevel,3} hp={unit.CurrentHP,4} " +
                $"seenF={unit.SeenFires.Count,2} seenW={unit.SeenWaters.Count,2}"
            );
            Console.WriteLine(
                $"     state={state,-14} action={operation,-16} target={target,-24} {stuckInfo} thought={intent}"
            );
        }

        Console.WriteLine();
        Console.WriteLine("--- FIRE PRIORITY ---");
        var fires = _state.KnownFires.Values
                     .OrderBy(f => f.Hp)
                     .ThenBy(f => f.LastSeenTick)
                     .Take(10)
                     .ToList();

        if (fires.Count == 0)
            Console.WriteLine("No known fires.");

        foreach (var fire in fires)
        {
            int age = _state.Tick - fire.LastSeenTick;
            Console.WriteLine($"FIRE ({fire.Position.X,3},{fire.Position.Y,3}) hp={fire.Hp,4} lastSeen={fire.LastSeenTick,5} age={age,3}");
        }

        Console.WriteLine();
        Console.WriteLine("--- WATER MEMORY ---");
        var waters = _state.KnownWaters.Values
            .OrderByDescending(w => w.LastSeenTick)
            .Take(5)
            .ToList();

        if (waters.Count == 0)
            Console.WriteLine("No known water yet.");

        foreach (var water in waters)
        {
            int age = _state.Tick - water.LastSeenTick;
            Console.WriteLine($"WATER ({water.Position.X,3},{water.Position.Y,3}) lastSeen={water.LastSeenTick,5} age={age,3}");
        }

        Console.WriteLine("==============================");
    }

    private UnitState GetUnitState(int unitId)
    {
        return _state.ActiveUnitStates.TryGetValue(unitId, out var state)
            ? state
            : UnitState.Seeking;
    }

    private static string FormatTarget(UnitDecision? decision)
    {
        if (decision?.Target == null)
            return "-";

        string result = $"({decision.Target.X},{decision.Target.Y})";

        if (decision.TargetDistance.HasValue)
            result += $" d={decision.TargetDistance.Value}";

        if (decision.TargetHp.HasValue)
            result += $" hp={decision.TargetHp.Value}";

        return result;
    }

    private string FormatPlanInfo(int unitId)
    {
        if (!_state.UnitPlans.TryGetValue(unitId, out var plan))
            return "";

        var blocked = new List<string>();

        if (_state.Tick <= plan.BlockUpUntil)
            blocked.Add("Up");
        if (_state.Tick <= plan.BlockDownUntil)
            blocked.Add("Down");
        if (_state.Tick <= plan.BlockLeftUntil)
            blocked.Add("Left");
        if (_state.Tick <= plan.BlockRightUntil)
            blocked.Add("Right");

        if (plan.StuckTicks == 0 && blocked.Count == 0)
            return "";

        string blockedText = blocked.Count == 0 ? "-" : string.Join(",", blocked);
        string edgeText = FormatEdgeHint(plan.LastPosition);
        return $"stuck={plan.StuckTicks,2} blocked={blockedText,-15}{edgeText}";
    }

    private static string FormatEdgeHint(Pos position)
    {
        var edges = new List<string>();

        if (position.X <= 1)
            edges.Add("left-edge");
        if (position.Y <= 1)
            edges.Add("top-edge");

        return edges.Count == 0 ? "" : $" edge={string.Join(",", edges),-16}";
    }

    private static string ShortType(string unitType)
    {
        if (unitType.Contains("copter", StringComparison.OrdinalIgnoreCase))
            return "Drone";

        if (unitType.Contains("truck", StringComparison.OrdinalIgnoreCase))
            return "Truck";

        if (unitType.Contains("fighter", StringComparison.OrdinalIgnoreCase))
            return "Firefighter";

        return string.IsNullOrWhiteSpace(unitType) ? "Unit" : unitType;
    }
}
