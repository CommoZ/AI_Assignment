using UnityEngine;

/// <summary>
/// Very small car AI that:
///  - Follows a chain of <see cref="Waypoint"/>s (rotating toward the next one).
///  - Slows / stops when a car ahead is detected by a forward raycast.
///  - Stops before a red light guarding its current target waypoint.
///
/// Uses direct transform movement (no Rigidbody) for simplicity and stability.
/// Attach to a car prefab that has a Collider on its own layer.
/// </summary>
[RequireComponent(typeof(Collider))]
public class CarAI : MonoBehaviour
{
    [Header("Waypoints")]
    [Tooltip("The waypoint this car is currently driving toward. Set by the spawner.")]
    public Waypoint currentTarget;

    [Tooltip("How close the car must get before it switches to the next waypoint.")]
    public float arriveDistance = 1.0f;

    [Header("Speed")]
    public float maxSpeed = 8f;
    public float acceleration = 6f;
    public float braking = 12f;
    public float turnSpeed = 6f;

    [Header("Sensing")]
    [Tooltip("How far ahead the car looks for other cars.")]
    public float sensorLength = 4f;
    [Tooltip("Radius of the forward spherecast used to detect cars.")]
    public float sensorRadius = 0.4f;
    [Tooltip("Layers that count as obstacles / other cars.")]
    public LayerMask carLayerMask = ~0;

    // Runtime
    private float currentSpeed;
    private bool destroyed;

    public float CurrentSpeed => currentSpeed;
    /// <summary>True when the car is essentially not moving (part of a jam).</summary>
    public bool IsStopped => currentSpeed < 0.2f;

    private void OnEnable()
    {
        TrafficSimulationManager.Register(this);
    }

    private void OnDisable()
    {
        TrafficSimulationManager.Unregister(this);
    }

    private void Update()
    {
        if (destroyed) return;

        // No target? Idle.
        if (currentTarget == null)
        {
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, braking * Time.deltaTime);
            return;
        }

        Vector3 toTarget = currentTarget.transform.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;

        // Rotate smoothly to face the target.
        if (toTarget.sqrMagnitude > 0.001f)
        {
            Quaternion desired = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, desired, turnSpeed * Time.deltaTime);
        }

        // Decide whether to accelerate or brake.
        bool shouldStop = false;

        // 1) Red light at the current target waypoint?
        if (currentTarget.controllingLight != null && !currentTarget.controllingLight.CanPass)
        {
            if (distance <= currentTarget.stopLineDistance + 0.5f)
                shouldStop = true;
        }

        // 2) Car directly ahead?
        if (SenseCarAhead(out float hitDistance))
        {
            // Stop before hitting it; slow proportionally as we get near.
            if (hitDistance < sensorLength * 0.5f) shouldStop = true;
            else if (hitDistance < sensorLength)
                currentSpeed = Mathf.Min(currentSpeed, maxSpeed * (hitDistance / sensorLength));
        }

        float targetSpeed = shouldStop ? 0f : maxSpeed;
        float rate = (targetSpeed > currentSpeed) ? acceleration : braking;
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, rate * Time.deltaTime);

        // Move forward.
        transform.position += transform.forward * currentSpeed * Time.deltaTime;

        // Advance to next waypoint on arrival.
        if (distance <= arriveDistance)
        {
            Waypoint next = currentTarget.GetRandomNext();
            if (next == null)
            {
                // End of route — remove this car.
                Despawn();
                return;
            }
            currentTarget = next;
        }
    }

    private bool SenseCarAhead(out float distance)
    {
        distance = 0f;
        Vector3 origin = transform.position + transform.forward * 0.5f + Vector3.up * 0.3f;
        if (Physics.SphereCast(origin, sensorRadius, transform.forward, out RaycastHit hit,
                sensorLength, carLayerMask, QueryTriggerInteraction.Ignore))
        {
            // Ignore self collider.
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform))
                return false;
            distance = hit.distance;
            return true;
        }
        return false;
    }

    public void Despawn()
    {
        destroyed = true;
        Destroy(gameObject);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.magenta;
        Vector3 origin = transform.position + transform.forward * 0.5f + Vector3.up * 0.3f;
        Gizmos.DrawLine(origin, origin + transform.forward * sensorLength);
        Gizmos.DrawWireSphere(origin + transform.forward * sensorLength, sensorRadius);
    }
}
