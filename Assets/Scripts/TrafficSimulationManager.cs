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

    // Cumulative-collision samples over the run, for the live graph.
    private readonly List<int> collisionHistory = new List<int>();
    private float sampleTimer;
    private const float SampleInterval = 1f;
    private const int MaxSamples = 180;
    public IReadOnlyList<int> CollisionHistory => collisionHistory;

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

    private void Update()
    {
        Time.timeScale = paused ? 0f : timeScale;

        // Sample cumulative collisions once a second for the graph (skip while paused).
        if (!paused)
        {
            sampleTimer += Time.unscaledDeltaTime;
            if (sampleTimer >= SampleInterval)
            {
                sampleTimer = 0f;
                collisionHistory.Add(totalCollisions);
                if (collisionHistory.Count > MaxSamples) collisionHistory.RemoveAt(0);
            }
        }
    }

    /// <summary>Reset the collision counter and its history graph.</summary>
    public void ResetCollisions()
    {
        totalCollisions = 0;
        collisionHistory.Clear();
        sampleTimer = 0f;
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
