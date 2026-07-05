using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A node in the road network. Cars move between waypoints.
/// Each waypoint can list several "next" waypoints (for lanes / turns);
/// the car picks one at random when it arrives.
/// If <see cref="controllingLight"/> is set, the car must stop before this
/// waypoint while the light is not green.
/// </summary>
public class Waypoint : MonoBehaviour
{
    [Tooltip("Possible waypoints a car may go to after reaching this one.")]
    public List<Waypoint> nextWaypoints = new List<Waypoint>();

    [Tooltip("Optional traffic light guarding the approach to this waypoint. Leave empty if none.")]
    public TrafficLight controllingLight;

    /// <summary>Distance (m) from waypoint where cars should stop when the light is red.</summary>
    [Tooltip("How far before this waypoint a car should stop when the light is red.")]
    public float stopLineDistance = 2f;

    public Waypoint GetRandomNext()
    {
        if (nextWaypoints == null || nextWaypoints.Count == 0) return null;
        return nextWaypoints[Random.Range(0, nextWaypoints.Count)];
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = controllingLight != null ? Color.red : Color.cyan;
        Gizmos.DrawWireSphere(transform.position, 0.4f);

        if (nextWaypoints == null) return;
        Gizmos.color = Color.yellow;
        foreach (var next in nextWaypoints)
        {
            if (next == null) continue;
            Vector3 a = transform.position;
            Vector3 b = next.transform.position;
            Gizmos.DrawLine(a, b);
            // small arrow head
            Vector3 dir = (b - a).normalized;
            Vector3 mid = Vector3.Lerp(a, b, 0.9f);
            Vector3 right = Quaternion.Euler(0, 25f, 0) * -dir * 0.6f;
            Vector3 left  = Quaternion.Euler(0, -25f, 0) * -dir * 0.6f;
            Gizmos.DrawLine(mid, mid + right);
            Gizmos.DrawLine(mid, mid + left);
        }
    }
}
