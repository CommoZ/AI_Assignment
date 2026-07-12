using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Editor helper that generates a single-lane roundabout: a circulating ring plus a number of
/// radial arms. Cars on the ring have priority; a car entering from an arm passes through a
/// give-way node (<see cref="Waypoint.isYield"/>) and waits for a gap in circulating traffic.
/// From each junction the ring both continues round AND forks out to that arm's exit lane, so a
/// routed car leaves at the correct arm.
///
/// Arm endpoints expose <see cref="RoadConnector"/>s so external roads snap on. Choose
/// "Rebuild roundabout" from the component context menu.
/// </summary>
[ExecuteAlways]
public class RoundaboutBuilder : MonoBehaviour
{
    [Header("Ring")]
    [Tooltip("Radius of the circulating lane (metres).")]
    public float radius = 8f;
    [Tooltip("Number of nodes around the ring. Higher = smoother circle.")]
    [Min(8)] public int waypointCount = 32;
    [Tooltip("If true, cars circulate anticlockwise (looking down).")]
    public bool anticlockwise = true;

    [Header("Arms")]
    [Tooltip("Number of evenly-spaced arms (roads joining the roundabout).")]
    [Min(1)] public int armCount = 4;
    [Tooltip("Angle offset for the first arm (degrees).")]
    public float armStartAngle = 0f;
    [Tooltip("Length of each arm from the ring to its connector endpoint (metres).")]
    public float armLength = 8f;
    [Tooltip("Half-separation between an arm's inbound and outbound lanes (metres).")]
    public float laneWidth = 2.5f;

    [Header("Placement")]
    public float height = 0.25f;

    [ContextMenu("Rebuild roundabout")]
    public void Rebuild()
    {
        ClearChildren();

        float dir = anticlockwise ? 1f : -1f;
        var ring = new Waypoint[waypointCount];

        Transform ringParent = new GameObject("Ring").transform;
        ringParent.SetParent(transform, false);
        ringParent.localPosition = Vector3.zero;

        for (int n = 0; n < waypointCount; n++)
        {
            float angle = (n / (float)waypointCount) * Mathf.PI * 2f * dir;
            Vector3 pos = new Vector3(Mathf.Cos(angle) * radius, height, Mathf.Sin(angle) * radius);
            ring[n] = NewWaypoint($"Ring_{n:00}", pos, ringParent, Vector3.zero);
        }
        // Circulating loop.
        for (int n = 0; n < waypointCount; n++)
            ring[n].nextWaypoints.Add(ring[(n + 1) % waypointCount]);

        // Arms.
        for (int i = 0; i < armCount; i++)
        {
            float armAngle = (armStartAngle + i * (360f / armCount)) * Mathf.Deg2Rad;
            Vector3 radial = new Vector3(Mathf.Cos(armAngle), 0f, Mathf.Sin(armAngle));
            // Nearest ring node index to this arm's angle.
            float frac = Mathf.Repeat(armAngle * dir / (Mathf.PI * 2f), 1f);
            int k = Mathf.RoundToInt(frac * waypointCount) % waypointCount;

            Vector3 inRight = Vector3.Cross(Vector3.up, -radial).normalized;
            Vector3 outRight = Vector3.Cross(Vector3.up, radial).normalized;
            float lat = laneWidth * 0.5f;

            Transform armParent = new GameObject($"Arm_{i}").transform;
            armParent.SetParent(transform, false);
            armParent.localPosition = Vector3.zero;

            Waypoint approach = NewWaypoint($"Arm{i}_approach",
                radial * (radius + armLength) + inRight * lat, armParent, -radial);
            Waypoint mergeIn = NewWaypoint($"Arm{i}_merge",
                radial * (radius + 1.2f) + inRight * lat, armParent, -radial);
            Waypoint exitOut = NewWaypoint($"Arm{i}_exit",
                radial * (radius + 1.2f) + outRight * lat, armParent, radial);
            Waypoint depart = NewWaypoint($"Arm{i}_depart",
                radial * (radius + armLength) + outRight * lat, armParent, radial);

            // Inbound: approach -> give-way merge -> ring node.
            approach.nextWaypoints.Add(mergeIn);
            mergeIn.isYield = true;
            mergeIn.stopLineDistance = 1.2f;
            mergeIn.nextWaypoints.Add(ring[k]);

            // Ring node also forks out this arm.
            ring[k].nextWaypoints.Add(exitOut);
            exitOut.nextWaypoints.Add(depart);

            AddConnector(approach, RoadConnector.Kind.Entry);
            AddConnector(depart, RoadConnector.Kind.Exit);
        }

        RoadConnector.StitchAll(1.5f);
        Debug.Log($"[RoundaboutBuilder] Built ring of {waypointCount} nodes with {armCount} arms.");
    }

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

    private void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var go = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        const int segs = 48;
        Vector3 prev = transform.TransformPoint(new Vector3(radius, height, 0f));
        for (int i = 1; i <= segs; i++)
        {
            float a = (i / (float)segs) * Mathf.PI * 2f;
            Vector3 p = transform.TransformPoint(new Vector3(Mathf.Cos(a) * radius, height, Mathf.Sin(a) * radius));
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
}
