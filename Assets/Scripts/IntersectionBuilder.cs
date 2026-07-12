using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Editor helper that generates a signalised (or give-way) junction as a small waypoint sub-graph:
/// a 4-way cross or a 3-way T. Each arm gets one inbound lane (toward the centre) and one outbound
/// lane (away from it); inbound stop lines fork to every other arm's outbound lane, so a car can go
/// straight, left or right (never U-turn). Arms expose <see cref="RoadConnector"/>s so external
/// roads snap on via <see cref="RoadNetworkStitcher"/>.
///
/// Right-of-way:
///  - 4-way + <see cref="useTrafficLights"/>: opposing arms share a phase, driven by a generated
///    <see cref="TrafficLightGroup"/>.
///  - otherwise: inbound stop lines are give-way (<see cref="Waypoint.isYield"/>) points.
///
/// Choose "Rebuild junction" from the component context menu after tweaking values.
/// </summary>
[ExecuteAlways]
public class IntersectionBuilder : MonoBehaviour
{
    public enum JunctionType { FourWay, TJunction }
    public enum Side { North, East, South, West }

    [Header("Type")]
    public JunctionType type = JunctionType.FourWay;
    [Tooltip("For a T-junction, which arm is ABSENT (the closed side).")]
    public Side missingArm = Side.South;

    [Header("Geometry")]
    [Tooltip("Distance from centre to each arm's connector endpoint (metres).")]
    public float armLength = 8f;
    [Tooltip("Distance from centre to the inbound stop line / outbound start (metres).")]
    public float innerRadius = 3f;
    [Tooltip("Half-separation of the inbound and outbound lanes (metres).")]
    public float laneWidth = 2.5f;
    [Tooltip("Y height of each waypoint.")]
    public float height = 0.25f;

    [Header("Control")]
    [Tooltip("4-way only: generate coordinated traffic lights. If off (or on a T), inbound approaches give way.")]
    public bool useTrafficLights = true;
    [Tooltip("Spawn small coloured cubes showing each light's state.")]
    public bool createLightVisuals = true;

    // Per-arm generated nodes.
    private class Arm
    {
        public Side side;
        public Vector3 dir;          // local outward unit direction
        public Waypoint approach;    // inbound far endpoint (Entry connector)
        public Waypoint stop;        // inbound stop line at centre (light / yield, turn fork)
        public Waypoint exitInner;   // outbound start at centre
        public Waypoint depart;      // outbound far endpoint (Exit connector)
    }

    [ContextMenu("Rebuild junction")]
    public void Rebuild()
    {
        ClearChildren();

        List<Arm> arms = BuildArms();

        // Create the four nodes of every arm.
        foreach (var a in arms)
        {
            Transform armParent = new GameObject($"Arm_{a.side}").transform;
            armParent.SetParent(transform, false);
            armParent.localPosition = Vector3.zero;
            armParent.localRotation = Quaternion.identity;

            Vector3 inRight = Vector3.Cross(Vector3.up, -a.dir).normalized;  // right of inbound travel
            Vector3 outRight = Vector3.Cross(Vector3.up, a.dir).normalized;  // right of outbound travel
            float lat = laneWidth * 0.5f;

            a.approach  = NewWaypoint($"WP_{a.side}_approach", a.dir * armLength   + inRight * lat, armParent, -a.dir);
            a.stop      = NewWaypoint($"WP_{a.side}_stop",     a.dir * innerRadius + inRight * lat, armParent, -a.dir);
            a.exitInner = NewWaypoint($"WP_{a.side}_exit",     a.dir * innerRadius + outRight * lat, armParent, a.dir);
            a.depart    = NewWaypoint($"WP_{a.side}_depart",   a.dir * armLength   + outRight * lat, armParent, a.dir);

            a.stop.stopLineDistance = 1.5f;

            // Connectors: external roads feed 'approach' (Entry) and receive from 'depart' (Exit).
            AddConnector(a.approach, RoadConnector.Kind.Entry);
            AddConnector(a.depart, RoadConnector.Kind.Exit);
        }

        // Wire the graph.
        foreach (var a in arms)
        {
            a.approach.nextWaypoints.Add(a.stop);
            a.exitInner.nextWaypoints.Add(a.depart);

            // From the stop line you may take any OTHER arm's outbound lane (straight/left/right).
            foreach (var b in arms)
                if (b != a) a.stop.nextWaypoints.Add(b.exitInner);
        }

        // Reservation zone over the crossing (used by Perfect / no-lights mode).
        var zoneGo = new GameObject("Zone");
        zoneGo.transform.SetParent(transform, false);
        zoneGo.transform.localPosition = Vector3.zero;
        zoneGo.AddComponent<IntersectionZoneMarker>().radius = innerRadius + laneWidth;

        // Right-of-way.
        bool lightsOn = useTrafficLights && type == JunctionType.FourWay;
        if (lightsOn) BuildLights(arms);
        else foreach (var a in arms) a.stop.isYield = true; // all-way give-way / T give-way

        // Join this junction's own arms into the scene graph in case a stitcher isn't present.
        RoadConnector.StitchAll(1.5f);

        Debug.Log($"[IntersectionBuilder] Built {type} with {arms.Count} arms " +
                  $"({(lightsOn ? "traffic lights" : "give-way")}).");
    }

    private List<Arm> BuildArms()
    {
        var all = new (Side side, Vector3 dir)[]
        {
            (Side.North, Vector3.forward),
            (Side.East,  Vector3.right),
            (Side.South, Vector3.back),
            (Side.West,  Vector3.left),
        };

        var arms = new List<Arm>();
        foreach (var e in all)
        {
            if (type == JunctionType.TJunction && e.side == missingArm) continue;
            arms.Add(new Arm { side = e.side, dir = e.dir });
        }
        return arms;
    }

    private void BuildLights(List<Arm> arms)
    {
        Transform lightRoot = new GameObject("Lights").transform;
        lightRoot.SetParent(transform, false);
        lightRoot.localPosition = Vector3.zero;

        var group = lightRoot.gameObject.AddComponent<TrafficLightGroup>();

        foreach (var a in arms)
        {
            var lightGo = new GameObject($"Light_{a.side}");
            lightGo.transform.SetParent(lightRoot, false);
            lightGo.transform.localPosition = a.stop.transform.localPosition + Vector3.up * 1.2f;
            var light = lightGo.AddComponent<TrafficLight>();

            if (createLightVisuals)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Bulb";
                var col = cube.GetComponent<Collider>();      // NEVER leave a collider in the lane
                if (col != null) DestroyImmediateSafe(col);
                cube.transform.SetParent(lightGo.transform, false);
                cube.transform.localPosition = Vector3.zero;
                cube.transform.localScale = Vector3.one * 0.6f;
                light.lightRenderer = cube.GetComponent<Renderer>();
            }

            a.stop.controllingLight = light;

            // Arms aligned with the Z axis share phase A; X-axis arms share phase B.
            bool phaseA = Mathf.Abs(a.dir.z) > Mathf.Abs(a.dir.x);
            if (phaseA) group.phaseA.Add(light);
            else        group.phaseB.Add(light);
        }
    }

    // ---- helpers ----

    private Waypoint NewWaypoint(string name, Vector3 localPos, Transform parent, Vector3 localFlow)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(localPos.x, height, localPos.z);
        if (localFlow.sqrMagnitude > 0.001f)
            go.transform.localRotation = Quaternion.LookRotation(localFlow.normalized, Vector3.up);
        return go.AddComponent<Waypoint>();
    }

    private void AddConnector(Waypoint node, RoadConnector.Kind kind)
    {
        var c = node.gameObject.AddComponent<RoadConnector>();
        c.kind = kind;
        c.node = node;
    }

    /// <summary>All inbound approach endpoints, e.g. to feed a spawner.</summary>
    public List<Waypoint> GetApproaches()
    {
        var list = new List<Waypoint>();
        foreach (var c in GetComponentsInChildren<RoadConnector>())
            if (c.kind == RoadConnector.Kind.Entry && c.node != null) list.Add(c.node);
        return list;
    }

    private void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediateSafe(transform.GetChild(i).gameObject);
    }

    private static void DestroyImmediateSafe(Object o)
    {
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }
}
