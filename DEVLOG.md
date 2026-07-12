# Dev Log & Key Insights

Change history and hard-won insights, so context survives even when the conversation is compacted.
Companion to **[ARCHITECTURE.md](ARCHITECTURE.md)** (the "how it works" reference).

---

## Iteration status

- **Iter 0–1 (base, by the author):** waypoint-graph roads, rigidbody `CarAI`, `CarSpawner`,
  `TrafficSimulationManager`, `CircleTrackBuilder` + `MultiLaneRoadBuilder`, traffic lights,
  IMGUI `SimulationUI`, gizmo settings. Random-walk routing.
- **Iter 2 (base, authored but disconnected):** `DriverProfile` / `DriverPopulation` existed with
  full behaviour math but **nothing consumed them** — zero runtime effect.
- **Iter 2-wiring → 4 (this work):** everything below.

---

## What was added / changed

### Iteration 2 completion — personalities actually drive the car
- `CarAI` now consumes a `DriverProfile`: reaction lag, follow gap, desired speed, throttle
  sharpness, sensor range, overtake keenness, dynamic `frustration`.
- `CarSpawner` draws archetypes from a `DriverPopulation` and rolls each car's top speed.
- `SimulationUI` gained a **Perfect mode** control baseline and avg-frustration readout.

### Iteration 3 — road types + right-of-way + cornering
- Fixed the traffic-light bug: ownership precedence (`manualControl` > group > auto) and a
  dilemma-zone yellow (`TrafficLight.RequiresStop`).
- New builders: `IntersectionBuilder` (4-way/T), `RoundaboutBuilder`, `RampBuilder`.
- `RoadConnector` + `RoadNetworkStitcher` to snap modules together.
- `Waypoint.isYield` give-way logic; angle-based cornering speed limit in `CarAI`.

### Iteration 4 — city + GPS routing
- `Pathfinder` (bidirectional A\*) + `IEdgeCost` seam; per-car `destination`/`route`.
- `CityGridBuilder` with per-intersection **ports** (opposing lanes offset → no head-on).
- Route gizmos.

### Presentation & tooling
- `RoadRenderer` — visible road-surface mesh from the graph (no collider).
- `CameraController` — free-fly camera.
- `TrafficDemoGenerator` (Editor) — generates driver assets + City/Junctions scenes via the API.
- `SimulationUI` grew: max cars, clear cars, signal green time, city rebuild, collision graph.

### The safe-speed following model (replaced the original proportional slowdown)
Cars never travel faster than they can brake to match what's ahead:
`SafeSpeed = √(lead² + 2·(0.7·braking)·(gap − minFollowGap))`. Sensor range auto-extends to the
braking distance. This killed the rear-end pile-ups. Reaction time is modelled as a **gap
reduction** (`gap − reactionTime·speed`) rather than a perception buffer (the buffer caused
startup stalls and oscillation).

### Perfect mode — the hard part (evolved over several passes)
Goal: **no collisions, no traffic lights, and non-crossing paths pass simultaneously.**
Final design = **conflict-aware intersection reservation** (see ARCHITECTURE §7). The journey:
1. *Single-occupancy per junction* → collision-free but over-serialised and buggy when toggled
   mid-run (cars caught inside overlapped).
2. Added a **core vs detection radius** split so a *waiting* car (stopped at the edge) is never
   mistaken for a *committed* one (inside the core).
3. Replaced single-occupancy with **per-trajectory claims + conflict test**: two chords conflict
   only if they cross/merge, so non-crossing movements go together.
4. **Box-blocking** caused a fresh pile-up → added an exit check.
5. The exit check was too strict (blocked on *any* nearby car) → **gridlock**. Fixed to block only
   on a genuinely **stopped** car at the exit (`ExitJammed`).
6. T-junctions still collided → the conflict test missed **merges** (same exit) and **grazing
   turns**. Replaced the thin-line crossing test with a **min-distance test** (`< 1.6 m` = conflict)
   plus explicit merge handling.

Priority = closer-to-entry wins, ties by instance id → a strict total order → **no deadlock**.

### Collision instrumentation
`TrafficSimulationManager` counts collisions (deduped per pair by id) and samples a cumulative
history; `SimulationUI` draws it as a live bar-graph with a Reset. This is the objective check
that Perfect mode is collision-free.

---

## Key insights & decisions (the "why")

- **Roads = pure waypoint graph.** Simplest thing that supports pathfinding, arbitrary junctions,
  and cheap authoring. Visuals are a separate cosmetic mesh.
- **Colliders only on cars.** Any collider on a lane is seen as an obstacle by the sensor/overlap
  checks. Road mesh has none; light bulbs have theirs stripped.
- **Scenes are generated, not committed as hand-written YAML.** Regenerate after builder/scene
  changes; recompile-only for runtime-code changes. Reservation needs regenerated scenes (markers).
- **Reaction time as gap-reduction** beats a perception delay buffer for stability.
- **Plan for weaker braking than physics delivers** (`0.7×`) so commanded decel is always
  achievable — otherwise cars overshoot and "collide" from control lag, not driver behaviour.
- **Perfect-mode conflict = geometry, not fixed phases.** A min-distance test between trajectory
  chords is robust where an exact-crossing test silently misses grazing/merging paths.
- **Strict priority ordering (distance, then id)** is what makes reservations deadlock-free.
- **`IEdgeCost` seam is the intended path to congestion-aware "cars react to traffic."** Static
  shortest-path first (the perfect-information baseline the author wanted), dynamic rerouting later.

---

## Known limitations / next steps

- **Congestion-aware rerouting** not built yet — implement `CongestionCost : IEdgeCost` + periodic
  `CarAI.ReplanRoute()`. The seam is ready.
- **Perfect mode throughput is conservative** (safe over fast); under very heavy spawn it queues.
  Could allow more simultaneous passage with a smarter (time-windowed) reservation.
- **Junction right-of-way ignores major/minor road** — priority is purely by proximity. Fine and
  collision-free, but not "main road always wins."
- **Roundabout/ramp** get less testing than the city grid; reservation zones are added at 4-way
  and city intersections, not roundabouts (those rely on `isYield`).
- **No data export** — metrics are live-only (no CSV/time-series dump).
- Road mesh draws one quad per lane edge (opposing lanes = two strips, junctions = filled patch);
  no lane markings.

---

## Fast orientation for a new contributor

1. Read `ARCHITECTURE.md`.
2. Run **`Traffic Sim ▸ Generate City Scene`**, open `Assets/Scenes/City.unity`, press Play.
3. Toggle **Perfect mode** and watch the **Collisions** graph stay flat while cars self-organise.
4. Start point for most work: `CarAI.cs` (behaviour), `TrafficSimulationManager.cs` (registries +
   reservations + metrics), `CityGridBuilder.cs` (the map), `Pathfinder.cs` (routing).
