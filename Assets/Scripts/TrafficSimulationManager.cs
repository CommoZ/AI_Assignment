using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Singleton that keeps a registry of all live cars AND every waypoint node in the scene.
/// The car registry drives simple jam metrics (average speed, count, % stopped, average
/// frustration); the node registry is the graph the <see cref="Pathfinder"/> searches and the
/// pool the spawner draws random destinations from. Also exposes global controls: pause,
/// time-scale multiplier, and max car cap.
/// </summary>
public class TrafficSimulationManager : MonoBehaviour
{
    public static TrafficSimulationManager Instance { get; private set; }

    [Header("Global limits")]
    public int maxCars = 60;

    [Header("Time controls")]
    [Range(0f, 4f)] public float timeScale = 1f;
    public bool paused;

    [Header("Scenario")]
    [Tooltip("Seed for reproducible demand (origins, destinations, driver rolls). Applied on Start and on 'Restart scenario'.")]
    public int randomSeed = 12345;

    // ---- Global environment conditions (read by CarAI / CarSpawner; defaults are no-ops) ----
    /// <summary>Road grip 0.5..1 (weather). Scales effective braking + traction. 1 = dry road.</summary>
    public static float RoadGrip = 1f;
    /// <summary>Global speed cap in m/s, or 0 for no limit.</summary>
    public static float GlobalSpeedLimit = 0f;
    /// <summary>Spawn-rate multiplier (rush-hour surge). 1 = normal.</summary>
    public static float DemandMultiplier = 1f;
    /// <summary>Master tap for scheduled spawning. False = no new cars (manual N-key spawns still work).</summary>
    public static bool SpawningEnabled = true;

    // ---- Congestion-aware routing ----
    /// <summary>When true, cars route with <see cref="CongestionCost"/> so they avoid busy roads.</summary>
    public static bool CongestionAwareRouting = false;
    /// <summary>How strongly congestion inflates edge cost (0 = ignore, higher = detour harder).</summary>
    public static float CongestionWeight = 2f;
    // Smoothed count of cars currently heading to each node (the "weighted road nodes").
    private static readonly Dictionary<Waypoint, float> congestion = new Dictionary<Waypoint, float>();

    /// <summary>Smoothed congestion at a node (roughly the number of cars targeting it). 0 if unknown.</summary>
    public static float NodeCongestion(Waypoint w)
    {
        if (w == null) return 0f;
        return congestion.TryGetValue(w, out float c) ? c : 0f;
    }

    /// <summary>The edge cost cars should plan with right now: congestion-aware or plain distance.</summary>
    public static IEdgeCost ActiveEdgeCost => CongestionAwareRouting ? CongestionCost.Default : null;

    private static readonly List<CarAI> cars = new List<CarAI>();

    // Every Waypoint registers here (see Waypoint.OnEnable). This is the routing graph's node set.
    private static readonly List<Waypoint> nodes = new List<Waypoint>();

    /// <summary>Read-only view of all waypoints in the scene (for the pathfinder).</summary>
    public static IReadOnlyList<Waypoint> Nodes => nodes;

    public int CarCount => cars.Count;

    // ---- Collision tracking ----
    private static int totalCollisions;
    public static int TotalCollisions => totalCollisions;
    /// <summary>Called by a car when it collides with another (deduplicated to one per pair).</summary>
    public static void ReportCollision() { totalCollisions++; }

    // ---- Trip / throughput metrics ----
    private static int completedTrips;
    private static double tripTimeSum;
    // Sim-time stamps of recent arrivals, pruned to a 60 s window -> throughput = count = cars/min.
    private static readonly Queue<float> arrivalTimes = new Queue<float>();
    private const float ThroughputWindow = 60f;

    public int CompletedTrips => completedTrips;
    public float AverageTripTime => completedTrips > 0 ? (float)(tripTimeSum / completedTrips) : 0f;

    /// <summary>Cars that reached their destination in the last 60 s of sim time (cars/min).</summary>
    public float ThroughputPerMin { get { PruneArrivals(); return arrivalTimes.Count; } }

    /// <summary>Called by a car when it reaches its destination. tripTime is sim seconds start->finish.</summary>
    public static void ReportArrival(float tripTime)
    {
        completedTrips++;
        tripTimeSum += tripTime;
        arrivalTimes.Enqueue(Time.time);
    }

    private static void PruneArrivals()
    {
        float cutoff = Time.time - ThroughputWindow;
        while (arrivalTimes.Count > 0 && arrivalTimes.Peek() < cutoff) arrivalTimes.Dequeue();
    }

    // ---- Time-series samples (once a second) for the live graphs + CSV export ----
    public struct Sample
    {
        public float t;            // sim time
        public int cars;
        public float avgSpeed;
        public float stoppedFrac;  // 0..1
        public float frustration;  // 0..1
        public float throughput;   // cars/min
        public int collisions;     // cumulative
        public int trips;          // cumulative completed
    }
    private readonly List<Sample> samples = new List<Sample>();
    private float sampleTimer;
    private const float SampleInterval = 1f;
    private const int MaxSamples = 180;
    public IReadOnlyList<Sample> Samples => samples;

    // ---- Intersection reservations (used by Perfect mode instead of traffic lights) ----
    /// <summary>One car's trajectory (entry->exit) through a junction, plus its distance to entry.</summary>
    public class ZoneClaim { public CarAI car; public Vector3 entry; public Vector3 exit; public float dist; }

    /// <summary>A junction region. Cars whose trajectories cross must take turns; others pass together.</summary>
    public class IntersectionZone
    {
        public Vector3 center;
        public float radius;     // detection radius (finds entry/exit port nodes)
        public float coreRadius; // committed radius (the central crossing area)
        public readonly List<ZoneClaim> claims = new List<ZoneClaim>();
    }

    private static readonly List<IntersectionZone> zones = new List<IntersectionZone>();
    /// <summary>When true, cars negotiate intersections by reservation (perfect-info mode).</summary>
    public static bool ReservationsEnabled;

    public float AverageSpeed
    {
        get
        {
            if (cars.Count == 0) return 0f;
            float sum = 0f;
            foreach (var c in cars) if (c != null) sum += c.CurrentSpeed;
            return sum / cars.Count;
        }
    }

    /// <summary>Fraction of cars currently stopped (0..1). A rough jam indicator.</summary>
    public float StoppedFraction
    {
        get
        {
            if (cars.Count == 0) return 0f;
            int stopped = 0;
            foreach (var c in cars) if (c != null && c.IsStopped) stopped++;
            return (float)stopped / cars.Count;
        }
    }

    /// <summary>Average driver frustration/mood across all cars (0..1). Rises as jams build.</summary>
    public float AverageFrustration
    {
        get
        {
            if (cars.Count == 0) return 0f;
            float sum = 0f;
            foreach (var c in cars) if (c != null) sum += c.Frustration;
            return sum / cars.Count;
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        // Seed the shared RNG so the run's demand + driver rolls are reproducible for a given seed.
        Random.InitState(randomSeed);
    }

    private void Update()
    {
        Time.timeScale = paused ? 0f : timeScale;

        // Sample the live metrics once a second for the graphs / CSV (skip while paused).
        if (!paused)
        {
            sampleTimer += Time.unscaledDeltaTime;
            if (sampleTimer >= SampleInterval)
            {
                sampleTimer = 0f;
                UpdateCongestionMap();
                samples.Add(new Sample
                {
                    t           = Time.time,
                    cars        = cars.Count,
                    avgSpeed    = AverageSpeed,
                    stoppedFrac = StoppedFraction,
                    frustration = AverageFrustration,
                    throughput  = ThroughputPerMin,
                    collisions  = totalCollisions,
                    trips       = completedTrips,
                });
                if (samples.Count > MaxSamples) samples.RemoveAt(0);
            }
        }
    }

    // Scratch buffers reused each tick so the congestion update allocates nothing.
    private static readonly Dictionary<Waypoint, int> congestionCounts = new Dictionary<Waypoint, int>();
    private static readonly List<Waypoint> congestionScratch = new List<Waypoint>();

    /// <summary>
    /// Rebuild the smoothed congestion map: count cars heading to each node, then EMA toward that
    /// count (so weights ramp/decay smoothly rather than flicker) and drop nodes that have emptied.
    /// </summary>
    private static void UpdateCongestionMap()
    {
        congestionCounts.Clear();
        foreach (var c in cars)
        {
            if (c == null || c.currentTarget == null) continue;
            congestionCounts.TryGetValue(c.currentTarget, out int n);
            congestionCounts[c.currentTarget] = n + 1;
        }

        // Snapshot the set of nodes to touch (current smoothed entries + newly counted ones) so we
        // can safely add/remove dictionary entries while updating.
        congestionScratch.Clear();
        foreach (var k in congestion.Keys) congestionScratch.Add(k);
        foreach (var k in congestionCounts.Keys) if (!congestion.ContainsKey(k)) congestionScratch.Add(k);

        for (int i = 0; i < congestionScratch.Count; i++)
        {
            Waypoint k = congestionScratch[i];
            congestionCounts.TryGetValue(k, out int target);
            congestion.TryGetValue(k, out float prev);
            float v = Mathf.Lerp(prev, target, 0.4f);
            if (target == 0 && v < 0.05f) congestion.Remove(k);
            else congestion[k] = v;
        }
    }

    /// <summary>Reset the collision counter and clear the metric graphs.</summary>
    public void ResetCollisions()
    {
        totalCollisions = 0;
        samples.Clear();
        sampleTimer = 0f;
    }

    /// <summary>Wipe every run metric (collisions, trips, throughput, graphs). Used by Restart scenario.</summary>
    public void ResetMetrics()
    {
        totalCollisions = 0;
        completedTrips = 0;
        tripTimeSum = 0;
        arrivalTimes.Clear();
        samples.Clear();
        congestion.Clear();
        sampleTimer = 0f;
    }

    /// <summary>
    /// Restart the scenario deterministically: re-seed the RNG, clear cars, reset every metric, and
    /// reset each spawner's demand state so the exact same demand sequence replays for a given seed.
    /// </summary>
    public void RestartScenario()
    {
        ClearAllCars();
        ResetMetrics();
        Random.InitState(randomSeed);
        foreach (var s in FindObjectsByType<CarSpawner>(FindObjectsSortMode.None))
            if (s != null) s.ResetScenarioState(randomSeed);
    }

    /// <summary>Write the sampled metric time-series to a CSV in persistentDataPath; returns the path.</summary>
    public string ExportCsv()
    {
        string path = System.IO.Path.Combine(Application.persistentDataPath,
            $"traffic_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("time,cars,avgSpeed,stoppedPct,frustrationPct,throughputPerMin,collisions,completedTrips");
        foreach (var s in samples)
            sb.AppendLine($"{s.t:0.0},{s.cars},{s.avgSpeed:0.00},{s.stoppedFrac * 100f:0.0}," +
                          $"{s.frustration * 100f:0.0},{s.throughput:0.0},{s.collisions},{s.trips}");
        System.IO.File.WriteAllText(path, sb.ToString());
        Debug.Log($"Traffic metrics exported to: {path}");
        return path;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Time.timeScale = 1f;
    }

    public static void Register(CarAI c)
    {
        if (c != null && !cars.Contains(c)) cars.Add(c);
    }

    public static void Unregister(CarAI c)
    {
        cars.Remove(c);
    }

    /// <summary>Despawn every live car (e.g. before rebuilding the road network at runtime).</summary>
    public static void ClearAllCars()
    {
        // Copy because Despawn -> OnDisable -> Unregister mutates the list.
        var snapshot = cars.ToArray();
        foreach (var c in snapshot) if (c != null) c.Despawn();
        cars.Clear();
    }

    /// <summary>Force every live car onto one driver profile (used by Perfect mode).</summary>
    public static void ApplyProfileToAll(DriverProfile p)
    {
        foreach (var c in cars) if (c != null) c.SetProfile(p);
    }

    // ---- Incidents (breakdowns) ----
    // Dedicated RNG so the "break down random car" button perturbs neither UnityEngine.Random nor
    // the spawner's demand stream.
    private static System.Random incidentRng;

    /// <summary>How many live cars are currently broken down.</summary>
    public static int StalledCount
    {
        get { int n = 0; foreach (var c in cars) if (c != null && c.Stalled) n++; return n; }
    }

    /// <summary>Break down a random currently-moving car. Returns false if there was none.</summary>
    public static bool StallRandomCar()
    {
        incidentRng ??= new System.Random();
        var candidates = new List<CarAI>();
        foreach (var c in cars) if (c != null && !c.Stalled) candidates.Add(c);
        if (candidates.Count == 0) return false;
        candidates[incidentRng.Next(candidates.Count)].StallFor(0f); // 0 = until cleared
        return true;
    }

    /// <summary>Clear every breakdown so all cars resume driving.</summary>
    public static void ClearAllStalls()
    {
        foreach (var c in cars) if (c != null && c.Stalled) c.ClearStall();
    }

    /// <summary>Re-roll every live car's profile from a population (used when leaving Perfect mode).</summary>
    public static void ReassignFromPopulation(DriverPopulation pop)
    {
        if (pop == null) return;
        foreach (var c in cars) if (c != null) c.SetProfile(pop.PickWeighted());
    }

    // ---- Waypoint node registry (routing graph) ----

    public static void RegisterNode(Waypoint w)
    {
        if (w != null && !nodes.Contains(w)) nodes.Add(w);
    }

    public static void UnregisterNode(Waypoint w)
    {
        nodes.Remove(w);
    }

    // ---- Intersection zone registry ----

    public static IntersectionZone RegisterZone(Vector3 center, float radius)
    {
        var z = new IntersectionZone { center = center, radius = radius, coreRadius = radius * 0.45f };
        zones.Add(z);
        return z;
    }

    public static void UnregisterZone(IntersectionZone z)
    {
        if (z != null) zones.Remove(z);
    }

    /// <summary>Clear every reservation claim (call when toggling reservation mode).</summary>
    public static void ClearZoneReservations()
    {
        for (int i = 0; i < zones.Count; i++) zones[i].claims.Clear();
    }

    /// <summary>The zone whose (detection) disc contains <paramref name="p"/>, or null.</summary>
    public static IntersectionZone ZoneContainingPoint(Vector3 p)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            IntersectionZone z = zones[i];
            Vector3 to = z.center - p; to.y = 0f;
            if (to.sqrMagnitude <= z.radius * z.radius) return z;
        }
        return null;
    }

    /// <summary>The zone whose CORE (central crossing area) contains <paramref name="p"/>, or null.</summary>
    public static IntersectionZone ZoneCoreContaining(Vector3 p)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            IntersectionZone z = zones[i];
            Vector3 to = z.center - p; to.y = 0f;
            if (to.sqrMagnitude <= z.coreRadius * z.coreRadius) return z;
        }
        return null;
    }

    /// <summary>Record / refresh a car's trajectory claim through a zone.</summary>
    public static void SetClaim(IntersectionZone z, CarAI car, Vector3 entry, Vector3 exit, float dist)
    {
        for (int i = 0; i < z.claims.Count; i++)
            if (z.claims[i].car == car)
            {
                z.claims[i].entry = entry; z.claims[i].exit = exit; z.claims[i].dist = dist;
                return;
            }
        z.claims.Add(new ZoneClaim { car = car, entry = entry, exit = exit, dist = dist });
    }

    /// <summary>Drop a car's claim from a zone (or from all zones if <paramref name="z"/> is null).</summary>
    public static void RemoveClaim(IntersectionZone z, CarAI car)
    {
        if (z != null) { z.claims.RemoveAll(c => c.car == car); return; }
        for (int i = 0; i < zones.Count; i++) zones[i].claims.RemoveAll(c => c.car == car);
    }

    /// <summary>
    /// True if the car must yield: some other claimant's trajectory CROSSES ours and that car has
    /// priority (closer to entry, ties broken by id). Non-crossing trajectories never yield, so
    /// cars whose paths don't intersect pass the junction simultaneously.
    /// </summary>
    public static bool MustYield(IntersectionZone z, CarAI me, Vector3 myEntry, Vector3 myExit, float myDist, int myId)
    {
        for (int i = 0; i < z.claims.Count; i++)
        {
            ZoneClaim c = z.claims[i];
            if (c.car == null || c.car == me) continue;
            if (!SegmentsCross(myEntry, myExit, c.entry, c.exit)) continue;
            bool otherHigher = c.dist < myDist - 0.01f ||
                               (Mathf.Abs(c.dist - myDist) <= 0.01f && c.car.GetInstanceID() < myId);
            if (otherHigher) return true;
        }
        return false;
    }

    /// <summary>
    /// Do two junction trajectories conflict? They do if they MERGE (share an exit) or if their
    /// paths pass within a car-width of each other. Sharing an entry (diverging) is safe. Using a
    /// distance threshold — not just a mathematical crossing — catches turn paths that graze past
    /// each other, which a thin-line test would miss and let collide.
    /// </summary>
    private static bool SegmentsCross(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4)
    {
        Vector2 a = new Vector2(p1.x, p1.z), b = new Vector2(p2.x, p2.z);
        Vector2 c = new Vector2(p3.x, p3.z), d = new Vector2(p4.x, p4.z);
        const float eps = 0.5f;
        bool sameEntry = (a - c).sqrMagnitude < eps * eps;
        bool sameExit = (b - d).sqrMagnitude < eps * eps;
        if (sameEntry && !sameExit) return false; // diverging from a shared inbound is safe
        if (sameExit) return true;                 // merging into a shared outbound: take turns

        const float conflict = 1.6f;               // ~a car width of clearance
        return SegSegDistanceSq(a, b, c, d) < conflict * conflict;
    }

    private static float Cross2(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    private static bool SegSegIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float d1 = Cross2(d - c, a - c), d2 = Cross2(d - c, b - c);
        float d3 = Cross2(b - a, c - a), d4 = Cross2(b - a, d - a);
        return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
               ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
    }

    private static float PtSegDistSq(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return (p - (a + ab * t)).sqrMagnitude;
    }

    private static float SegSegDistanceSq(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        if (SegSegIntersect(a, b, c, d)) return 0f;
        return Mathf.Min(Mathf.Min(PtSegDistSq(a, c, d), PtSegDistSq(b, c, d)),
                         Mathf.Min(PtSegDistSq(c, a, b), PtSegDistSq(d, a, b)));
    }

    /// <summary>
    /// A random waypoint to use as a destination, other than <paramref name="except"/>.
    /// Prefers nodes that are reachable sinks (have somewhere to go or are dead-ends).
    /// Returns null if none exist.
    /// </summary>
    public static Waypoint RandomNodeExcept(Waypoint except)
    {
        if (nodes.Count == 0) return null;
        // A few random tries to avoid returning the start node.
        for (int i = 0; i < 8; i++)
        {
            Waypoint w = nodes[Random.Range(0, nodes.Count)];
            if (w != null && w != except) return w;
        }
        return null;
    }
}
