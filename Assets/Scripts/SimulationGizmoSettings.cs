using UnityEngine;

/// <summary>
/// Global on/off switches for the simulation's editor gizmos (waypoint spheres,
/// route arrows, lane-neighbour links and car sensor rays).
///
/// Add ONE of these to any GameObject in the scene (e.g. the TrafficManager).
/// The values are mirrored into static fields so that Waypoint and CarAI gizmos
/// can read them without a reference. Toggling a box in the Inspector updates the
/// Scene view immediately, in both edit and play mode.
///
/// Note: this controls the coloured gizmos drawn by our scripts. The master
/// "Gizmos" button at the top of the Scene/Game view still overrides everything.
/// </summary>
[ExecuteAlways]
public class SimulationGizmoSettings : MonoBehaviour
{
    [Tooltip("Show the waypoint spheres and the yellow route arrows between them.")]
    public bool showWaypoints = true;

    [Tooltip("Show the green lines linking parallel-lane neighbours.")]
    public bool showLaneLinks = true;

    [Tooltip("Show the magenta forward sensor ray on the selected car.")]
    public bool showCarSensors = true;

    [Tooltip("Show the blue GPS route line from the selected car to its destination.")]
    public bool showRoutes = true;

    // Static mirror so gizmo code can read without a reference. Default all-on.
    public static bool ShowWaypoints  = true;
    public static bool ShowLaneLinks  = true;
    public static bool ShowCarSensors = true;
    public static bool ShowRoutes     = true;

    private void OnEnable()  { Apply(); }
    private void OnValidate() { Apply(); }
    private void Update()      { Apply(); } // keeps static values live in edit mode too

    private void Apply()
    {
        ShowWaypoints  = showWaypoints;
        ShowLaneLinks  = showLaneLinks;
        ShowCarSensors = showCarSensors;
        ShowRoutes     = showRoutes;
    }
}
