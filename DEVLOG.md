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
- **Iter 2-wiring → 4:** driver profiles wired in, road types + right-of-way, city + GPS routing.
- **Iter 5:** Environment & Scenarios update — env knobs, seeded/patterned demand, incidents,
  throughput/trip-time metrics + CSV.
- **Iter 6 (this work):** congestion-aware routing, car selection/inspector, highway + ring scenes.
  See below.

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

### Iteration 5 — Environment & Scenarios update
Turned the sim from "watch random cars" into something you can actually run experiments on.
- **Environment knobs** (`TrafficSimulationManager` statics, read by `CarAI`): `RoadGrip` (weather →
  grip-scaled braking + traction), `GlobalSpeedLimit`, `DemandMultiplier` (rush-hour surge). Grip is
  the interesting one — weaker brakes lengthen stopping distance and, because `SafeSpeed` uses the
  grip-scaled brake, cars keep bigger gaps *for free*. The catch (caught in review): three hardcoded
  horizons assumed dry grip, so low grip silently broke Perfect mode. Made them grip-aware
  (reservation window `16/grip`, corner window `7/grip`, `cornerSpeed·√grip`) and floored grip at 0.5.
- **Reproducible demand** — the old spawner used unseeded `UnityEngine.Random` for origins *and*
  destinations, and `RandomNodeExcept` sent cars to arbitrary mid-street nodes. Replaced with an
  isolated seeded `System.Random` on the spawner (draw OD+profile once per car, retry-not-reroll when
  blocked, so physics/frame timing can't shift the draw order) + `RestartScenario` for deterministic
  replay. Destinations now come only from real ports. `DemandPattern` = Uniform / Hotspot (downtown
  sink) / Commuter (inbound⇄outbound phase), weighted by cached distance-from-centroid.
- **Incidents** — click-to-stall (`IncidentController`, runtime-added) + break-down/clear buttons.
  A stalled car blocks its lane; followers detour via `AvoidNodeCost : IEdgeCost` (the first real use
  of the routing seam). In Perfect mode a stall releases its reservation unless committed in the core,
  or it would freeze all cross-traffic.
  - *Follow-up:* the graph detour alone left cars already on the blocked street trapped (single-lane,
    no U-turn edges — no graph path exists from there). Added a **physical side-pass**: after ~1.5 s
    stuck, a follower checks the side corridor is clear (incl. oncoming further out), then steers a
    rolling offset point around the stall, ignoring it in the forward sensor (else `SafeSpeed` pins it
    behind) and widening the arrival radius so offset-passed nodes still advance the route. Sensor
    switched to `SphereCastAll` to support the ignore. Queues unwind one car at a time.
- **Metrics** — throughput (arrivals/60 s of sim time), avg trip time, completed trips; a per-second
  `Sample` buffer drives three live graphs (collisions / avg speed / throughput) and a **CSV export**
  to `persistentDataPath`. Trip completion is counted only when `currentTarget == destination` (not
  every dead-end).

### Iteration 6 — Congestion routing, car inspector, highway & ring scenes
Turns the sim into a traffic-engineering tool (add/remove roads, watch cars re-route, measure).
- **Congestion-aware routing** — finally uses the `IEdgeCost` seam for its intended purpose. The
  1 Hz sampler builds an EMA-smoothed map of cars-per-target-node (`NodeCongestion`); `CongestionCost`
  = `distance × (1 + weight × congestion)` (stays ≥ distance → heuristic still admissible). Cars plan
  with `ActiveEdgeCost` (spawn, `ReplanRoute`, `TrySetDestination`) and re-plan every ~4 s **staggered
  by instance id** (no RNG → reproducible demand intact). EMA + stagger + moderate default weight damp
  route oscillation. This is the A/B tool: same seed, routing off vs on → compare throughput/avg speed.
- **Car selection & inspector** — replaced click-to-stall (`IncidentController` deleted) with
  `CarSelectionController`: left-click selects (cyan tint + route LineRenderer + inspector window),
  left-click a road re-routes the selected car (ground via math-plane y=0 → `NearestNode` →
  `TrySetDestination`), Esc deselects. Inspector edits current/max speed live and has break-down/repair.
  Selected tint sits atop the `UpdateBodyColor` priority. Breakdowns now come from buttons, not clicks.
- **New scenes** — `Generate Highway Scene` (long 3-lane `MultiLaneRoadBuilder` + on-ramp merge /
  off-ramp diverge, wired with direct `LinkTo(NearestWaypoint(...))` edges since the road builder
  places no connectors; two spawners) and `Generate Ring Scene` (single-lane `CircleTrackBuilder`
  closed loop, no despawn → density set by Max cars → **phantom stop-and-go jams**, especially after a
  brief breakdown). Builders don't auto-rebuild at runtime, so the manual ramp edges persist in the
  saved scene.
  - *Follow-up (merge deadlock):* the first on-ramp ended **exactly on** a highway node (collision
    knot) and kept its `isYield`, whose "any car within 4 m" check never clears against continuous
    highway flow → permanent gridlock at the merge. Fixed by ending the ramp just outside the lane,
    linking to a node a little **downstream** (cars merge moving forward), and clearing `isYield` so
    it zippers in via car-following. Roundabouts keep their yield (perpendicular cross traffic, where
    the zone check is right). **Re-generate the Highway scene** to pick this up.
  - *Follow-up (corner wedges):* after a collision shoved a car past a corner node, it tried to
    U-turn back to the now-behind node and wedged against the queue. Fixed with (a) an **overshoot
    advance** — if the car is past the plane through the node along the leg it drove in on, advance
    instead of doubling back (position-based, robust to the car being spun); and (b) a gated
    **stuck-recovery** — a car stopped >4 s with clear road ahead (front of a wedge) skips to the
    next node to free the queue. Recovery is suppressed for legitimate stops (car close ahead, red
    light/yield, breakdown, or waiting on a Perfect-mode reservation) so it never runs a light or
    breaks the collision-free guarantee.

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
- **Data export** — done (per-second CSV to `persistentDataPath`); only the sampled series, not
  per-car traces.
- **Congestion-aware rerouting** — done (`CongestionCost` from the live EMA density map, periodic
  staggered replan). Weight/oscillation are tuned by feel, not calibrated against real flow data.
- **Reproducibility** covers the demand sequence + archetype mix, not exact car positions (Unity
  physics isn't bit-identical). Per-car reaction-time rolls still use `UnityEngine.Random`.
- Road mesh draws one quad per lane edge (opposing lanes = two strips, junctions = filled patch);
  no lane markings.

---

## Fast orientation for a new contributor

1. Read `ARCHITECTURE.md`.
2. Run **`Traffic Sim ▸ Generate City Scene`**, open `Assets/Scenes/City.unity`, press Play.
3. Toggle **Perfect mode** and watch the **Collisions** graph stay flat while cars self-organise.
4. Start point for most work: `CarAI.cs` (behaviour), `TrafficSimulationManager.cs` (registries +
   reservations + metrics), `CityGridBuilder.cs` (the map), `Pathfinder.cs` (routing).
