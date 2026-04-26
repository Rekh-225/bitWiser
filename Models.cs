using System.Collections.Generic;

public enum UnitState
{
    Seeking,
    Pursuing,
    Extinguishing,
    SeekingWater,
    Scouting,
    Supporting,
    Manual
}

public class UnitDecision
{
    public int UnitId { get; set; }
    public UnitState State { get; set; }
    public string Operation { get; set; } = "NOP";
    public string Intent { get; set; } = "";
    public Pos? Target { get; set; }
    public int? TargetDistance { get; set; }
    public int? TargetHp { get; set; }
}

public class UnitData
{
    public int Id { get; set; }
    public string UnitType { get; set; } = "";
    public string Owner { get; set; } = "";
    public Pos Position { get; set; } = new();
    public List<Pos> SeenWaters { get; set; } = new();
    public List<Pos> SeenFires { get; set; } = new();
    public int CurrentWaterLevel { get; set; }
    public int CurrentHP { get; set; }
}

public class Pos
{
    public bool IsEmpty { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Hp { get; set; } = 1000;
}

public class KnownFire
{
    public Pos Position { get; set; } = new();
    public int Hp { get; set; } = 1000;
    public int LastSeenTick { get; set; }
}

public class KnownWater
{
    public Pos Position { get; set; } = new();
    public int LastSeenTick { get; set; }
}

public class UnitPlan
{
    public Pos Origin { get; set; } = new();
    public Pos? ExploreTarget { get; set; }
    public int ExploreStep { get; set; }
    public Pos LastPosition { get; set; } = new();
    public int StuckTicks { get; set; }
    public string LastOperation { get; set; } = "NOP";

    // Blocked direction tracking (wall memory)
    public int BlockUpUntil { get; set; }
    public int BlockDownUntil { get; set; }
    public int BlockLeftUntil { get; set; }
    public int BlockRightUntil { get; set; }

    public string Sector { get; set; } = "center";

    public Pos? CurrentReservation { get; set; }
    public int LastReservationUpdate { get; set; }

    public Pos? ClosestWater { get; set; }
    public int LastClosestWaterUpdate { get; set; }

    public int LastWaterLevel { get; set; } = -1;
    public int WaterNoGainTicks { get; set; }
    public int WaterSeekSuspendUntil { get; set; }

    // --- Fire claiming ---
    public string? AssignedFireKey { get; set; }
    public int AssignedFireTick { get; set; }
}

public class ManualCommandFile
{
    public bool ManualMode { get; set; } = false;
    public string UpdatedBy { get; set; } = "unknown";
    public List<ManualCommand> Commands { get; set; } = new();
}

public class ManualCommand
{
    public int UnitId { get; set; }
    public string Operation { get; set; } = "NOP";
    public long Sequence { get; set; }
    public string Controller { get; set; } = "human";
    public string IssuedAt { get; set; } = "";
}