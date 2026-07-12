using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marks a boundary waypoint of a road module (a straight segment, an intersection arm, a
/// roundabout arm, a ramp) so separate modules can be wired into one graph without hand-editing
/// every <see cref="Waypoint.nextWaypoints"/> list.
///
///  - An <see cref="Kind.Exit"/> connector sits on the last node where traffic LEAVES this module.
///  - An <see cref="Kind.Entry"/> connector sits on the first node where traffic ENTERS a module.
///
/// <see cref="StitchAll"/> (invoked by <see cref="RoadNetworkStitcher"/> or a builder) links every
/// Exit to the nearest compatible Entry within a snap radius by appending the entry node to the
/// exit node's successor list. Snap two module endpoints close together and they connect.
/// </summary>
public class RoadConnector : MonoBehaviour
{
    public enum Kind { Entry, Exit }

    [Tooltip("Entry = traffic flows INTO the module here. Exit = traffic flows OUT of the module here.")]
    public Kind kind = Kind.Exit;

    [Tooltip("Which lane this connector belongs to (matched when stitching multi-lane seams).")]
    public int laneIndex = 0;

    [Tooltip("The boundary waypoint this connector represents. Defaults to a Waypoint on this GameObject.")]
    public Waypoint node;

    private void Reset()
    {
        node = GetComponent<Waypoint>();
    }

    /// <summary>World direction traffic flows through this connector (this transform's forward).</summary>
    public Vector3 FlowDirection => transform.forward;

    /// <summary>
    /// Link every Exit connector to the nearest Entry connector whose node is within
    /// <paramref name="snapDistance"/> and roughly ahead of it (same flow direction). Returns the
    /// number of edges created.
    /// </summary>
    public static int StitchAll(float snapDistance)
    {
        var connectors = FindObjectsByType<RoadConnector>(FindObjectsSortMode.None);
        var entries = new List<RoadConnector>();
        var exits = new List<RoadConnector>();
        foreach (var c in connectors)
        {
            if (c == null || c.node == null) continue;
            if (c.kind == Kind.Entry) entries.Add(c);
            else exits.Add(c);
        }

        int created = 0;
        float snapSqr = snapDistance * snapDistance;
        foreach (var ex in exits)
        {
            RoadConnector best = null;
            float bestSqr = snapSqr;
            foreach (var en in entries)
            {
                if (en.node == ex.node) continue;
                if (en.laneIndex != ex.laneIndex) continue;
                // Flow must be broadly aligned so we don't join opposing endpoints.
                if (Vector3.Dot(ex.FlowDirection, en.FlowDirection) < 0.3f) continue;
                float d = (en.node.transform.position - ex.node.transform.position).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = en; }
            }
            if (best != null && !ex.node.nextWaypoints.Contains(best.node))
            {
                ex.node.nextWaypoints.Add(best.node);
                created++;
            }
        }
        return created;
    }

    private void OnDrawGizmos()
    {
        if (!SimulationGizmoSettings.ShowWaypoints) return;
        Gizmos.color = kind == Kind.Entry ? new Color(0.2f, 1f, 0.4f) : new Color(1f, 0.4f, 0.2f);
        Gizmos.DrawWireCube(transform.position + Vector3.up * 0.15f, Vector3.one * 0.5f);
        Gizmos.DrawLine(transform.position, transform.position + FlowDirection * 1.2f);
    }
}
