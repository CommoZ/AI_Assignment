using UnityEngine;

/// <summary>
/// Marks a GameObject's position as an intersection reservation zone. At play time it registers a
/// mutual-exclusion region with <see cref="TrafficSimulationManager"/>; when reservations are on
/// (Perfect mode) only one car may occupy the region at once, so cross-traffic never collides
/// without needing traffic lights. Builders drop one of these at each junction centre.
/// </summary>
public class IntersectionZoneMarker : MonoBehaviour
{
    [Tooltip("Radius of the mutual-exclusion region (metres).")]
    public float radius = 5f;

    private TrafficSimulationManager.IntersectionZone zone;

    private void OnEnable()
    {
        zone = TrafficSimulationManager.RegisterZone(transform.position, radius);
    }

    private void OnDisable()
    {
        TrafficSimulationManager.UnregisterZone(zone);
        zone = null;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, radius);
    }
}
