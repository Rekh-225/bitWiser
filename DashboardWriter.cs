using System.Text.Json;

public class DashboardWriter
{
    private const string HtmlPath = "dashboard.html";
    private const string StatePath = "dashboard-state.js";
    private static readonly string ReactStatePath = Path.Combine("Viewer", "bitview", "public", "dashboard-state.json");
    private static readonly string ReactDistStatePath = Path.Combine("Viewer", "bitview", "dist", "dashboard-state.json");
    private readonly GameState _state;
    private bool _htmlWritten;

    public DashboardWriter(GameState state)
    {
        _state = state;
    }

    public void Write(Dictionary<int, UnitDecision> decisions)
    {
        if (!_htmlWritten || !File.Exists(HtmlPath))
        {
            File.WriteAllText(HtmlPath, BuildHtml());
            _htmlWritten = true;
        }

        object snapshot;

        lock (_state.StateLock)
        {
            var units = _state.LatestUnits
                .Select(unit =>
                {
                    decisions.TryGetValue(unit.Id, out var decision);
                    _state.UnitPlans.TryGetValue(unit.Id, out var plan);

                    return new
                    {
                        id = unit.Id,
                        type = ShortType(unit.UnitType),
                        x = unit.Position.X,
                        y = unit.Position.Y,
                        water = unit.CurrentWaterLevel,
                        hp = unit.CurrentHP,
                        seenFires = unit.SeenFires.Count,
                        seenWaters = unit.SeenWaters.Count,
                        state = decision?.State.ToString() ?? GetUnitState(unit.Id).ToString(),
                        action = decision?.Operation ?? "NOP",
                        thought = decision?.Intent ?? "",
                        target = decision?.Target == null ? null : new
                        {
                            x = decision.Target.X,
                            y = decision.Target.Y,
                            distance = decision.TargetDistance,
                            hp = decision.TargetHp
                        },
                        stuck = plan?.StuckTicks ?? 0,
                        blocked = GetBlockedDirections(plan)
                    };
                })
                .ToList();

            var fires = _state.KnownFires.Values
                .OrderBy(f => f.Hp)
                .ThenByDescending(f => f.LastSeenTick)
                .Select(f => new
                {
                    x = f.Position.X,
                    y = f.Position.Y,
                    hp = f.Hp,
                    age = _state.Tick - f.LastSeenTick,
                    lastSeen = f.LastSeenTick
                })
                .ToList();

            var waters = _state.KnownWaters.Values
                .OrderByDescending(w => w.LastSeenTick)
                .Select(w => new
                {
                    x = w.Position.X,
                    y = w.Position.Y,
                    age = _state.Tick - w.LastSeenTick,
                    lastSeen = w.LastSeenTick
                })
                .ToList();

            var points = units.Select(u => (u.x, u.y))
                .Concat(fires.Select(f => (f.x, f.y)))
                .Concat(waters.Select(w => (w.x, w.y)))
                .ToList();

            int minX = _state.MinX == int.MaxValue ? 0 : _state.MinX;
            int maxX = _state.MaxX == int.MinValue ? 120 : _state.MaxX;
            int minY = _state.MinY == int.MaxValue ? 0 : _state.MinY;
            int maxY = _state.MaxY == int.MinValue ? 120 : _state.MaxY;

            var bMinX = Math.Max(0, minX - 8);
            var bMaxX = Math.Max(maxX + 8, 20);
            var bMinY = Math.Max(0, minY - 8);
            var bMaxY = Math.Max(maxY + 8, 20);

            var heatmapData = new List<object>();
            for (int x = bMinX; x <= bMaxX; x++)
            {
                for (int y = bMinY; y <= bMaxY; y++)
                {
                    if (x < 350 && y < 350)
                    {
                        int lastSeen = _state.GetLastSeenTick(x, y);
                        if (lastSeen > 0)
                        {
                            heatmapData.Add(new { x, y, age = _state.Tick - lastSeen });
                        }
                    }
                }
            }

            snapshot = new
            {
                tick = _state.Tick,
                time = DateTime.Now.ToString("HH:mm:ss"),
                units,
                fires,
                waters,
                heatmap = heatmapData,
                bounds = new
                {
                    minX = bMinX,
                    maxX = bMaxX,
                    minY = bMinY,
                    maxY = bMaxY
                }
            };
        }

        var json = JsonSerializer.Serialize(snapshot);
        File.WriteAllText(StatePath, "window.__FIRE_STATE = " + json + ";");

        var reactStateDirectory = Path.GetDirectoryName(ReactStatePath);
        if (!string.IsNullOrWhiteSpace(reactStateDirectory))
            Directory.CreateDirectory(reactStateDirectory);

        File.WriteAllText(ReactStatePath, json);

        var reactDistStateDirectory = Path.GetDirectoryName(ReactDistStatePath);
        if (!string.IsNullOrWhiteSpace(reactDistStateDirectory) && Directory.Exists(reactDistStateDirectory))
            File.WriteAllText(ReactDistStatePath, json);
    }

    private UnitState GetUnitState(int unitId)
    {
        return _state.ActiveUnitStates.TryGetValue(unitId, out var state)
            ? state
            : UnitState.Seeking;
    }

    private List<string> GetBlockedDirections(UnitPlan? plan)
    {
        var blocked = new List<string>();

        if (plan == null)
            return blocked;

        if (_state.Tick <= plan.BlockUpUntil)
            blocked.Add("Up");
        if (_state.Tick <= plan.BlockDownUntil)
            blocked.Add("Down");
        if (_state.Tick <= plan.BlockLeftUntil)
            blocked.Add("Left");
        if (_state.Tick <= plan.BlockRightUntil)
            blocked.Add("Right");

        return blocked;
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

    private static string BuildHtml()
    {
        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Fire Client Dashboard</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #111315;
      --panel: #191d20;
      --panel-2: #20262b;
      --line: #343d45;
      --text: #eef2f4;
      --muted: #98a4ad;
      --fire: #ffb02e;
      --fire-hot: #ff4d2e;
      --water: #3aa7ff;
      --drone: #c8f05a;
      --truck: #66d7d1;
      --fighter: #f0f4f8;
      --target: #ff5f8f;
    }

    * { box-sizing: border-box; }

    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-family: Segoe UI, Inter, Arial, sans-serif;
      min-height: 100vh;
    }

    .shell {
      display: grid;
      grid-template-columns: minmax(520px, 1fr) 420px;
      gap: 14px;
      padding: 14px;
      height: 100vh;
    }

    .map-panel, .side-panel {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      overflow: hidden;
    }

    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 12px 14px;
      background: var(--panel-2);
      border-bottom: 1px solid var(--line);
    }

    h1 {
      font-size: 18px;
      margin: 0;
      font-weight: 650;
    }

    .stats {
      display: flex;
      gap: 10px;
      color: var(--muted);
      font-size: 13px;
      white-space: nowrap;
    }

    .map-wrap {
      position: relative;
      height: calc(100vh - 72px);
      padding: 12px;
    }

    #map {
      position: relative;
      width: 100%;
      height: 100%;
      background:
        linear-gradient(to right, rgba(255,255,255,.055) 1px, transparent 1px),
        linear-gradient(to bottom, rgba(255,255,255,.055) 1px, transparent 1px),
        #0f1518;
      background-size: var(--cell, 20px) var(--cell, 20px);
      border: 1px solid #2d363d;
      border-radius: 8px;
      overflow: hidden;
    }

    .node {
      position: absolute;
      transform: translate(-50%, -50%);
      display: grid;
      place-items: center;
      font-size: 11px;
      font-weight: 700;
      color: #061015;
      border: 1px solid rgba(255,255,255,.55);
      box-shadow: 0 0 0 2px rgba(0,0,0,.25);
    }

    .unit {
      width: 24px;
      height: 24px;
      border-radius: 4px;
      z-index: 5;
    }

    .Drone { background: var(--drone); }
    .Truck { background: var(--truck); }
    .Firefighter { background: var(--fighter); }

    .fire {
      width: 12px;
      height: 12px;
      border-radius: 50%;
      background: var(--fire);
      z-index: 3;
    }

    .fire.hot { background: var(--fire-hot); }

    .water {
      width: 10px;
      height: 10px;
      border-radius: 2px;
      background: var(--water);
      opacity: .85;
      z-index: 2;
    }

    .target-line {
      position: absolute;
      height: 2px;
      background: rgba(255,95,143,.8);
      transform-origin: left center;
      z-index: 1;
    }

    .target-dot {
      width: 14px;
      height: 14px;
      border-radius: 50%;
      border: 2px solid var(--target);
      background: rgba(255,95,143,.18);
      z-index: 4;
    }

    .side-panel {
      display: grid;
      grid-template-rows: auto 1fr;
      min-width: 0;
    }

    .lists {
      overflow: auto;
      padding: 12px;
      display: grid;
      gap: 12px;
      align-content: start;
    }

    .card {
      background: #15191c;
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 10px;
    }

    .unit-card {
      display: grid;
      gap: 6px;
      border-left: 4px solid var(--fighter);
    }

    .unit-card.Drone { border-left-color: var(--drone); }
    .unit-card.Truck { border-left-color: var(--truck); }

    .row {
      display: flex;
      justify-content: space-between;
      gap: 12px;
      font-size: 13px;
    }

    .label { color: var(--muted); }
    .thought { color: #c9d3d9; font-size: 12px; line-height: 1.35; }

    .section-title {
      margin: 6px 0 8px;
      color: var(--muted);
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: .08em;
    }

    .mini-list {
      display: grid;
      gap: 6px;
      font-size: 12px;
      color: #d8e0e4;
    }

    .pill {
      display: inline-flex;
      align-items: center;
      min-height: 22px;
      padding: 2px 8px;
      border-radius: 999px;
      background: #252c31;
      color: #dfe7eb;
      font-size: 12px;
    }

    @media (max-width: 980px) {
      .shell {
        grid-template-columns: 1fr;
        height: auto;
      }

      .map-wrap {
        height: 65vh;
      }
    }
  </style>
</head>
<body>
  <main class="shell">
    <section class="map-panel">
      <header>
        <h1>Fire Client Dashboard</h1>
        <div class="stats">
          <span id="tick">Tick -</span>
          <span id="time">Time -</span>
          <span id="counts">0 fires / 0 water</span>
        </div>
      </header>
      <div class="map-wrap">
        <div id="map"></div>
      </div>
    </section>

    <aside class="side-panel">
      <header>
        <h1>Live Strategy</h1>
        <div class="stats"><span id="bounds">Bounds -</span></div>
      </header>
      <div class="lists">
        <section>
          <div class="section-title">Units</div>
          <div id="units"></div>
        </section>
        <section>
          <div class="section-title">Top Fires</div>
          <div id="fires" class="mini-list"></div>
        </section>
        <section>
          <div class="section-title">Recent Water</div>
          <div id="waters" class="mini-list"></div>
        </section>
      </div>
    </aside>
  </main>

  <script>
    window.__FIRE_STATE = { tick: 0, time: "", units: [], fires: [], waters: [], bounds: { minX: 0, maxX: 120, minY: 0, maxY: 120 } };

    function loadState() {
      const old = document.getElementById("state-script");
      if (old) old.remove();
      const script = document.createElement("script");
      script.id = "state-script";
      script.src = "dashboard-state.js?t=" + Date.now();
      script.onload = render;
      document.body.appendChild(script);
    }

    function pct(value, min, max) {
      if (max <= min) return 0;
      return ((value - min) / (max - min)) * 100;
    }

    function place(el, point, bounds) {
      el.style.left = pct(point.x, bounds.minX, bounds.maxX) + "%";
      el.style.top = pct(point.y, bounds.minY, bounds.maxY) + "%";
    }

    function addNode(map, cls, text, point, bounds, title) {
      const el = document.createElement("div");
      el.className = "node " + cls;
      el.textContent = text || "";
      el.title = title || "";
      place(el, point, bounds);
      map.appendChild(el);
      return el;
    }

    function addLine(map, from, to, bounds) {
      const x1 = pct(from.x, bounds.minX, bounds.maxX);
      const y1 = pct(from.y, bounds.minY, bounds.maxY);
      const x2 = pct(to.x, bounds.minX, bounds.maxX);
      const y2 = pct(to.y, bounds.minY, bounds.maxY);
      const dx = x2 - x1;
      const dy = y2 - y1;
      const length = Math.sqrt(dx * dx + dy * dy);
      const angle = Math.atan2(dy, dx) * 180 / Math.PI;
      const line = document.createElement("div");
      line.className = "target-line";
      line.style.left = x1 + "%";
      line.style.top = y1 + "%";
      line.style.width = length + "%";
      line.style.transform = "rotate(" + angle + "deg)";
      map.appendChild(line);
    }

    function render() {
      const state = window.__FIRE_STATE;
      const bounds = state.bounds;
      const map = document.getElementById("map");
      map.innerHTML = "";
      map.style.setProperty("--cell", Math.max(12, Math.min(34, map.clientWidth / Math.max(8, bounds.maxX - bounds.minX))) + "px");

      document.getElementById("tick").textContent = "Tick " + state.tick;
      document.getElementById("time").textContent = "Time " + state.time;
      document.getElementById("counts").textContent = state.fires.length + " fires / " + state.waters.length + " water";
      document.getElementById("bounds").textContent = "x " + bounds.minX + "-" + bounds.maxX + " y " + bounds.minY + "-" + bounds.maxY;

      state.waters.forEach(w => addNode(map, "water", "", w, bounds, "Water (" + w.x + "," + w.y + ") age " + w.age));
      state.fires.forEach(f => addNode(map, "fire " + (f.hp < 250 ? "hot" : ""), "", f, bounds, "Fire (" + f.x + "," + f.y + ") hp " + f.hp + " age " + f.age));

      state.units.forEach(u => {
        if (u.target) {
          addLine(map, u, u.target, bounds);
          addNode(map, "target-dot", "", u.target, bounds, "Target (" + u.target.x + "," + u.target.y + ")");
        }
      });

      state.units.forEach(u => {
        const label = u.type === "Drone" ? "D" : u.type === "Truck" ? "T" : "F";
        addNode(map, "unit " + u.type, label, u, bounds, u.type + " #" + u.id + " " + u.action);
      });

      renderUnits(state.units);
      renderFires(state.fires);
      renderWaters(state.waters);
    }

    function renderUnits(units) {
      const root = document.getElementById("units");
      root.innerHTML = "";
      units.forEach(u => {
        const blocked = u.blocked.length ? u.blocked.join(", ") : "-";
        const target = u.target ? "(" + u.target.x + "," + u.target.y + ") d=" + u.target.distance : "-";
        const el = document.createElement("div");
        el.className = "card unit-card " + u.type;
        el.innerHTML =
          "<div class='row'><strong>#" + u.id + " " + u.type + "</strong><span class='pill'>" + u.state + "</span></div>" +
          "<div class='row'><span class='label'>pos</span><span>(" + u.x + "," + u.y + ")</span></div>" +
          "<div class='row'><span class='label'>action</span><strong>" + u.action + "</strong></div>" +
          "<div class='row'><span class='label'>target</span><span>" + target + "</span></div>" +
          "<div class='row'><span class='label'>water / hp</span><span>" + u.water + " / " + u.hp + "</span></div>" +
          "<div class='row'><span class='label'>seen</span><span>F " + u.seenFires + " / W " + u.seenWaters + "</span></div>" +
          "<div class='row'><span class='label'>stuck</span><span>" + u.stuck + " blocked " + blocked + "</span></div>" +
          "<div class='thought'>" + (u.thought || "") + "</div>";
        root.appendChild(el);
      });
    }

    function renderFires(fires) {
      const root = document.getElementById("fires");
      root.innerHTML = "";
      fires.slice(0, 12).forEach(f => {
        const el = document.createElement("div");
        el.className = "card";
        el.textContent = "(" + f.x + "," + f.y + ") hp=" + f.hp + " age=" + f.age;
        root.appendChild(el);
      });
      if (!fires.length) root.textContent = "No known fires.";
    }

    function renderWaters(waters) {
      const root = document.getElementById("waters");
      root.innerHTML = "";
      waters.slice(0, 8).forEach(w => {
        const el = document.createElement("div");
        el.className = "card";
        el.textContent = "(" + w.x + "," + w.y + ") age=" + w.age;
        root.appendChild(el);
      });
      if (!waters.length) root.textContent = "No known water.";
    }

    loadState();
    setInterval(loadState, 500);
  </script>
</body>
</html>
""";
    }
}
