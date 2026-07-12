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

    [Tooltip("Adjacent waypoints in parallel lanes. CarAI may retarget to one of these when overtaking a slower car.")]
    public List<Waypoint> laneNeighbors = new List<Waypoint>();

    [Tooltip("Optional traffic light guarding the approach to this waypoint. Leave empty if none.")]
    public TrafficLight controllingLight;

    /// <summary>Distance (m) from waypoint where cars should stop when the light is red.</summary>
    [Tooltip("How far before this waypoint a car should stop when the light is red.")]
    public float stopLineDistance = 2f;

    [Tooltip("If true, this is an unsignalled give-way point (junction entry / roundabout entry): a car must wait here until the conflict zone around it is clear of other cars.")]
    public bool isYield = false;

    private void OnEnable()
    {
        TrafficSimulationManager.RegisterNode(this);
    }

    private void OnDisable()
    {
        TrafficSimulationManager.UnregisterNode(this);
    }

    public Waypoint GetRandomNext()
    {
        if (nextWaypoints == null || nextWaypoints.Count == 0) return null;
        return nextWaypoints[Random.Range(0, nextWaypoints.Count)];
    }

    private void OnDrawGizmos()
    {
        if (SimulationGizmoSettings.ShowWaypoints)
        {
            // Red = light-controlled, orange = give-way/yield, cyan = plain node.
            Gizmos.color = controllingLight != null ? Color.red
                         : isYield ? new Color(1f, 0.55f, 0f)
                         : Color.cyan;
            Gizmos.DrawWireSphere(transform.position, 0.4f);

            if (nextWaypoints != null)
            {
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

        // Lane neighbours drawn in green so they're distinguishable from route arrows.
        if (SimulationGizmoSettings.ShowLaneLinks && laneNeighbors != null)
        {
            Gizmos.color = Color.green;
            foreach (var n in laneNeighbors)
            {
                if (n == null) continue;
                Gizmos.DrawLine(transform.position, n.transform.position);
            }
        }
    }
}
