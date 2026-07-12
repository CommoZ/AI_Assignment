using UnityEngine;

/// <summary>
/// Drop this on a root GameObject and choose "Stitch connectors" from its context menu (or press
/// it at runtime) to wire every <see cref="RoadConnector"/> Exit to the nearest matching Entry.
/// Use it after positioning several road modules (segments / junctions) so their graphs join into
/// one network. Safe to run repeatedly — duplicate edges are skipped.
/// </summary>
[ExecuteAlways]
public class RoadNetworkStitcher : MonoBehaviour
{
    [Tooltip("Two module endpoints closer than this (metres) are joined.")]
    public float snapDistance = 1.5f;

    [ContextMenu("Stitch connectors")]
    public void Stitch()
    {
        int n = RoadConnector.StitchAll(snapDistance);
        Debug.Log($"[RoadNetworkStitcher] Linked {n} module seam(s).");
    }
}
