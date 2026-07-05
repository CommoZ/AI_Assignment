using UnityEngine;

/// <summary>
/// Editor helper. Attach to an empty GameObject and choose "Rebuild waypoints"
/// from the component's context menu (three-dot icon in the Inspector, or
/// right-click the component header). It removes any child GameObjects and
/// generates <see cref="waypointCount"/> new <see cref="Waypoint"/> children
/// spaced evenly around a circle of the given radius, linked into a closed loop.
///
/// This does NOT change how <see cref="CarAI"/> drives — cars still go waypoint
/// to waypoint. It just lets you produce a very fine ring (e.g. 48 or 64 nodes)
/// so the path looks like an exact circle.
/// </summary>
[ExecuteAlways]
public class CircleTrackBuilder : MonoBehaviour
{
    [Tooltip("Radius of the circle, in metres.")]
    public float radius = 10f;

    [Tooltip("Number of waypoints around the ring. Higher = smoother circle. 32–64 looks perfect.")]
    [Min(3)] public int waypointCount = 48;

    [Tooltip("Y position of each waypoint (car height).")]
    public float height = 0.25f;

    [Tooltip("If true, cars go anticlockwise (looking down from above). Uncheck for clockwise.")]
    public bool anticlockwise = true;

    [Tooltip("Optional starting angle offset in degrees.")]
    public float startAngleDegrees = 0f;

    [ContextMenu("Rebuild waypoints")]
    public void Rebuild()
    {
        // Remove all existing children (previous waypoints).
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child);
            else DestroyImmediate(child);
        }

        // Create fresh waypoints on the circle.
        Waypoint[] wps = new Waypoint[waypointCount];
        float startRad = startAngleDegrees * Mathf.Deg2Rad;
        for (int i = 0; i < waypointCount; i++)
        {
            float t = i / (float)waypointCount;
            float angle = startRad + t * Mathf.PI * 2f * (anticlockwise ? 1f : -1f);
            Vector3 local = new Vector3(Mathf.Cos(angle) * radius, height, Mathf.Sin(angle) * radius);

            GameObject go = new GameObject($"WP_{i:00}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = local;
            wps[i] = go.AddComponent<Waypoint>();
        }

        // Link into a closed loop: 0 -> 1 -> 2 -> ... -> N-1 -> 0
        for (int i = 0; i < waypointCount; i++)
        {
            wps[i].nextWaypoints.Clear();
            wps[i].nextWaypoints.Add(wps[(i + 1) % waypointCount]);
        }

        Debug.Log($"[CircleTrackBuilder] Rebuilt {waypointCount} waypoints on a radius-{radius:0.##} ring.");
    }

    // Visualise the ring in the Scene view even before you press Rebuild.
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        const int segs = 64;
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
