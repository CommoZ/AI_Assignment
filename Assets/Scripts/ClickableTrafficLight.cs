using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Makes a TrafficLight clickable under the new Input System. Click the object
/// (which must have a Collider) to toggle the light between Green (go) and Red (stop).
///
/// Cars are controlled automatically: their stop-line Waypoints reference this
/// TrafficLight in their "Controlling Light" field, so when it is not green, cars
/// approaching those waypoints stop.
///
/// Setup:
///  - Put this on the same GameObject as a TrafficLight (or assign 'targetLight').
///  - The GameObject needs a Collider big enough to click (e.g. a Box Collider).
///  - There must be a Camera tagged MainCamera in the scene.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ClickableTrafficLight : MonoBehaviour
{
    [Tooltip("The light to toggle. If empty, a TrafficLight on this GameObject is used.")]
    public TrafficLight targetLight;

    [Tooltip("Force the light into manual mode on start so it doesn't auto-cycle.")]
    public bool forceManualControl = true;

    [Tooltip("State the light starts in when manual control is forced.")]
    public TrafficLight.State startState = TrafficLight.State.Red;

    [Tooltip("Max distance for the click raycast.")]
    public float clickRayDistance = 500f;

    private Collider myCollider;

    private void Awake()
    {
        myCollider = GetComponent<Collider>();
        if (targetLight == null)
            targetLight = GetComponent<TrafficLight>();
    }

    private void Start()
    {
        if (targetLight == null)
        {
            Debug.LogWarning($"{name}: ClickableTrafficLight has no TrafficLight to control.", this);
            return;
        }

        if (forceManualControl)
        {
            targetLight.manualControl = true;
            targetLight.SetState(startState);
        }
    }

    private void Update()
    {
        if (targetLight == null || myCollider == null) return;

        // Only act on the frame the left mouse button is pressed.
        Mouse mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector2 screenPos = mouse.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(screenPos);
        if (Physics.Raycast(ray, out RaycastHit hit, clickRayDistance) && hit.collider == myCollider)
        {
            targetLight.Toggle();
        }
    }
}
