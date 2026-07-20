using System.Collections.Generic;
using UnityEngine;

/// <summary>Origin/destination demand pattern the spawner draws each car from.</summary>
public enum DemandPattern
{
    Uniform,  // origins and destinations uniformly random over the ports
    Hotspot,  // destinations biased toward a "downtown" (the centre of the network)
    Commuter, // rush-hour flow: inbound = edges->centre, outbound = centre->edges (phase toggled live)
}

/// <summary>
/// Spawns car prefabs at spawn waypoints at a configurable rate, drawing each car's origin,
/// destination and driver archetype from a <b>seeded RNG</b> so a scenario is reproducible for a
/// given seed. A global cap (<see cref="TrafficSimulationManager.maxCars"/>) prevents runaway
/// spawning, and <see cref="TrafficSimulationManager.DemandMultiplier"/> scales the rate (rush hour).
///
/// Reproducibility: origin/destination/profile are drawn once per car from an isolated
/// <see cref="System.Random"/> stream (not <c>UnityEngine.Random</c>, whose consumption order is
/// entangled with physics and frame timing). If a drawn origin is momentarily blocked, the same
/// draw is retried on a later tick rather than re-rolled, so the draw sequence never shifts.
/// </summary>
public class CarSpawner : MonoBehaviour
{
    [Tooltip("Prefab that has a CarAI component on it.")]
    public CarAI carPrefab;

    [Tooltip("Possible starting waypoints (intersection departure ports). Also the destination pool.")]
    public List<Waypoint> spawnPoints = new List<Waypoint>();

    [Tooltip("Seconds between spawn attempts.")]
    [Min(0.05f)] public float spawnInterval = 1.5f;

    [Tooltip("Minimum clear distance around a spawn point before spawning there.")]
    public float minClearRadius = 2f;

    [Header("Driver population")]
    [Tooltip("Weighted mix of driver archetypes to draw from. Leave empty to spawn plain cars using the prefab's own values.")]
    public DriverPopulation population;

    [Header("GPS routing")]
    [Tooltip("If ticked, each car is given a destination and drives a shortest path (bidirectional A*) to it, then despawns on arrival. Off = the car random-walks the graph (ring / highway scenes).")]
    public bool assignDestinations = false;

    [Tooltip("How origins/destinations are chosen (only used when Assign Destinations is on).")]
    public DemandPattern demandPattern = DemandPattern.Uniform;

    [Tooltip("Commuter phase: true = morning inbound (edges->centre), false = evening outbound (centre->edges). Toggled live from the UI.")]
    [HideInInspector] public bool inboundPhase = true;

    private float timer;

    // Reproducible demand stream (seeded from the manager). Isolated from UnityEngine.Random.
    private System.Random rng;

    // A drawn-but-not-yet-placed car, kept across blocked ticks so the draw order stays stable.
    private class PendingSpawn { public Waypoint origin, dest; public List<Waypoint> route; public DriverProfile profile; }
    private PendingSpawn pending;
    private float pendingWait; // how long the queued draw has been blocked at its origin

    // If a queued origin stays blocked this long (e.g. a broken-down car parked on a spawn port),
    // discard the draw and pick a fresh origin — otherwise that one blocked port halts ALL spawning.
    private const float PendingRedrawTimeout = 3f;

    // Cached demand geometry (centroid of spawnPoints + each port's 0..1 distance from it).
    private Vector3 demandCenter;
    private float[] portNorm;

    private void Start()
    {
        var mgr = TrafficSimulationManager.Instance;
        rng = new System.Random(mgr != null ? mgr.randomSeed : 0);
        RefreshDemandCache();
    }

    private void Update()
    {
        if (carPrefab == null || spawnPoints.Count == 0) return;
        if (!TrafficSimulationManager.SpawningEnabled) return; // master tap closed: timer frozen, no draws
        if (rng == null) rng = new System.Random(TrafficSimulationManager.Instance != null
                                                  ? TrafficSimulationManager.Instance.randomSeed : 0);
        EnsureCache();

        // Rush-hour surge scales the effective rate.
        float interval = spawnInterval / Mathf.Max(0.01f, TrafficSimulationManager.DemandMultiplier);
        timer += Time.deltaTime;
        if (timer < interval) return;
        timer = 0f;

        var mgr = TrafficSimulationManager.Instance;
        if (mgr != null && mgr.CarCount >= mgr.maxCars) return;

        // Draw the next car once; retry placement on later ticks if its origin is blocked.
        if (pending == null) { pending = DrawSpawn(); pendingWait = 0f; }
        if (pending == null) return;

        if (IsBlockedByCar(pending.origin.transform.position))
        {
            // Origin blocked. Normally a transient jam that clears in a tick or two, so we keep the
            // same draw (stable order). But a broken-down car can block a port forever — so after a
            // timeout, discard this draw and pick a fresh origin next tick instead of halting.
            pendingWait += interval;
            if (pendingWait > PendingRedrawTimeout) pending = null;
            return;
        }
        SpawnPending();
        pending = null;
    }

    /// <summary>Draw a car's origin/destination/route/profile from the seeded RNG (no placement yet).</summary>
    private PendingSpawn DrawSpawn()
    {
        Waypoint origin = PickOrigin();
        if (origin == null) return null;

        Waypoint dest = null;
        List<Waypoint> route = null;
        if (assignDestinations)
        {
            dest = PickDestination(origin);
            if (dest == null) return null;
            route = Pathfinder.FindPath(origin, dest, TrafficSimulationManager.ActiveEdgeCost);
            if (route == null || route.Count < 2) return null; // unreachable: skip this draw
        }

        DriverProfile profile = population != null ? population.PickWeighted(rng) : null;
        return new PendingSpawn { origin = origin, dest = dest, route = route, profile = profile };
    }

    /// <summary>Instantiate the queued car and wire up its route/profile.</summary>
    private void SpawnPending()
    {
        SpawnCar(pending.origin, pending.dest, pending.route, pending.profile, rng);
    }

    /// <summary>
    /// Spawn one car right now at <paramref name="origin"/> (cursor-driven manual spawn).
    /// Draws its destination/profile from <c>UnityEngine.Random</c>, NOT the seeded demand
    /// stream — like breakdowns, manual spawns are ad-hoc interventions and must not shift
    /// the reproducible draw order. Returns the car, or null if blocked / at the car cap.
    /// </summary>
    public CarAI SpawnManualAt(Waypoint origin)
    {
        if (carPrefab == null || origin == null) return null;
        var mgr = TrafficSimulationManager.Instance;
        if (mgr != null && mgr.CarCount >= mgr.maxCars) return null;
        if (IsBlockedByCar(origin.transform.position)) return null;

        Waypoint dest = null;
        List<Waypoint> route = null;
        if (assignDestinations && spawnPoints.Count > 1)
        {
            for (int i = 0; i < 8 && route == null; i++)
            {
                Waypoint w = spawnPoints[Random.Range(0, spawnPoints.Count)];
                if (w == null || w == origin) continue;
                var r = Pathfinder.FindPath(origin, w, TrafficSimulationManager.ActiveEdgeCost);
                if (r != null && r.Count >= 2) { dest = w; route = r; }
            }
        }
        DriverProfile profile = population != null ? population.PickWeighted() : null;
        return SpawnCar(origin, dest, route, profile, null);
    }

    /// <summary>Shared instantiation: place, orient toward the first leg, wire route/profile.</summary>
    private CarAI SpawnCar(Waypoint sp, Waypoint dest, List<Waypoint> route, DriverProfile profile, System.Random speedRng)
    {
        Vector3 pos = sp.transform.position;
        Waypoint next = route != null ? route[1] : sp.GetRandomNext();

        Quaternion rot = sp.transform.rotation;
        if (next != null)
        {
            Vector3 dir = next.transform.position - pos;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f) rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        CarAI car = Instantiate(carPrefab, pos, rot);
        car.currentTarget = next != null ? next : sp;
        car.originNode = sp;

        if (route != null)
        {
            car.route = route;
            car.destination = dest;
        }

        if (profile != null)
        {
            car.profile = profile;
            car.maxSpeed *= speedRng != null ? profile.RollSpeedFactor(speedRng) : profile.RollSpeedFactor();
        }
        return car;
    }

    // ---- Demand selection ----

    private Waypoint PickOrigin()
    {
        // Commuter biases where cars *appear*; other patterns spawn uniformly.
        if (assignDestinations && demandPattern == DemandPattern.Commuter)
            return PickWeightedPort(towardCenter: !inboundPhase, except: null);
        for (int i = 0; i < 8; i++)
        {
            Waypoint w = spawnPoints[rng.Next(spawnPoints.Count)];
            if (w != null) return w;
        }
        return FirstOtherPort(null);
    }

    private Waypoint PickDestination(Waypoint origin)
    {
        switch (demandPattern)
        {
            case DemandPattern.Hotspot:
                return PickWeightedPort(towardCenter: true, except: origin);
            case DemandPattern.Commuter:
                return PickWeightedPort(towardCenter: inboundPhase, except: origin);
            default: // Uniform
                return PickUniformPort(except: origin);
        }
    }

    private Waypoint PickUniformPort(Waypoint except)
    {
        for (int i = 0; i < 8; i++)
        {
            Waypoint w = spawnPoints[rng.Next(spawnPoints.Count)];
            if (w != null && w != except) return w;
        }
        return except == null ? null : FirstOtherPort(except);
    }

    /// <summary>
    /// Weighted pick over the ports by distance from the network centre: toward the centre
    /// (downtown sink) or toward the edges. Weight is sharpened (squared) with a small floor so no
    /// port is impossible.
    /// </summary>
    private Waypoint PickWeightedPort(bool towardCenter, Waypoint except)
    {
        if (portNorm == null || portNorm.Length != spawnPoints.Count) RefreshDemandCache();

        float total = 0f;
        for (int i = 0; i < spawnPoints.Count; i++)
        {
            Waypoint sp = spawnPoints[i];
            if (sp == null || sp == except) continue;
            total += PortWeight(i, towardCenter);
        }
        if (total <= 0f) return PickUniformPort(except);

        float roll = (float)rng.NextDouble() * total;
        for (int i = 0; i < spawnPoints.Count; i++)
        {
            Waypoint sp = spawnPoints[i];
            if (sp == null || sp == except) continue;
            roll -= PortWeight(i, towardCenter);
            if (roll <= 0f) return sp;
        }
        return FirstOtherPort(except);
    }

    private float PortWeight(int i, bool towardCenter)
    {
        float dn = portNorm[i];                 // 0 = centre, 1 = edge
        float w = towardCenter ? (1f - dn) : dn;
        return w * w + 0.05f;                   // sharpen + floor
    }

    private Waypoint FirstOtherPort(Waypoint except)
    {
        for (int i = 0; i < spawnPoints.Count; i++)
            if (spawnPoints[i] != null && spawnPoints[i] != except) return spawnPoints[i];
        return null;
    }

    private void EnsureCache()
    {
        if (portNorm == null || portNorm.Length != spawnPoints.Count) RefreshDemandCache();
    }

    /// <summary>
    /// Recompute the demand centre (centroid of the spawn ports) and each port's normalised
    /// distance from it. Call after <see cref="spawnPoints"/> is reassigned (e.g. city rebuild).
    /// </summary>
    public void RefreshDemandCache()
    {
        int n = spawnPoints.Count;
        portNorm = new float[n];
        if (n == 0) return;

        Vector3 sum = Vector3.zero;
        int valid = 0;
        for (int i = 0; i < n; i++)
        {
            if (spawnPoints[i] == null) continue;
            sum += spawnPoints[i].transform.position;
            valid++;
        }
        demandCenter = valid > 0 ? sum / valid : Vector3.zero;

        float maxD = 0f;
        var raw = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (spawnPoints[i] == null) { raw[i] = 0f; continue; }
            Vector3 d = spawnPoints[i].transform.position - demandCenter; d.y = 0f;
            raw[i] = d.magnitude;
            if (raw[i] > maxD) maxD = raw[i];
        }
        for (int i = 0; i < n; i++) portNorm[i] = maxD > 0.001f ? raw[i] / maxD : 0f;
    }

    private bool IsBlockedByCar(Vector3 position)
    {
        Collider[] hits = Physics.OverlapSphere(position, minClearRadius, ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            if (h == null) continue;
            if (h.GetComponentInParent<CarAI>() != null) return true;
        }
        return false;
    }

    /// <summary>Change spawn rate at runtime (used by UI).</summary>
    public void SetSpawnInterval(float seconds)
    {
        spawnInterval = Mathf.Max(0.05f, seconds);
    }

    /// <summary>
    /// Reset this spawner to the start of a scenario: re-seed the demand RNG, clear the spawn timer
    /// and any queued draw, reset the commuter phase, and rebuild the demand cache. Called by
    /// <see cref="TrafficSimulationManager.RestartScenario"/>.
    /// </summary>
    public void ResetScenarioState(int seed)
    {
        rng = new System.Random(seed);
        timer = 0f;
        pending = null;
        inboundPhase = true;
        RefreshDemandCache();
    }
}
