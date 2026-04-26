# bitWiser Firefighting AI Bot

A strategic C# firefighting simulation bot that controls a 3-unit emergency response team made up of a drone, a fire truck, and a firefighter.

The AI is designed to explore the map, detect fires, remember important locations, refill water intelligently, avoid obstacles, and coordinate units to extinguish fires as efficiently as possible.

---

## Project Overview

This project implements an AI brain for a grid-based firefighting simulation.  
The bot receives information about the current game state and decides what each unit should do every tick.

Each unit has a different role:

- **Drone / Copter**: Fast scout with wide vision, useful for discovering fires and water sources.
- **Fire Truck**: Large water capacity unit responsible for heavy firefighting and refilling.
- **Firefighter**: Ground unit with strong direct firefighting behavior and no water refill dependency.

The AI combines memory, pathfinding, role-based behavior, water management, and stuck recovery to make the team act smarter than a simple reactive bot.

---

## Key Features

### Role-Based Team Strategy

Each unit has its own behavior pattern and exploration sector.

- Drone explores upper areas and scouts ahead.
- Truck patrols the middle area and supports firefighting.
- Firefighter covers deeper areas and attacks fires aggressively.

This helps the team spread out instead of all units chasing the same target.

---

### Smart Firefighting Logic

The bot can:

- Detect visible fires.
- Remember previously seen fires.
- Prioritize nearby fires.
- Prioritize fire clusters.
- Move to attack positions around a fire.
- Avoid wasting movement when already in attack range.
- Continue fighting until water is empty.

The AI does not simply chase the nearest fire. It uses scoring logic to choose valuable targets.

---

### Water Management

Drone and truck units have limited water, so the AI includes refill decision-making.

The bot can:

- Remember known water sources.
- Move toward reachable water.
- Refill when empty or critically low.
- Refill on the way to a fire if water is nearby.
- Avoid abandoning a fire too early.
- Prevent teammate blocking near water.
- Correctly allow ground units to enter water tiles when refilling.

This fixes a common issue where water tiles are treated as blocked and units fail to refill.

---

### A* Pathfinding

The movement system uses A* pathfinding with a priority queue.

The pathfinder handles:

- Obstacles
- Walls
- Fires
- Water tiles
- Teammates
- Temporarily blocked cells
- Special water-entry behavior when seeking refill

This makes the bot more reliable than simple greedy movement.

---

### Memory System

The AI keeps track of important information across ticks.

It remembers:

- Known fires
- Known water sources
- Explored areas
- Cold/unvisited cells
- Assigned fire targets
- Blocked cells
- Unit-specific plans

This allows units to make decisions based on more than just what they currently see.

---

### Stuck Detection and Recovery

The bot detects when a unit is not moving after issuing movement commands.

When stuck, it can:

- Try alternate movement
- Release stale fire claims
- Reset exploration targets
- Temporarily block problematic cells
- Avoid marking water, fire, or teammate cells as permanent walls

This prevents units from repeating the same failed command forever.

---

### Dashboard Support

The project includes dashboard-related files for visualizing or debugging the simulation.

Relevant files include:

- `dashboard.html`
- `DashboardWriter.cs`
- `ConsoleRenderer.cs`
- `dashboard-state.js`

These help inspect game state, unit behavior, and AI decisions during development.

---

## Main Files

| File | Purpose |
|---|---|
| `BotAI.cs` | Main AI decision-making logic |
| `GameState.cs` | Stores known fires, water, units, map state, and memory |
| `Models.cs` | Data models used by the simulation |
| `Program.cs` | Main application entry point |
| `DashboardWriter.cs` | Writes dashboard/debugging data |
| `ConsoleRenderer.cs` | Console-based rendering/debug output |
| `dashboard.html` | Dashboard viewer |
| `FireClient.csproj` | C# project file |
| `FireClient.sln` | Visual Studio solution file |
| `Proto/` | Protocol or generated communication files |
| `Assets/` | Project assets |
| `Viewer/` | Viewer-related files |

---

## AI States

The bot uses finite-state decision logic.

Common states include:

- `Scouting`
- `Seeking`
- `SeekingWater`
- `Pursuing`
- `Extinguishing`
- `Supporting`

Each tick, the AI selects a state for each unit and then executes the correct action.

Example decisions:

- Move toward a fire
- Extinguish a fire
- Refill with water
- Explore an unvisited area
- Move around a blocked teammate
- Recover from being stuck

---

## Strategy Summary

The AI follows this general priority order:

1. If a fire is adjacent and the unit can fight, extinguish it.
2. If the unit has no water, seek water.
3. If water is critically low, refill unless already fighting a nearby fire.
4. If a visible fire exists, pursue it.
5. If a remembered fire exists, move toward it.
6. If no fire is known, explore cold/unvisited areas.
7. If stuck, recover with alternate movement.

This makes the bot reactive when needed, but also strategic over time.

---

## Technologies Used

- C#
- .NET
- A* pathfinding
- Priority queue search
- Finite-state AI
- Grid-based simulation logic
- Dashboard/debug visualization

---

## How to Run

Make sure you have the .NET SDK installed.

From the project directory, run:

```bash
dotnet build
