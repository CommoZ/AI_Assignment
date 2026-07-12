using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Free-fly camera for inspecting the simulation. Uses the new Input System:
///  - Hold RIGHT mouse button and move the mouse to look around.
///  - W/A/S/D move, Q/E drop/rise, hold Shift to move faster.
///  - Mouse scroll wheel dollies forward/back (zoom).
/// Runs on unscaled time so it still works while the sim is paused or slowed.
/// </summary>
public class CameraController : MonoBehaviour
{
    [Tooltip("Metres per second at normal speed.")]
    public float moveSpeed = 20f;
    [Tooltip("Multiplier applied while holding Shift.")]
    public float fastMultiplier = 3f;
    [Tooltip("Look sensitivity (degrees per pixel of mouse movement).")]
    public float lookSpeed = 0.15f;
    [Tooltip("Zoom (dolly) speed for the scroll wheel.")]
    public float zoomSpeed = 40f;

    private float yaw;
    private float pitch;

    private void Start()
    {
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x;
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        float dt = Time.unscaledDeltaTime;

        // Look while the right mouse button is held.
        if (mouse != null && mouse.rightButton.isPressed)
        {
            Vector2 d = mouse.delta.ReadValue();
            yaw += d.x * lookSpeed;
            pitch = Mathf.Clamp(pitch - d.y * lookSpeed, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        if (kb != null)
        {
            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed) move += transform.forward;
            if (kb.sKey.isPressed) move -= transform.forward;
            if (kb.dKey.isPressed) move += transform.right;
            if (kb.aKey.isPressed) move -= transform.right;
            if (kb.eKey.isPressed) move += Vector3.up;
            if (kb.qKey.isPressed) move -= Vector3.up;

            float sp = moveSpeed * (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed ? fastMultiplier : 1f);
            transform.position += move.normalized * sp * dt;
        }

        // Scroll to zoom.
        if (mouse != null)
        {
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                transform.position += transform.forward * Mathf.Sign(scroll) * zoomSpeed * dt;
        }
    }
}
