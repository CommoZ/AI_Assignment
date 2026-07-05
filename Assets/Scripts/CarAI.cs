using UnityEngine;

/// <summary>
/// Rigidbody-based car AI:
///  - Follows a chain of <see cref="Waypoint"/>s (rotating toward the next one).
///  - Slows / stops when a car ahead is detected by a forward SphereCast.
///  - Stops before a red light guarding its current target waypoint.
///  - Uses forces (a P-controller on forward speed) rather than setting velocity
///    directly, so collision impulses are preserved and momentum transfers between
///    cars like real-life physics.
/// </summary>
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
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
    public float turnSpeed = 10f;

    [Header("Sensing")]
    [Tooltip("How far ahead the car looks for other cars.")]
    public float sensorLength = 4f;
    [Tooltip("Radius of the forward spherecast used to detect cars.")]
    public float sensorRadius = 0.4f;
    [Tooltip("Layers that count as obstacles / other cars.")]
    public LayerMask carLayerMask = ~0;

    [Header("Physics")]
    [Tooltip("Kilograms. Heavier cars push lighter ones harder in collisions.")]
    public float mass = 800f;
    [Tooltip("How aggressively the driver corrects toward the target speed. Higher = snappier throttle/brake.")]
    public float driveGain = 8f;
    [Tooltip("How quickly sideways slide is bled off after a hit (force-based, stable up to ~30).")]
    public float lateralDamping = 20f;
    [Range(0f, 1f)]
    [Tooltip("Tyre grip. 0 = no grip (drifts freely), 1 = instant grip (nailed to heading, no visible slide). Applied AFTER lateralDamping and stays stable at any value.")]
    public float traction = 0.6f;
    [Tooltip("Rigidbody linear damping (air/rolling resistance).")]
    public float linearDamping = 8.0f;
    [Tooltip("Rigidbody angular damping — reduces spinning after off-centre hits.")]
    public float angularDamping = 5f;

    // Runtime
    private Rigidbody rb;
    private float currentSpeed;
    private bool destroyed;

    public float CurrentSpeed => currentSpeed;
    /// <summary>True when the car is essentially not moving (part of a jam).</summary>
    public bool IsStopped => Mathf.Abs(currentSpeed) < 0.2f;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = mass;
        rb.useGravity = false;
        rb.linearDamping = linearDamping;
        rb.angularDamping = angularDamping;
        // 2D-in-3D: cars only slide and yaw on the ground plane, they can't fly or tip.
        rb.constraints = RigidbodyConstraints.FreezePositionY
                       | RigidbodyConstraints.FreezeRotationX
                       | RigidbodyConstraints.FreezeRotationZ;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // Frictionless per-instance material so ground contact / grazing hits
        // don't secretly drain speed.
        Collider col = GetComponent<Collider>();
        if (col != null)
        {
            col.material = new PhysicsMaterial
            {
                dynamicFriction = 0f,
                staticFriction  = 0f,
                bounciness      = 0.05f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine   = PhysicsMaterialCombine.Average
            };
        }
    }

    private void OnEnable()
    {
        TrafficSimulationManager.Register(this);
    }

    private void OnDisable()
    {
        TrafficSimulationManager.Unregister(this);
    }

    private void FixedUpdate()
    {
        if (destroyed) return;
        float dt = Time.fixedDeltaTime;

        // No target? Coast to a stop.
        if (currentTarget == null)
        {
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, braking * dt);
            ApplyDriveForces();
            return;
        }

        Vector3 toTarget = currentTarget.transform.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;

        // Smoothly rotate toward the target (via physics-friendly MoveRotation).
        if (toTarget.sqrMagnitude > 0.001f)
        {
            Quaternion desired = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, desired, turnSpeed * dt));
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
            if (hitDistance < sensorLength * 0.5f) shouldStop = true;
            else if (hitDistance < sensorLength)
                currentSpeed = Mathf.Min(currentSpeed, maxSpeed * (hitDistance / sensorLength));
        }

        float targetSpeed = shouldStop ? 0f : maxSpeed;
        float rate = (targetSpeed > currentSpeed) ? acceleration : braking;
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, rate * dt);

        ApplyDriveForces();

        // Waypoint arrival.
        if (distance <= arriveDistance)
        {
            Waypoint next = currentTarget.GetRandomNext();
            if (next == null) { Despawn(); return; }
            currentTarget = next;
        }
    }

    /// <summary>
    /// P-controller: forward force proportional to (desired forward speed - actual
    /// forward speed), plus a lateral damping force to bleed off sideways slide.
    /// Using AddForce (rather than assigning linearVelocity) means collision impulses
    /// are preserved — momentum transfer between cars is visible.
    /// </summary>
    private void ApplyDriveForces()
    {
        Vector3 forward = transform.forward;
        Vector3 vel = rb.linearVelocity;
        float forwardVel = Vector3.Dot(vel, forward);

        // Forward drive: P-controller on speed error.
        float speedError = currentSpeed - forwardVel;
        rb.AddForce(forward * speedError * driveGain, ForceMode.Acceleration);

        // Force-based lateral damping (stable at low values, oscillates above ~30).
        Vector3 lateralVel = vel - forward * forwardVel;
        rb.AddForce(-lateralVel * lateralDamping, ForceMode.Acceleration);

        // Direct traction clamp: multiplicatively bleed off the remaining lateral velocity.
        // Numerically stable for any traction in [0..1]. traction=1 means the car is
        // nailed to its heading (no slide at all, but sideways collision impulses are killed).
        if (traction > 0f)
        {
            Vector3 newVel = forward * forwardVel + lateralVel * (1f - traction);
            rb.linearVelocity = newVel;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        // Only react to car-on-car collisions.
        if (collision.rigidbody == null) return;
        if (collision.rigidbody.GetComponent<CarAI>() == null) return;

        // Drop the AI's target speed to whatever the impact left us with, so the
        // driver 'goes with' the shove for a moment instead of instantly re-throttling.
        float forwardVelAfterImpact = Vector3.Dot(rb.linearVelocity, transform.forward);
        currentSpeed = Mathf.Clamp(forwardVelAfterImpact, 0f, maxSpeed);
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
