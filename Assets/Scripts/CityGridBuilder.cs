using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Editor helper that generates a whole city as a grid of intersections joined by two-way streets,
/// producing one connected <see cref="Waypoint"/> graph the <see cref="Pathfinder"/> can route
/// across (GPS point A -> B).
///
/// Each intersection is a small hub of "ports": for every direction with a neighbour it has an
/// outbound port (traffic leaving toward that side) and an inbound port (traffic arriving from
/// that side). Ports are offset to the right of travel so opposing lanes never share a point — no
/// head-on meeting at the centre. Inbound ports fork to the other directions' outbound ports
/// (straight / left / right, never a U-turn). Optionally the two axes alternate on generated
/// <see cref="TrafficLightGroup"/> signals.
///
/// Point a <see cref="CarSpawner"/> at the returned spawn ports with "Assign Destinations" ticked
/// and cars drive shortest paths between random points. Choose "Rebuild city" from the context menu
/// (or use the SimulationUI sliders at runtime).
/// </summary>
[ExecuteAlways]
public class CityGridBuilder : MonoBehaviour
{
    [Header("Grid")]
    [Min(2)] public int rows = 4;
    [Min(2)] public int cols = 4;
    [Tooltip("Distance between adjacent intersections (metres).")]
    public float spacing = 25f;

    [Header("Streets")]
    [Tooltip("Lateral offset of each lane from the street centreline (metres).")]
    public float laneWidth = 2.5f;
    [Tooltip("How far each port sits from the intersection centre (metres).")]
    public float portDistance = 3f;
    [Tooltip("Intermediate nodes along each street lane (higher = smoother, finer sensing).")]
    [Min(1)] public int nodesPerStreet = 4;
    public float height = 0.25f;

    [Header("Signals")]
    public bool useTrafficLights = true;
    public float greenTime = 6f;
    public float yellowTime = 2f;
    public bool createLightVisuals = true;

    // Direction indices: 0=E, 1=W, 2=N, 3=S.
    private static readonly Vector3[] DirVec = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
    private static readonly int[] Opposite = { 1, 0, 3, 2 };

    private class Port
    {
        public Waypoint[] inbound = new Waypoint[4];  // arrival from side d (heading toward centre)
        public Waypoint[] outbound = new Waypoint[4]; // departure toward side d
    }
    private Port[,] ports;
    private readonly List<Waypoint> spawnPorts = new List<Waypoint>();

    [ContextMenu("Rebuild city")]
    public void Rebuild()
    {
        ClearChildren();
        ports = new Port[rows, cols];
        spawnPorts.Clear();

        Transform hubRoot = new GameObject("Intersections").transform;
        hubRoot.SetParent(transform, false);
        Transform streetRoot = new GameObject("Streets").transform;
        streetRoot.SetParent(transform, false);

        // 1) Create the port nodes at every intersection.
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                ports[r, c] = BuildIntersection(r, c, hubRoot);

        // 2) Wire in-intersection turns (inbound -> every other existing outbound).
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Port p = ports[r, c];
                for (int din = 0; din < 4; din++)
                {
                    if (p.inbound[din] == null) continue;
                    for (int dout = 0; dout < 4; dout++)
                        if (dout != din && p.outbound[dout] != null)
                            p.inbound[din].nextWaypoints.Add(p.outbound[dout]);
                }
            }

        // 3) Connect neighbouring intersections with two opposing lanes each.
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                if (c + 1 < cols) // East edge
                {
                    Link(ports[r, c].outbound[0], ports[r, c + 1].inbound[1], streetRoot, $"E_{r}_{c}");
                    Link(ports[r, c + 1].outbound[1], ports[r, c].inbound[0], streetRoot, $"W_{r}_{c}");
                }
                if (r + 1 < rows) // North edge
                {
                    Link(ports[r, c].outbound[2], ports[r + 1, c].inbound[3], streetRoot, $"N_{r}_{c}");
                    Link(ports[r + 1, c].outbound[3], ports[r, c].inbound[2], streetRoot, $"S_{r}_{c}");
                }
            }

        if (useTrafficLights) BuildSignals(hubRoot);

        Debug.Log($"[CityGridBuilder] Built {rows}x{cols} city " +
                  $"({GetComponentsInChildren<Waypoint>(true).Length} waypoints, " +
                  $"{(useTrafficLights ? "signalised" : "uncontrolled")}).");
    }

    private Port BuildIntersection(int r, int c, Transform root)
    {
        var p = new Port();
        Vector3 center = new Vector3(c * spacing, height, r * spacing);
        float lat = laneWidth * 0.5f;

        Transform hub = new GameObject($"X_{r}_{c}").transform;
        hub.SetParent(root, false);
        hub.localPosition = center;

        // Reservation zone covering the crossing area (used by Perfect / no-lights mode).
        hub.gameObject.AddComponent<IntersectionZoneMarker>().radius = portDistance + laneWidth;

        bool[] has = { c + 1 < cols, c - 1 >= 0, r + 1 < rows, r - 1 >= 0 }; // E,W,N,S

        for (int d = 0; d < 4; d++)
        {
            if (!has[d]) continue;
            Vector3 dir = DirVec[d];
            Vector3 outRight = Vector3.Cross(Vector3.up, dir).normalized;
            Vector3 inRight = Vector3.Cross(Vector3.up, -dir).normalized;

            p.outbound[d] = NewWaypoint($"out_{DirName(d)}", center + dir * portDistance + outRight * lat, hub, dir);
            p.inbound[d] = NewWaypoint($"in_{DirName(d)}", center + dir * portDistance + inRight * lat, hub, -dir);
            p.inbound[d].stopLineDistance = 1.5f;

            spawnPorts.Add(p.outbound[d]);
        }
        return p;
    }

    /// <summary>Build one directed lane of intermediate nodes between two existing ports.</summary>
    private void Link(Waypoint from, Waypoint to, Transform parent, string name)
    {
        if (from == null || to == null) return;
        Vector3 a = from.transform.position;
        Vector3 b = to.transform.position;

        Transform laneParent = new GameObject($"Lane_{name}").transform;
        laneParent.SetParent(parent, false);

        Waypoint prev = from;
        for (int i = 1; i <= nodesPerStreet; i++)
        {
            float t = i / (float)(nodesPerStreet + 1);
            Waypoint node = NewWaypoint($"{name}_{i}", Vector3.Lerp(a, b, t), laneParent, b - a);
            prev.nextWaypoints.Add(node);
            prev = node;
        }
        prev.nextWaypoints.Add(to);
    }

    private void BuildSignals(Transform root)
    {
        Transform lightRoot = new GameObject("Signals").transform;
        lightRoot.SetParent(transform, false);

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Port p = ports[r, c];
                var groupGo = new GameObject($"Signal_{r}_{c}");
                groupGo.transform.SetParent(lightRoot, false);
                groupGo.transform.localPosition = new Vector3(c * spacing, height, r * spacing);
                var group = groupGo.AddComponent<TrafficLightGroup>();
                group.greenTime = greenTime;
                group.yellowTime = yellowTime;

                // E/W inbound ports (indices 0,1) = phase A; N/S inbound (2,3) = phase B.
                for (int d = 0; d < 4; d++)
                {
                    if (p.inbound[d] == null) continue;
                    var light = MakeLight(p.inbound[d], groupGo.transform);
                    if (d < 2) group.phaseA.Add(light);
                    else       group.phaseB.Add(light);
                }
            }
    }

    private TrafficLight MakeLight(Waypoint approach, Transform parent)
    {
        var go = new GameObject($"Light_{approach.name}");
        go.transform.SetParent(parent, false);
        go.transform.position = approach.transform.position + Vector3.up * 1.2f;
        var light = go.AddComponent<TrafficLight>();

        if (createLightVisuals)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Bulb";
            var col = cube.GetComponent<Collider>();     // keep colliders out of the lane
            if (col != null) DestroyImmediateSafe(col);
            cube.transform.SetParent(go.transform, false);
            cube.transform.localPosition = Vector3.zero;
            cube.transform.localScale = Vector3.one * 0.7f;
            light.lightRenderer = cube.GetComponent<Renderer>();
        }

        approach.controllingLight = light;
        return light;
    }

    /// <summary>Departure ports — hand these to a spawner as spawn points.</summary>
    public List<Waypoint> GetIntersections() => new List<Waypoint>(spawnPorts);

    private static string DirName(int d) => d == 0 ? "E" : d == 1 ? "W" : d == 2 ? "N" : "S";

    private Waypoint NewWaypoint(string name, Vector3 worldPos, Transform parent, Vector3 flow)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;
        flow.y = 0f;
        if (flow.sqrMagnitude > 0.001f)
            go.transform.rotation = Quaternion.LookRotation(flow.normalized, Vector3.up);
        return go.AddComponent<Waypoint>();
    }

    private void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediateSafe(transform.GetChild(i).gameObject);
    }

    private static void DestroyImmediateSafe(Object o)
    {
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.3f, 0.35f);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector3 p = transform.TransformPoint(new Vector3(c * spacing, height, r * spacing));
                if (c < cols - 1)
                    Gizmos.DrawLine(p, transform.TransformPoint(new Vector3((c + 1) * spacing, height, r * spacing)));
                if (r < rows - 1)
                    Gizmos.DrawLine(p, transform.TransformPoint(new Vector3(c * spacing, height, (r + 1) * spacing)));
            }
    }
}
