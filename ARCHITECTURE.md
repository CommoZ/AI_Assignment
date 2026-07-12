# Traffic Simulation — Architecture Reference

A Unity 6.3 (URP) agent-based traffic simulation. Each car is an autonomous agent with driver
parameters (reaction time, aggression, patience, awareness, mood). The goal is to study what
causes congestion across different road environments (grids, intersections, roundabouts, ramps),
with cars routing point-A-to-B via pathfinding. All tuning is live via an on-screen panel.

This document is the whole picture, compressed. Read it and you can continue the project cold.
For the change history and design rationale, see **[DEVLOG.md](DEVLOG.md)**.

---

## 1. The core idea: roads are a waypoint graph

There are **no road meshes with physics** and **no NavMesh**. A road is a **directed graph of
empty-GameObject nodes** (`Waypoint`). A car drives node-to-node, rotating toward its current
target; fine node spacing makes paths look like smooth curves/circles. The visible grey road
surface is a *cosmetic* mesh generated from the graph (`RoadRenderer`) — it has no collider.

`Waypoint` (`Assets/Scripts/Waypoint.cs`) fields:
- `nextWaypoints` — outgoing edges (a fork = a junction; the car picks by route or random).
- `laneNeighbors` — sideways links to parallel lanes (used only for overtaking).
- `controllingLight` — optional `TrafficLight` guarding the approach.
- `stopLineDistance` — how far before the node to stop for a red.
- `isYield` — unsignalled give-way point (roundabout/T entry).
- Registers itself into the global node list at play start (`OnEnable`).

---

## 2. Runtime systems

| Script | Role |
|--------|------|
| `TrafficSimulationManager` | Singleton. Registries of all **cars** and all **waypoint nodes**; jam metrics (count, avg speed, % stopped, avg frustration); collision counter + history graph; **intersection reservation zones**; global controls (pause, timescale, maxCars). Most of its API is `static`. |
| `CarSpawner` | Spawns `CarPrefab` at random spawn waypoints on a timer, up to `maxCars`. Assigns each car a `DriverProfile` from a `DriverPopulation`, and (if `assignDestinations`) a random destination + an A\* route. |
| `CarAI` | The agent. See §3. |
| `SimulationUI` | IMGUI panel (no canvas). All live tuning + the collision graph. |
| `SimulationGizmoSettings` | Static toggles for editor gizmos (waypoints, lane links, sensors, routes). |
| `CameraController` | Free-fly camera: right-drag to look, WASD/QE move, Shift sprint, scroll zoom. Runs on unscaled time. |

---

## 3. `CarAI` — the driving model (`Assets/Scripts/CarAI.cs`)

Rigidbody physics on the ground plane (Y frozen, no gravity). Each `FixedUpdate`:

1. **Steer** toward `currentTarget` (Slerp rotation).
2. **Desired speed** = `maxSpeed × profile.DesiredSpeedFactor`, capped near corners by a
   cornering limit (`cornerSpeed`, only within ~7 m of the bend so cars don't crawl the whole road).
3. **Hazards** cap the target speed via a **safe-speed following model** — a car never travels
   faster than it can brake to match what's ahead, within its follow gap:
   `SafeSpeed(gap, leadSpeed) = √(leadSpeed² + 2·(0.7·braking)·(gap − minFollowGap))`.
   The `0.7` factor plans for slightly weaker braking than the physics can deliver, so it never
   overshoots. Hazards considered: traffic light / stop line, give-way (`isYield`) zones, the
   car ahead (forward `SphereCast`, range auto-extended to cover braking distance), and
   intersection reservation (Perfect mode).
4. **Reaction time** shrinks every gap (`gap − reactionTime·speed`), so a slow-to-react driver
   effectively tailgates. Perfect drivers have zero lag.
5. **Throttle** via a P-controller (`ApplyDriveForces`), scaled by `profile.ThrottleResponse`.
6. **Mood**: `frustration` rises while held up, decays while flowing; feeds back into aggression.
7. **Arrival**: advance along `route` (GPS) or `GetRandomNext()` (sandbox); despawn at route end.
8. **Overtaking**: if a slower car is ahead and a `laneNeighbor` is clear, retarget to it and
   re-plan the route.

Collisions call `TrafficSimulationManager.ReportCollision()` (deduplicated per pair by instance id).

---

## 4. Driver personality (`DriverProfile`, `DriverPopulation`)

`DriverProfile` is a ScriptableObject archetype (Cautious / Average / Aggressive / Distracted /
Perfect) holding traits and all the **trait→behaviour math** as pure accessor methods
(`RollReactionTime`, `FollowGapMultiplier`, `DesiredSpeedFactor`, `ThrottleResponse`,
`SensorRangeMultiplier`, `OvertakeSpeedMargin`, `UpdateFrustration`). `isPerfect` = zero lag, exact
speeds, full awareness, no frustration.

`DriverPopulation` is a weighted mix of profiles (`PickWeighted`), plus `globalOverride` and
`perfectProfile` (the control baseline). `CarSpawner.population` points at one; `SimulationUI`'s
**Perfect mode** flips the whole population + all live cars to `perfectProfile`.

Assets live in `Assets/DriverProfiles/` (generated — see §9). `CarAI` guards every profile call, so
a car with no profile falls back to its raw inspector values.

---

## 5. Pathfinding / GPS routing (`Pathfinder.cs`, `IEdgeCost`)

`Pathfinder.FindPath(start, goal, IEdgeCost)` = **bidirectional A\*** over the waypoint graph
(searches forward from start and backward from goal, meets in the middle). Heuristic = Euclidean
distance; default cost = segment length (`DistanceCost`). The reverse-adjacency map is cached and
rebuilt only when the node count changes (`InvalidateCache()` after runtime rebuilds).

`IEdgeCost` is the extension seam: a future `CongestionCost` (travel time from live density) drops
in for **congestion-aware rerouting** without touching cars or the pathfinder — this is the planned
"cars react to traffic" upgrade. Cars follow a static shortest path today.

---

## 6. Building roads

Roads are authored by `[ExecuteAlways]` builder components (context-menu **"Rebuild…"**). They only
create/lay out/link waypoints — no geometry.

| Builder | Produces |
|---------|----------|
| `CircleTrackBuilder` | Concentric ring lanes (closed loops). The `CircleTraffic` scene. |
| `MultiLaneRoadBuilder` | Straight multi-lane road (open chains + lane neighbours). |
| `IntersectionBuilder` | 4-way or T junction: per-arm inbound/outbound lanes, turn forks, traffic lights (4-way) or give-way (T). |
| `RoundaboutBuilder` | Circulating ring + radial arms; entries are give-way (`isYield`). |
| `RampBuilder` | On-ramp (merge, give-way) / off-ramp (diverge). |
| `CityGridBuilder` | Full N×M grid of intersections joined by two-way streets. See §7. |

**Stitching modules together:** each builder places `RoadConnector` markers (Entry/Exit) at its
boundary nodes. `RoadNetworkStitcher` (context menu **"Stitch connectors"**, or `RoadConnector.StitchAll`)
wires each Exit to the nearest matching Entry by appending to `nextWaypoints`. Snap two modules
close and stitch to connect them.

### `CityGridBuilder` intersection design (important)
Each intersection is a hub of **ports**: for every direction with a neighbour it has an *outbound*
port (traffic leaving) and an *inbound* port (traffic arriving), offset to the right of travel so
opposing lanes never share a point (**no head-on at the centre**). Inbound ports fork to every
*other* direction's outbound port (straight/left/right, never U-turn). Two-way streets are two
opposing single-lane chains between neighbours. Every hub also gets an `IntersectionZoneMarker`
(see §7). Edge/corner intersections naturally become T/2-way.

---

## 7. Perfect mode: collision-free junctions without traffic lights

With perfect information, lights are unnecessary — cars self-organise. `SimulationUI` **Perfect
mode** turns lights off (forces green), sets every car to the perfect profile, and enables
**intersection reservations** (`TrafficSimulationManager.ReservationsEnabled`).

Each junction carries an `IntersectionZoneMarker` → a reservation zone with two radii:
- **detection radius** (finds the entry/exit port nodes a car will use), and
- **core radius** (`= 0.45 × detection`, the central crossing area; a car is only "committed" once
  it enters the core).

Reservation logic (`CarAI.ApplyReservation` + manager helpers):
1. A car computes its **trajectory chord** through the junction (entry port → exit port).
2. It **registers a claim** `{car, entry, exit, distToEntry}` on the zone.
3. **Two trajectories conflict** if they **merge** (share an exit) OR pass within ~1.6 m of each
   other (`SegmentsCross` — a min-distance test, robust to grazing turns). Sharing an entry
   (diverging) is safe.
4. Among conflicting claims, priority = **closer to entry wins**, ties broken by instance id — a
   strict total order, so exactly one proceeds and there's **no deadlock**.
5. Before committing, a car also requires its **exit isn't jammed** (a *stopped* car sitting in it),
   so it never blocks the box. A merely-moving car at the exit doesn't hold it up.

Result: **non-crossing paths pass simultaneously; crossing/merging paths take turns; nobody
collides.** Verify with the **Collisions** graph in the panel — it stays flat in Perfect mode.

---

## 8. Traffic lights (normal mode)

`TrafficLight` (R/Y/G state machine), `TrafficLightGroup` (coordinates two out-of-phase groups at
an intersection), `ClickableTrafficLight` (click to toggle). Control precedence:
`manualControl` (click) > `ExternallyDriven` (group) > auto-cycle — the group never overrides a
light a user grabbed. `RequiresStop(distance, commitDist)` implements a dilemma zone: on yellow a
committed car (too close to stop) clears instead of freezing.

---

## 9. Scenes, assets, and how to run

**Scenes are generated by an editor menu, not hand-authored.** The generator builds everything
wired correctly via the Unity API (avoids fragile `.unity` YAML). Menu **`Traffic Sim ▸`**
(`Assets/Scripts/Editor/TrafficDemoGenerator.cs`):

- **Generate Driver Assets** → writes the 5 profiles + `Mixed.asset` population to `Assets/DriverProfiles/`.
- **Generate City Scene** → `Assets/Scenes/City.unity`: signalised 4×4 grid demoing GPS routing.
- **Generate Junctions Scene** → `Assets/Scenes/Junctions.unity`: a 4-way + a roundabout.

Each generated scene has: ground, free camera, sun, a `Systems` object (manager + UI + gizmo
settings), the builder(s), a `Spawner`, and a `Roads` object (`RoadRenderer`).

**To run:** open a generated scene → Play. Use the panel to tune; fly with the camera controls.
**Legacy sandbox scenes** (`Traffic`, `CircleTraffic`, `TestRoads`) predate the generator.

> ⚠️ **Regeneration rule.** Changes to *builders, the generator, or scene contents* require
> re-running the menu to take effect (scenes are baked). Changes to *runtime code only*
> (`CarAI`, manager logic, UI) just need a recompile. In particular, **reservation/Perfect mode
> needs `IntersectionZoneMarker`s, which only exist in regenerated scenes.**

---

## 10. The live control panel (`SimulationUI`)

Pause · time scale · **Max cars** · Clear all cars · **Collisions** counter + Reset + live
bar-graph over time · spawn interval · **Perfect mode** · route-gizmo toggle · signal green time
(live) · **city grid size + Rebuild** (city scene). All apply at runtime.

---

## 11. Extending the project — where things go

- **New road type** → new `[ExecuteAlways]` builder in the CircleTrackBuilder style; place
  `RoadConnector`s at its boundaries and an `IntersectionZoneMarker` at any crossing region.
- **Congestion-aware routing** → implement `CongestionCost : IEdgeCost` (travel time from live
  density) and have cars periodically call `CarAI.ReplanRoute()`; the seam already exists.
- **New driver behaviour** → add a trait + accessor to `DriverProfile`; consume it in `CarAI`
  (guard the null-profile case).
- **New metric / graph** → add to `TrafficSimulationManager`, surface in `SimulationUI`.
- **A demo scene** → add a `[MenuItem]` in `TrafficDemoGenerator`.

## 12. Gotchas (read before debugging)

- **Nothing on a lane may have a collider** except cars (the forward sensor + overlap checks scan
  everything and would treat a light/road collider as an obstacle). Road mesh has no collider;
  light bulbs have their colliders stripped.
- **Static registries** (cars, nodes, zones) rely on Unity's domain reload clearing them between
  play sessions (the default).
- **Materials for generated meshes** are cloned from a throwaway primitive's default material so
  they render under URP (`Shader.Find` for URP shaders is unreliable).
- **Prefab fields**: a new public field added to `CarAI` uses its C# initializer default on the
  existing prefab unless the prefab was re-serialized — set safety-critical values in code if unsure.
- **`CarPrefab`** must have `CarAI` + `Rigidbody` + a `Collider`; the spawner loads it from
  `Assets/Prefabs/CarPrefab.prefab`.
