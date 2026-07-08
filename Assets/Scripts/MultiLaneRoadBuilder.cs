using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Editor helper that generates a straight, multi-lane road as a grid of
/// <see cref="Waypoint"/> children:
///   - <see cref="laneCount"/> parallel lanes, spaced <see cref="laneWidth"/> apart.
///   - <see cref="nodesPerLane"/> waypoints along each lane, spaced <see cref="spacing"/> apart.
///   - nextWaypoints wired down each lane (in the road's local +Z direction).
///   - laneNeighbors wired sideways between adjacent lanes so CarAI can overtake.
///
/// Rotate / move this GameObject to place and orient the whole road; waypoints are
/// created as local children so they follow the transform. Then choose
/// "Rebuild road" from the component's context menu (the three-dot icon).
///
/// This does NOT change how CarAI drives. It only lays out and links waypoints.
/// </summary>
[ExecuteAlways]
public class MultiLaneRoadBuilder : MonoBehaviour
{
    [Header("Lanes")]
    [Min(1)] public int laneCount = 3;
    [Tooltip("Sideways distance between adjacent lane centres (metres).")]
    public float laneWidth = 2.5f;

    [Header("Length")]
    [Min(2)] public int nodesPerLane = 20;
    [Tooltip("Distance between waypoints along a lane (metres).")]
    public float spacing = 5f;

    [Header("Placement")]
    [Tooltip("Y height of each waypoint (car height).")]
    public float height = 0.25f;
    [Tooltip("If true, adjacent lanes are linked both ways so cars can move left or right. If false, no lane changes are wired.")]
    public bool linkLaneNeighbors = true;

    // Keep references so re-running can rewire cleanly.
    // [lane][node]
    private void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child);
            else DestroyImmediate(child);
        }
    }

    [ContextMenu("Rebuild road")]
    public void Rebuild()
    {
        ClearChildren();

        Waypoint[,] grid = new Waypoint[laneCount, nodesPerLane];

        // Centre the lanes around x = 0 so the transform sits in the middle of the road.
        float laneOffset0 = -(laneCount - 1) * 0.5f * laneWidth;

        for (int lane = 0; lane < laneCount; lane++)
        {
            float x = laneOffset0 + lane * laneWidth;
            Transform laneParent = new GameObject($"Lane_{lane}").transform;
            laneParent.SetParent(transform, worldPositionStays: false);
            laneParent.localPosition = Vector3.zero;
            laneParent.localRotation = Quaternion.identity;

            for (int n = 0; n < nodesPerLane; n++)
            {
                float z = n * spacing;
                GameObject go = new GameObject($"WP_L{lane}_{n:00}");
                go.transform.SetParent(laneParent, worldPositionStays: false);
                go.transform.localPosition = new Vector3(x, height, z);
                grid[lane, n] = go.AddComponent<Waypoint>();
            }
        }

        // Wire nextWaypoints down each lane.
        for (int lane = 0; lane < laneCount; lane++)
        {
            for (int n = 0; n < nodesPerLane; n++)
            {
                var wp = grid[lane, n];
                wp.nextWaypoints.Clear();
                if (n + 1 < nodesPerLane)
                    wp.nextWaypoints.Add(grid[lane, n + 1]);
            }
        }

        // Wire laneNeighbors sideways (same node index in adjacent lanes).
        if (linkLaneNeighbors)
        {
            for (int lane = 0; lane < laneCount; lane++)
            {
                for (int n = 0; n < nodesPerLane; n++)
                {
                    var wp = grid[lane, n];
                    wp.laneNeighbors.Clear();
                    if (lane - 1 >= 0)        wp.laneNeighbors.Add(grid[lane - 1, n]);
                    if (lane + 1 < laneCount) wp.laneNeighbors.Add(grid[lane + 1, n]);
                }
            }
        }

        Debug.Log($"[MultiLaneRoadBuilder] Built {laneCount} lanes x {nodesPerLane} nodes " +
                  $"({laneCount * nodesPerLane} waypoints).");
    }

    /// <summary>First waypoints of each lane (node 0). Useful for hooking up a spawner.</summary>
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

    private void OnDrawGizmosSelected()
    {
        // Preview the road footprint even before Rebuild.
        Gizmos.color = new Color(0.2f, 0.2f, 0.2f, 1f);
        float laneOffset0 = -(laneCount - 1) * 0.5f * laneWidth;
        float length = (nodesPerLane - 1) * spacing;
        for (int lane = 0; lane < laneCount; lane++)
        {
            float x = laneOffset0 + lane * laneWidth;
            Vector3 a = transform.TransformPoint(new Vector3(x, height, 0f));
            Vector3 b = transform.TransformPoint(new Vector3(x, height, length));
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(a, b);
        }
    }
}
