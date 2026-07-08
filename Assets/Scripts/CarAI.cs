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

    [Tooltip("If ticked, this car picks a random maxSpeed between Random Speed Min/Max when it spawns.")]
    public bool randomizeSpeed = false;
    [Tooltip("Lowest possible maxSpeed when Randomize Speed is on.")]
    public float randomSpeedMin = 4f;
    [Tooltip("Highest possible maxSpeed when Randomize Speed is on.")]
    public float randomSpeedMax = 12f;

    [Header("Speed colour")]
    [Tooltip("If ticked, the car's body is tinted from red (stopped) to green (max speed).")]
    public bool colorBySpeed = true;
    [Tooltip("Renderer to tint. If left empty, the first renderer on this car (or its children) is used.")]
    public Renderer bodyRenderer;
    [Tooltip("Colour at zero speed.")]
    public Color slowColor = Color.red;
    [Tooltip("Colour at max speed.")]
    public Color fastColor = Color.green;

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

    [Header("Lane changing / overtaking (highway)")]
    [Tooltip("If true, when a SLOWER car is detected ahead the car may retarget to a faster/clear lane neighbour.")]
    public bool enableLaneChanges = true;
    [Tooltip("Seconds that must pass between two lane changes on the same car.")]
    public float laneChangeCooldown = 2f;
    [Tooltip("A car ahead counts as 'slower' only if it is at least this much slower than my current speed (m/s).")]
    public float overtakeSpeedMargin = 1.5f;
    [Tooltip("Radius around a candidate lane-neighbour that must be clear of other cars before moving there.")]
    public float laneChangeClearRadius = 2.5f;

    // Runtime
    private Rigidbody rb;
    private float currentSpeed;
    private bool destroyed;
    private float lastLaneChangeTime = -999f;
    private MaterialPropertyBlock mpb;
    private static readonly int ColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorIdLegacy = Shader.PropertyToID("_Color");

    public float CurrentSpeed => currentSpeed;    /// <summary>True when the car is essentially not moving (part of a jam).</summary>
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

        // Per-car random top speed so faster cars catch and overtake slower ones.
        if (randomizeSpeed)
            maxSpeed = Random.Range(randomSpeedMin, randomSpeedMax);

        // Cache the renderer to tint by speed.
        if (bodyRenderer == null)
            bodyRenderer = GetComponentInChildren<Renderer>();
        mpb = new MaterialPropertyBlock();

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
        if (SenseCarAhead(out float hitDistance, out CarAI carAhead))
        {
            if (hitDistance < sensorLength * 0.5f) shouldStop = true;
            else if (hitDistance < sensorLength)
                currentSpeed = Mathf.Min(currentSpeed, maxSpeed * (hitDistance / sensorLength));

            // 3) The car ahead is meaningfully SLOWER than the speed I want to drive
            //    -> consider overtaking. Compare against maxSpeed (my desired speed), NOT
            //    currentSpeed, because currentSpeed is already braking to match the blocker.
            bool aheadIsSlower = carAhead != null &&
                                 carAhead.CurrentSpeed < maxSpeed - overtakeSpeedMargin;
            if (enableLaneChanges && aheadIsSlower &&
                Time.time - lastLaneChangeTime > laneChangeCooldown)
            {
                if (TryOvertake(carAhead.CurrentSpeed))
                    lastLaneChangeTime = Time.time;
            }
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

        UpdateBodyColor();
    }

    /// <summary>
    /// Tints the body renderer from slowColor (0 speed) to fastColor (max speed).
    /// Uses a MaterialPropertyBlock so it doesn't create per-car material instances.
    /// </summary>
    private void UpdateBodyColor()
    {
        if (!colorBySpeed || bodyRenderer == null || mpb == null) return;

        float t = maxSpeed > 0.01f ? Mathf.Clamp01(currentSpeed / maxSpeed) : 0f;
        Color c = Color.Lerp(slowColor, fastColor, t);

        bodyRenderer.GetPropertyBlock(mpb);
        mpb.SetColor(ColorId, c);       // URP/HDRP Lit uses _BaseColor
        mpb.SetColor(ColorIdLegacy, c); // Built-in Standard uses _Color
        bodyRenderer.SetPropertyBlock(mpb);
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

    private bool SenseCarAhead(out float distance, out CarAI carAhead)
    {
        distance = 0f;
        carAhead = null;
        Vector3 origin = transform.position + transform.forward * 0.5f + Vector3.up * 0.3f;
        if (Physics.SphereCast(origin, sensorRadius, transform.forward, out RaycastHit hit,
                sensorLength, carLayerMask, QueryTriggerInteraction.Ignore))
        {
            // Ignore self collider.
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform))
                return false;
            distance = hit.distance;
            carAhead = hit.collider.GetComponentInParent<CarAI>();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Try to move into a lane neighbour of the current waypoint in order to overtake.
    /// A candidate lane is accepted only if:
    ///   - the spot around the neighbour waypoint is clear of cars, AND
    ///   - the nearest car in that lane (if any) is faster than the car we're stuck behind.
    /// Returns true if the target changed.
    /// </summary>
    private bool TryOvertake(float blockingCarSpeed)
    {
        if (currentTarget == null) return false;
        var neighbours = currentTarget.laneNeighbors;
        if (neighbours == null || neighbours.Count == 0) return false;

        for (int i = 0; i < neighbours.Count; i++)
        {
            Waypoint candidate = neighbours[i];
            if (candidate == null) continue;

            if (!IsSpotClear(candidate.transform.position, laneChangeClearRadius,
                             out CarAI nearestInLane))
                continue;

            // Accept if the lane is empty, or its nearest car is faster than our blocker.
            if (nearestInLane == null || nearestInLane.CurrentSpeed > blockingCarSpeed)
            {
                currentTarget = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True if no other car overlaps the given radius. Also outputs the nearest car found
    /// (null if none) so the caller can compare speeds.
    /// </summary>
    private bool IsSpotClear(Vector3 pos, float radius, out CarAI nearest)
    {
        nearest = null;
        float nearestSqr = float.MaxValue;
        bool clear = true;

        Collider[] hits = Physics.OverlapSphere(pos, radius, carLayerMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h == null) continue;
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
            var other = h.GetComponentInParent<CarAI>();
            if (other == null) continue;

            clear = false;
            float d = (h.transform.position - pos).sqrMagnitude;
            if (d < nearestSqr) { nearestSqr = d; nearest = other; }
        }
        return clear;
    }

    public void Despawn()
    {
        destroyed = true;
        Destroy(gameObject);
    }

    private void OnDrawGizmosSelected()
    {
        if (!SimulationGizmoSettings.ShowCarSensors) return;
        Gizmos.color = Color.magenta;
        Vector3 origin = transform.position + transform.forward * 0.5f + Vector3.up * 0.3f;
        Gizmos.DrawLine(origin, origin + transform.forward * sensorLength);
        Gizmos.DrawWireSphere(origin + transform.forward * sensorLength, sensorRadius);
    }
}
