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
| `TrafficSimulationManager` | Singleton. Registries of all **cars** and all **waypoint nodes**; jam metrics (count, avg speed, % stopped, avg frustration, **throughput, avg trip time, completed trips**); collision counter; a **time-series sample buffer** feeding the graphs + **CSV export**; **intersection reservation zones**; **global environment knobs** (`RoadGrip`, `GlobalSpeedLimit`, `DemandMultiplier`); **scenario seed + `RestartScenario`**; **incident helpers** (`StallRandomCar`, `ClearAllStalls`). Global controls (pause, timescale, maxCars). Most of its API is `static`. |
| `CarSpawner` | Spawns `CarPrefab` on a timer, up to `maxCars`. Draws each car's **origin, destination and archetype from an isolated seeded `System.Random`** (reproducible demand) per a `DemandPattern` (Uniform / Hotspot / Commuter). Destinations are real intersection ports. Assigns a `DriverProfile` from a `DriverPopulation` and an A\* route. See §12. |
| `CarAI` | The agent. See §3. |
| `CarSelectionController` | Left-click a car to **select** it (drives the inspector + cyan tint + route line); with one selected, left-click a road to **re-route** it there; Esc deselects. Added at runtime by `SimulationUI` (no scene regen). Mirrors `ClickableTrafficLight`. |
| `SimulationUI` | IMGUI panel (no canvas). All live tuning, scenario/environment/incident controls, metric readouts + graphs, CSV export. |
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

`IEdgeCost` is the extension seam, and three implementations exist: `DistanceCost` (default),
`AvoidNodeCost` (incident detours, §14), and `CongestionCost` (**congestion-aware routing**, §15).
`TrafficSimulationManager.ActiveEdgeCost` picks distance vs congestion cost globally; cars call
`FindPath` with it, so switching routing modes touches neither the cars nor the pathfinder.

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
- **Generate Highway Scene** → `Assets/Scenes/Highway.unity`: a long 3-lane road, on-ramp merge
  (give-way) + off-ramp diverge, two spawners (main + ramp). Overtaking and merging demo. Ramps are
  wired to the road with direct edges (`LinkTo` to the `NearestWaypoint`), since `MultiLaneRoadBuilder`
  places no connectors.
- **Generate Ring Scene** → `Assets/Scenes/Ring.unity`: a single-lane closed loop (no despawn);
  raise Max cars and briefly break a car down to trigger a **phantom (stop-and-go) jam**.

Each generated scene has: ground, free camera, sun, a `Systems` object (manager + UI + gizmo
settings), the builder(s), a `Spawner`, and a `Roads` object (`RoadRenderer`).

**To run:** open a generated scene → Play. Use the panel to tune; fly with the camera controls.
**Legacy sandbox scenes** (`Traffic`, `CircleTraffic`, `TestRoads`) predate the generator.

**Scene shortcuts** (`SceneSwitcher`, runtime-added by `SimulationUI`): **F1** City · **F2** Junctions
· **F3** Highway · **F4** Ring · **F5** reset (reload current scene) — via `SceneManager`. Function
keys are used so they don't collide with typing the seed. Target scenes must be in **Build Settings**:
the generator registers each scene on save, or run **`Traffic Sim ▸ Add Generated Scenes To Build
Settings`** once for pre-existing scenes. `SceneSwitcher` warns if a scene isn't registered.

> ⚠️ **Regeneration rule.** Changes to *builders, the generator, or scene contents* require
> re-running the menu to take effect (scenes are baked). Changes to *runtime code only*
> (`CarAI`, manager logic, UI) just need a recompile. In particular, **reservation/Perfect mode
> needs `IntersectionZoneMarker`s, which only exist in regenerated scenes.**

---

## 10. The live control panel (`SimulationUI`)

A scrolling IMGUI window, everything live:
- **Time** — pause · time scale.
- **Metrics** — car count · avg speed · % stopped · frustration · **throughput (cars/min)** · **avg
  trip time** · **completed trips** · **collisions**; three time-series bar-graphs (collisions, avg
  speed, throughput) + **Export CSV** (writes to `Application.persistentDataPath`). Max cars · Clear
  all cars · collisions Reset.
- **Scenario** — **seed + Restart scenario** (deterministic replay) · **demand pattern** selector
  (Uniform / Hotspot / Commuter) · commuter **phase** toggle. See §13.
- **Environment** — **road grip** (weather) · **global speed limit** · **rush-hour demand** multiplier.
  See §13.
- **Routing** — **congestion-aware routing** toggle · **congestion weight**. See §15.
- **Incidents** — Break down random car · Clear all · live stalled count. See §14.
- Spawn interval · **Perfect mode** · route-gizmo toggle · signal green time · **city grid + Rebuild**.

A second **Selected Car** window appears when you left-click a car (§16): driver/origin/destination,
live + editable current/max speed, break down/repair, deselect.

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

## 13. Environment & Scenarios (reproducible experiments)

**Environment** — three global statics on `TrafficSimulationManager`, read by `CarAI`/`CarSpawner`,
all no-ops at their defaults:
- `RoadGrip` (0.5–1) scales **braking + traction** (`CarAI.EffectiveBraking`, grip-scaled traction).
  Lower grip lengthens stopping distance, so cars keep bigger gaps automatically via `SafeSpeed`. The
  fixed hazard **horizons are grip-aware** so Perfect mode stays collision-free on wet roads: the
  reservation detection window (`16/grip`), the corner slow-down window (`7/grip`), and the cornering
  speed (`cornerSpeed·√grip`).
- `GlobalSpeedLimit` (m/s, 0 = off) caps cruise speed.
- `DemandMultiplier` scales the spawn rate (rush-hour surge).

**Scenarios (reproducible demand)** — `CarSpawner` owns a seeded `System.Random` (isolated from
`UnityEngine.Random`, whose consumption order is entangled with physics/frame timing). Each car's
origin, destination and archetype are **drawn once** from that stream; a momentarily-blocked origin
is **retried, not re-rolled**, so the draw order never shifts.
`TrafficSimulationManager.RestartScenario` re-seeds every spawner + wipes metrics for a clean
deterministic replay. `DemandPattern`:
- **Uniform** — origins/destinations uniform over the ports.
- **Hotspot** — destinations weighted toward the network centre (a downtown sink).
- **Commuter** — `inboundPhase` flips morning (edges→centre) vs evening (centre→edges).

Weighting uses each port's cached distance-from-centroid (`RefreshDemandCache`, recomputed when
`spawnPoints` is reassigned by a city rebuild). Destinations are drawn **only from real intersection
ports** — this replaces the old `RandomNodeExcept`, which sent cars to arbitrary mid-street nodes.
(Caveat: the *demand sequence* is reproducible; Unity physics isn't bit-identical, so exact positions
may drift slightly. Patterns only apply where `assignDestinations` is on, i.e. the City scene.)

## 14. Incidents (breakdowns)

`CarAI.StallFor` / `ClearStall` / `ToggleStall` break a car down: it holds position (dark tint) and
stays a physical obstacle. In Perfect mode a stalled car **releases its junction reservation** unless
it's committed inside the core, so it never freezes cross-traffic. Trigger a breakdown from the panel
("Break down random car") or the selected-car inspector's **Break down / Repair** button (§16).

Blocked followers escape in two ways, in order:
1. **Physical side-pass** (`enableStallPass`, ~1.5 s stuck): the follower checks the side corridor
   (left/oncoming lane first, then right) is clear of cars and approaching oncoming traffic, then
   steers along a rolling point offset `passOffset` (~one lane) beside the stall — out, alongside,
   and back in one arc. While passing, the stalled car is **ignored by the forward sensor** (that's
   the point), speed is capped at `cornerSpeed`, and the **arrival radius is widened** so lane nodes
   slid past while offset still advance the route. Roads have no colliders, so leaving the waypoint
   line is physically free. Queued cars unwind one at a time — each starts its own pass only once it
   directly senses the stall.
2. **Graph detour** (`DetourDelay`, fallback): `RerouteAvoiding` re-plans with `AvoidNodeCost`
   (penalises the blocked node) — only useful for cars that can still turn off before the blocked
   stretch; a car already on a single-lane street has no graph alternative, hence the side-pass.

## 15. Congestion-aware routing

Cars can route around jams instead of following a fixed shortest path (the intended "cars react to
traffic" upgrade, built on the `IEdgeCost` seam):
- **Live congestion map** (`TrafficSimulationManager`): the 1 Hz sampler counts cars per
  `currentTarget` node and EMA-smooths it (`Lerp(prev, count, 0.4)`, decays to 0 and drops empty
  nodes). `NodeCongestion(w)` reads it — the "weighted road node".
- **`CongestionCost : IEdgeCost`** (`Pathfinder.cs`):
  `cost = distance × (1 + CongestionWeight × NodeCongestion(to))`. Cost ≥ distance, so the Euclidean
  heuristic stays admissible and paths stay optimal under the cost.
- **`ActiveEdgeCost`** = `CongestionCost.Default` when `CongestionAwareRouting`, else `null`
  (= `DistanceCost`). Used by `CarSpawner.DrawSpawn`, `CarAI.ReplanRoute`, and
  `CarAI.TrySetDestination`.
- **Periodic replan**: with routing on, each routed car re-plans every ~4 s, staggered by instance id
  (`(id & 0xF) × 0.25 s`) — no RNG, so reproducible demand is untouched. EMA + stagger + a moderate
  default weight (2) damp oscillation. Toggle + weight live in the panel; watch routes bend around
  jams with route gizmos on. This is the tool for "fix congestion by adding/removing roads": rebuild
  the network, compare throughput/avg-speed with routing on.

## 16. Car selection & inspector (`CarSelectionController`)

Left-click a car → **select** it: cyan tint (top of `UpdateBodyColor`'s priority: selected > stalled
> profile tint > speed), a cyan route `LineRenderer` (material via `RoadRenderer`'s primitive-clone
pattern, no `Shader.Find`), and a **Selected Car** inspector window. With a car selected, left-click
anywhere on a road → the ground point (math-plane y=0, no collider dependency) → `NearestNode` →
`CarAI.TrySetDestination` (pathfinds with `ActiveEdgeCost`; random-walk cars gain a destination and
then arrive/despawn). Esc deselects; selection auto-clears if the car despawns.

Inspector shows driver/origin/destination/trip-time and exposes **live** current speed
(`SetCurrentSpeed`, a nudge the P-controller then follows) and max speed sliders, plus Break down /
Repair, and **Delete car** (deselect then `Despawn` — not counted as an arrival). Clicks over either
IMGUI window are ignored (rect guards). `CameraController` uses only the right mouse button, so
left-click never conflicts.
