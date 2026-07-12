using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Editor helper that generates a single-lane on-ramp (merge) or off-ramp (diverge): a short chain
/// of <see cref="Waypoint"/>s that curves sideways from one road to another. Position it so its
/// Entry connector snaps to the feeding road's exit and its Exit connector snaps to the receiving
/// road's entry (via <see cref="RoadNetworkStitcher"/>).
///
///  - On-ramp: the merge node gives way (<see cref="Waypoint.isYield"/>) so joining cars wait for a
///    gap in the faster through-traffic.
///  - Off-ramp: no give-way; cars simply peel off.
///
/// Choose "Rebuild ramp" from the component context menu.
/// </summary>
[ExecuteAlways]
public class RampBuilder : MonoBehaviour
{
    public enum RampType { OnRamp, OffRamp }

    [Header("Type")]
    public RampType type = RampType.OnRamp;

    [Header("Shape")]
    [Tooltip("Number of nodes along the ramp.")]
    [Min(2)] public int nodeCount = 6;
    [Tooltip("Length of the ramp along its forward (+Z) axis (metres).")]
    public float length = 20f;
    [Tooltip("Sideways offset of the ramp START from the forward axis (metres).")]
    public float startOffsetX = 4f;
    [Tooltip("Sideways offset of the ramp END from the forward axis (metres).")]
    public float endOffsetX = 0f;
    [Tooltip("Y height of each waypoint.")]
    public float height = 0.25f;

    [ContextMenu("Rebuild ramp")]
    public void Rebuild()
    {
        ClearChildren();

        var nodes = new Waypoint[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            float t = i / (float)(nodeCount - 1);
            // Smoothstep the lateral shift so the merge/diverge eases in.
            float smooth = t * t * (3f - 2f * t);
            float x = Mathf.Lerp(startOffsetX, endOffsetX, smooth);
            float z = t * length;
            nodes[i] = NewWaypoint($"Ramp_{i:00}", new Vector3(x, height, z));
        }

        // Wire sequentially and orient each node toward the next.
        for (int i = 0; i < nodeCount; i++)
        {
            if (i + 1 < nodeCount)
            {
                nodes[i].nextWaypoints.Add(nodes[i + 1]);
                Vector3 flow = nodes[i + 1].transform.localPosition - nodes[i].transform.localPosition;
                flow.y = 0f;
                if (flow.sqrMagnitude > 0.001f)
                    nodes[i].transform.localRotation = Quaternion.LookRotation(flow.normalized, Vector3.up);
            }
            else if (nodeCount >= 2)
            {
                nodes[i].transform.localRotation = nodes[i - 1].transform.localRotation;
            }
        }

        if (type == RampType.OnRamp)
        {
            // The last node merges onto the through road: give way there.
            nodes[nodeCount - 1].isYield = true;
            nodes[nodeCount - 1].stopLineDistance = 1.5f;
        }

        AddConnector(nodes[0], RoadConnector.Kind.Entry);
        AddConnector(nodes[nodeCount - 1], RoadConnector.Kind.Exit);

        RoadConnector.StitchAll(1.5f);
        Debug.Log($"[RampBuilder] Built {type} with {nodeCount} nodes.");
    }

    private Waypoint NewWaypoint(string name, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = localPos;
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
}
