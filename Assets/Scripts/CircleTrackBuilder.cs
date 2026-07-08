using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Editor helper. Attach to an empty GameObject and choose "Rebuild waypoints"
/// from the component's context menu (three-dot icon in the Inspector, or
/// right-click the component header). It removes any child GameObjects and
/// generates one or more concentric ring lanes of <see cref="Waypoint"/> children:
///   - <see cref="laneCount"/> concentric lanes, spaced <see cref="laneWidth"/> apart.
///   - <see cref="waypointCount"/> waypoints evenly around each ring.
///   - nextWaypoints wired around each ring into a closed loop.
///   - laneNeighbors wired between adjacent rings so CarAI can change lanes / overtake.
///
/// This does NOT change how <see cref="CarAI"/> drives — cars still go waypoint to
/// waypoint. A fine ring (e.g. 48 or 64 nodes) makes the path look like an exact circle.
/// </summary>
[ExecuteAlways]
public class CircleTrackBuilder : MonoBehaviour
{
    [Header("Rings")]
    [Tooltip("Radius of the innermost lane, in metres.")]
    public float radius = 10f;

    [Tooltip("Number of concentric lanes. 1 = single ring. 2–3 = a multi-lane ring road.")]
    [Min(1)] public int laneCount = 1;

    [Tooltip("Radial distance between adjacent lane centres (metres). Lane 0 is innermost.")]
    public float laneWidth = 2.5f;

    [Header("Nodes")]
    [Tooltip("Number of waypoints around each ring. Higher = smoother circle. 32–64 looks perfect.")]
    [Min(3)] public int waypointCount = 48;

    [Tooltip("Y position of each waypoint (car height).")]
    public float height = 0.25f;

    [Header("Direction")]
    [Tooltip("If true, cars go anticlockwise (looking down from above). Uncheck for clockwise.")]
    public bool anticlockwise = true;

    [Tooltip("Optional starting angle offset in degrees.")]
    public float startAngleDegrees = 0f;

    [Tooltip("If true, adjacent lanes are linked both ways so cars can move in/out. If false, no lane changes are wired.")]
    public bool linkLaneNeighbors = true;

    [ContextMenu("Rebuild waypoints")]
    public void Rebuild()
    {
        // Remove all existing children (previous lanes / waypoints).
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child);
            else DestroyImmediate(child);
        }

        // [lane][node]
        Waypoint[,] grid = new Waypoint[laneCount, waypointCount];
        float startRad = startAngleDegrees * Mathf.Deg2Rad;
        float dir = anticlockwise ? 1f : -1f;

        for (int lane = 0; lane < laneCount; lane++)
        {
            float laneRadius = radius + lane * laneWidth;
            Transform laneParent = new GameObject($"Lane_{lane}").transform;
            laneParent.SetParent(transform, worldPositionStays: false);
            laneParent.localPosition = Vector3.zero;
            laneParent.localRotation = Quaternion.identity;

            for (int n = 0; n < waypointCount; n++)
            {
                float t = n / (float)waypointCount;
                float angle = startRad + t * Mathf.PI * 2f * dir;
                Vector3 local = new Vector3(Mathf.Cos(angle) * laneRadius, height, Mathf.Sin(angle) * laneRadius);

                GameObject go = new GameObject($"WP_L{lane}_{n:00}");
                go.transform.SetParent(laneParent, worldPositionStays: false);
                go.transform.localPosition = local;
                grid[lane, n] = go.AddComponent<Waypoint>();
            }
        }

        // Wire nextWaypoints around each ring into a closed loop.
        for (int lane = 0; lane < laneCount; lane++)
        {
            for (int n = 0; n < waypointCount; n++)
            {
                var wp = grid[lane, n];
                wp.nextWaypoints.Clear();
                wp.nextWaypoints.Add(grid[lane, (n + 1) % waypointCount]);
            }
        }

        // Wire laneNeighbors between adjacent rings (same node index).
        if (linkLaneNeighbors)
        {
            for (int lane = 0; lane < laneCount; lane++)
            {
                for (int n = 0; n < waypointCount; n++)
                {
                    var wp = grid[lane, n];
                    wp.laneNeighbors.Clear();
                    if (lane - 1 >= 0)        wp.laneNeighbors.Add(grid[lane - 1, n]);
                    if (lane + 1 < laneCount) wp.laneNeighbors.Add(grid[lane + 1, n]);
                }
            }
        }

        Debug.Log($"[CircleTrackBuilder] Rebuilt {laneCount} lane(s) x {waypointCount} nodes " +
                  $"({laneCount * waypointCount} waypoints) from radius {radius:0.##}.");
    }

    /// <summary>First waypoint (node 0) of each lane. Useful for hooking up a spawner.</summary>
    public List<Waypoint> GetLaneStarts()
    {
        var starts = new List<Waypoint>();
        foreach (Transform lane in transform)
        {
            if (lane.childCount > 0)
            {
                var wp = lane.GetChild(0).GetComponent<Waypoint>();
                if (wp != null) starts.Add(wp);
            }
        }
        return starts;
    }

    // Visualise every ring in the Scene view even before you press Rebuild.
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        const int segs = 64;
        for (int lane = 0; lane < Mathf.Max(1, laneCount); lane++)
        {
            float laneRadius = radius + lane * laneWidth;
            Vector3 prev = transform.TransformPoint(new Vector3(laneRadius, height, 0f));
            for (int i = 1; i <= segs; i++)
            {
                float a = (i / (float)segs) * Mathf.PI * 2f;
                Vector3 p = transform.TransformPoint(new Vector3(Mathf.Cos(a) * laneRadius, height, Mathf.Sin(a) * laneRadius));
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
