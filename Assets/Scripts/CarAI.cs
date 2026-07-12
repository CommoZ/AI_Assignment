using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rigidbody-based car AI:
///  - Follows a route of <see cref="Waypoint"/>s (rotating toward the next one). The route is
///    a shortest path from spawn to destination (see <see cref="Pathfinder"/>); with no route
///    it falls back to a random walk via <see cref="Waypoint.GetRandomNext"/>.
///  - Slows / stops when a car ahead is detected by a forward SphereCast.
///  - Stops before a red light guarding its current target waypoint, and yields at
///    unsignalled junctions / roundabout entries (see <see cref="Waypoint.isYield"/>).
///  - Slows into sharp corners so it doesn't oversteer through junctions.
///  - Its <see cref="DriverProfile"/> shapes every decision: reaction lag, follow gap, desired
///    speed, throttle sharpness, sensor range, overtaking keenness, and dynamic frustration.
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

    [Header("Routing (GPS)")]
    [Tooltip("Final destination waypoint. When set, the car follows 'route' to reach it and despawns on arrival.")]
    public Waypoint destination;
    [Tooltip("Shortest-path route from spawn to destination (filled by the spawner via Pathfinder). Empty = random walk.")]
    public List<Waypoint> route = new List<Waypoint>();
    private int routeIndex;

    [Header("Driver")]
    [Tooltip("Personality archetype driving this car. Assigned by the spawner from a DriverPopulation. If empty, the car uses its raw inspector values (legacy behaviour).")]
    public DriverProfile profile;

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

    [Header("Following")]
    [Tooltip("Minimum bumper-to-bumper gap (m) the car keeps to whatever is ahead. Scaled by the driver's follow-gap trait.")]
    public float minFollowGap = 2.5f;

    [Header("Cornering")]
    [Tooltip("Speed (m/s) the car is willing to carry through a 90-degree turn. Sharper turns are slower still, straights are unrestricted.")]
    public float cornerSpeed = 5.5f;

    [Header("Yield (unsignalled junctions)")]
    [Tooltip("Radius scanned around a yield waypoint's conflict zone; the car waits at the stop line while another car occupies it.")]
    public float yieldCheckRadius = 4f;

    // Runtime
    private Rigidbody rb;
    private float currentSpeed;
    private bool destroyed;
    private float lastLaneChangeTime = -999f;
    private MaterialPropertyBlock mpb;
    private static readonly int ColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorIdLegacy = Shader.PropertyToID("_Color");

    // Driver state
    private bool profileInitialized;
    private float reactionTime;                 // per-car perception lag (s), rolled from the profile
    private float frustration;                  // 0..1 mood, rises while blocked, decays while flowing
    private float currentThrottleMul = 1f;      // profile throttle responsiveness, applied in ApplyDriveForces

    public float CurrentSpeed => currentSpeed;    /// <summary>True when the car is essentially not moving (part of a jam).</summary>
    public bool IsStopped => Mathf.Abs(currentSpeed) < 0.2f;
    /// <summary>Current mood (0 calm .. 1 fully frustrated). Exposed for the jam metrics.</summary>
    public float Frustration => frustration;

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
        ReleaseZone(); // don't leave a junction reserved after we're gone
    }

    private void FixedUpdate()
    {
        if (destroyed) return;
        float dt = Time.fixedDeltaTime;

        EnsureProfileInit();

        // No target? Coast to a stop.
        if (currentTarget == null)
        {
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, braking * dt);
            ApplyDriveForces();
            return;
        }

        // ---- Profile-derived modifiers (all guard a null profile = legacy behaviour) ----
        float desiredFactor = profile != null ? profile.DesiredSpeedFactor(frustration) : 1f;
        float gapMul        = profile != null ? profile.FollowGapMultiplier(frustration) : 1f;
        float sensorMul     = profile != null ? profile.SensorRangeMultiplier() : 1f;
        currentThrottleMul  = profile != null ? profile.ThrottleResponse(frustration) : 1f;
        float overtakeMargin = profile != null ? profile.OvertakeSpeedMargin(frustration) : overtakeSpeedMargin;
        // Always sense at least far enough to brake from top speed and keep the min gap, so cars
        // never "discover" a hazard too late to stop (this is what caused the pile-ups).
        float brakeDist = (maxSpeed * maxSpeed) / (2f * Mathf.Max(1f, braking));
        float effSensorLength = Mathf.Max(sensorLength * sensorMul, brakeDist + minFollowGap * gapMul + 3f);

        Vector3 toTarget = currentTarget.transform.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;

        // Smoothly rotate toward the target (via physics-friendly MoveRotation).
        if (toTarget.sqrMagnitude > 0.001f)
        {
            Quaternion desired = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, desired, turnSpeed * dt));
        }

        // Desired open-road speed. Ease off for the upcoming corner only as it nears, so cars
        // cruise the street and merely slow through the bend instead of crawling the whole way.
        float cruiseSpeed = maxSpeed * desiredFactor;
        if (distance < 7f) cruiseSpeed = Mathf.Min(cruiseSpeed, CornerSpeedLimit());
        float rawTargetSpeed = cruiseSpeed;

        // Reaction time eats into every gap: a slower-to-react driver behaves as if hazards are
        // closer than they are. Perfect drivers have zero lag.
        float lag = reactionTime * Mathf.Max(0f, currentSpeed);

        // 1) Traffic light / stop line at the current target: brake early enough to stop AT it.
        //    Skipped in reservation (Perfect) mode, where junctions are managed by reservation.
        bool reservations = TrafficSimulationManager.ReservationsEnabled;
        TrafficLight light = currentTarget.controllingLight;
        bool mustStopAtTarget = false;
        if (!reservations && light != null && light.RequiresStop(distance, currentTarget.stopLineDistance * 0.5f))
            mustStopAtTarget = true;

        // 2) Give-way node (roundabout / T-junction entry): hold if the conflict zone isn't clear.
        if (currentTarget.isYield && distance <= currentTarget.stopLineDistance + 1f &&
            !IsYieldZoneClear(currentTarget))
            mustStopAtTarget = true;

        // 3) Don't block the junction: if the light lets us through but the space just beyond the
        //    junction is occupied, wait behind the line instead of stalling in the middle.
        if (!reservations && light != null && light.CanPass && !mustStopAtTarget)
        {
            Waypoint after = PeekAfterCurrent();
            if (after != null && distance < currentTarget.stopLineDistance + 3f &&
                !IsSpotClear(after.transform.position, 2f, out _))
                mustStopAtTarget = true;
        }

        if (mustStopAtTarget)
            rawTargetSpeed = Mathf.Min(rawTargetSpeed,
                SafeSpeed(distance - currentTarget.stopLineDistance - lag, 0f, gapMul));

        // 4) Car ahead: never carry more speed than we can shed before hitting it.
        if (SenseCarAhead(effSensorLength, out float hitDistance, out CarAI carAhead))
        {
            float leadSpeed = carAhead != null ? carAhead.CurrentSpeed : 0f;
            rawTargetSpeed = Mathf.Min(rawTargetSpeed, SafeSpeed(hitDistance - lag, leadSpeed, gapMul));

            // The car ahead is meaningfully SLOWER than I want to drive -> consider overtaking.
            bool aheadIsSlower = carAhead != null && carAhead.CurrentSpeed < cruiseSpeed - overtakeMargin;
            if (enableLaneChanges && aheadIsSlower &&
                Time.time - lastLaneChangeTime > laneChangeCooldown)
            {
                if (TryOvertake(carAhead.CurrentSpeed))
                    lastLaneChangeTime = Time.time;
            }
        }

        // 5) Intersection reservation (Perfect / no-lights mode): claim a junction before entering
        //    so cross-traffic never collides. Replaces traffic lights when enabled.
        if (TrafficSimulationManager.ReservationsEnabled)
            ApplyReservation(ref rawTargetSpeed, gapMul);

        rawTargetSpeed = Mathf.Max(0f, rawTargetSpeed);

        float rate = (rawTargetSpeed > currentSpeed) ? acceleration : braking;
        rate *= currentThrottleMul;
        currentSpeed = Mathf.MoveTowards(currentSpeed, rawTargetSpeed, rate * dt);

        ApplyDriveForces();

        // Mood: frustration builds while held up, decays while flowing.
        bool blocked = IsStopped || rawTargetSpeed < cruiseSpeed - 0.5f;
        if (profile != null) frustration = profile.UpdateFrustration(frustration, blocked, dt);

        // Waypoint arrival.
        if (distance <= arriveDistance)
        {
            Waypoint next = NextWaypoint();
            if (next == null) { Despawn(); return; }
            currentTarget = next;
        }

        UpdateBodyColor();
    }

    /// <summary>
    /// Lazily roll per-car values from the profile the first time it is present (the spawner
    /// assigns the profile after Instantiate, so we can't do this in Awake).
    /// </summary>
    private void EnsureProfileInit()
    {
        if (profileInitialized || profile == null) return;
        profileInitialized = true;
        reactionTime = profile.RollReactionTime();
    }

    /// <summary>
    /// Next node on the route (GPS mode), or a random successor when no route is assigned
    /// (keeps the ring / highway sandbox scenes working).
    /// </summary>
    private Waypoint NextWaypoint()
    {
        if (route != null && route.Count > 0)
        {
            // Advance the cursor to the node just reached, then hand back the following node.
            while (routeIndex < route.Count && route[routeIndex] != currentTarget) routeIndex++;
            int nextIndex = routeIndex + 1;
            if (nextIndex < route.Count) { routeIndex = nextIndex; return route[nextIndex]; }
            return null; // reached the destination
        }
        return currentTarget.GetRandomNext();
    }

    /// <summary>
    /// Max speed we should carry given how sharply the path bends at the current target.
    /// Straight ahead = no limit; a 90-degree turn = cornerSpeed; a U-turn = slower still.
    /// </summary>
    private float CornerSpeedLimit()
    {
        Waypoint after = PeekAfterCurrent();
        if (after == null) return float.MaxValue;

        Vector3 incoming = currentTarget.transform.position - transform.position;
        Vector3 outgoing = after.transform.position - currentTarget.transform.position;
        incoming.y = 0f; outgoing.y = 0f;
        if (incoming.sqrMagnitude < 0.001f || outgoing.sqrMagnitude < 0.001f) return float.MaxValue;

        float turn = Vector3.Angle(incoming, outgoing); // 0 = straight, 180 = U-turn
        if (turn < 15f) return float.MaxValue;          // basically straight
        // 90 deg -> cornerSpeed; scales down toward a U-turn, up toward a straight.
        return cornerSpeed * Mathf.Clamp(90f / turn, 0.5f, 3f);
    }

    /// <summary>The node the car will drive to after the current target (route-aware).</summary>
    private Waypoint PeekAfterCurrent()
    {
        if (route != null && route.Count > 0)
        {
            for (int i = 0; i < route.Count - 1; i++)
                if (route[i] == currentTarget) return route[i + 1];
            return null;
        }
        return currentTarget != null ? currentTarget.GetRandomNext() : null;
    }

    /// <summary>
    /// Fastest speed from which we can still slow to <paramref name="leadSpeed"/> before closing
    /// <paramref name="gap"/> (minus the driver's minimum following gap). This is the core of the
    /// crash-avoidance: a car never travels faster than it can brake within the space ahead.
    /// </summary>
    private float SafeSpeed(float gap, float leadSpeed, float gapMul)
    {
        float effGap = gap - minFollowGap * gapMul;
        if (effGap <= 0f) return Mathf.Max(0f, leadSpeed); // at/inside the gap: match the lead (0 -> stop)
        // Plan for slightly weaker braking than the car can actually manage, so the physics
        // controller always keeps up and never overshoots into the car/line ahead.
        float decel = Mathf.Max(1f, braking) * 0.7f;
        return Mathf.Sqrt(Mathf.Max(0f, leadSpeed * leadSpeed) + 2f * decel * effGap);
    }

    // ---- Intersection reservation (Perfect / no-lights mode) ----
    private TrafficSimulationManager.IntersectionZone claimedZone;
    private bool crossingCommitted; // true once I've entered a junction core this crossing

    private void ApplyReservation(ref float rawTargetSpeed, float gapMul)
    {
        Vector3 pos = transform.position;

        if (!ComputeCrossing(pos, out var z, out Vector3 entry, out Vector3 exit, out float distToEntry))
        {
            ReleaseZone();
            return;
        }

        // Register my trajectory so lower-priority crossing cars yield to me.
        if (claimedZone != null && claimedZone != z) TrafficSimulationManager.RemoveClaim(claimedZone, this);
        TrafficSimulationManager.SetClaim(z, this, entry, exit, distToEntry);
        claimedZone = z;

        // Before committing to the crossing, make sure I may go. Once committed I always proceed.
        if (!crossingCommitted)
        {
            // (a) yield to any CROSSING trajectory that outranks me (non-crossing cars pass together), and
            // (b) don't enter if my exit is JAMMED (a stopped car sitting in it), so I don't block the
            //     box. A merely-moving car at the exit will clear, so it doesn't hold me up.
            bool mustYield = TrafficSimulationManager.MustYield(z, this, entry, exit, distToEntry, GetInstanceID());
            bool exitJammed = ExitJammed(exit);
            if (mustYield || exitJammed)
            {
                // Stop before the CORE so a waiting car isn't mistaken for a committed one.
                float distToCore = Planar(z.center - pos) - z.coreRadius;
                rawTargetSpeed = Mathf.Min(rawTargetSpeed, SafeSpeed(distToCore - 0.5f, 0f, gapMul));
            }
        }
    }

    /// <summary>
    /// Work out the junction my path is crossing: the zone I'm physically inside (highest priority),
    /// else the nearest zone a node on my near path enters. entry/exit are the trajectory chord.
    /// </summary>
    private bool ComputeCrossing(Vector3 pos, out TrafficSimulationManager.IntersectionZone zone,
                                 out Vector3 entry, out Vector3 exit, out float distToEntry)
    {
        zone = null; entry = exit = Vector3.zero; distToEntry = 0f;

        // Once I touch the core I'm committed for the rest of this crossing (top priority).
        if (TrafficSimulationManager.ZoneCoreContaining(pos) != null) crossingCommitted = true;

        // Physically anywhere in the junction area: hold the claim until I've driven clear of it.
        var det = TrafficSimulationManager.ZoneContainingPoint(pos);
        if (det != null)
        {
            zone = det;
            entry = pos;
            exit = pos + transform.forward * 2f;
            for (int k = 1; k <= 3; k++)
            {
                Waypoint n = UpcomingNode(k);
                if (n == null) break;
                exit = n.transform.position;
                if (TrafficSimulationManager.ZoneContainingPoint(n.transform.position) != det) break;
            }
            // Committed cars have top priority; a car merely waiting at the edge does not.
            distToEntry = crossingCommitted ? 0f : Mathf.Max(0.1f, Planar(det.center - pos) - det.coreRadius);
            return true;
        }

        // Approaching: the first upcoming node inside a zone is my entry port; the next is my exit.
        for (int k = 0; k <= 3; k++)
        {
            Waypoint n = UpcomingNode(k);
            if (n == null) break;
            var z = TrafficSimulationManager.ZoneContainingPoint(n.transform.position);
            if (z == null) continue;

            float dToEntry = Planar(n.transform.position - pos);
            if (dToEntry > 16f) return false; // too far to reserve yet
            Waypoint ex = UpcomingNode(k + 1);
            zone = z;
            entry = n.transform.position;
            exit = ex != null ? ex.transform.position : entry + (entry - pos);
            distToEntry = dToEntry;
            return true;
        }

        crossingCommitted = false; // clear of all junctions
        return false;
    }

    /// <summary>k-th node ahead of the current target along the route (or nextWaypoints[0] chain).</summary>
    private Waypoint UpcomingNode(int k)
    {
        if (route != null && route.Count > 0)
        {
            if (routeIndex >= route.Count || route[routeIndex] != currentTarget)
            {
                int idx = route.IndexOf(currentTarget);
                if (idx >= 0) routeIndex = idx;
            }
            int t = routeIndex + k;
            return (t >= 0 && t < route.Count) ? route[t] : null;
        }
        Waypoint w = currentTarget;
        for (int i = 0; i < k && w != null; i++)
            w = (w.nextWaypoints != null && w.nextWaypoints.Count > 0) ? w.nextWaypoints[0] : null;
        return w;
    }

    /// <summary>True only if a STOPPED car is sitting at the exit point (a real jam, not flowing traffic).</summary>
    private bool ExitJammed(Vector3 exitPos)
    {
        Collider[] hits = Physics.OverlapSphere(exitPos, 1.8f, carLayerMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h == null) continue;
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
            var other = h.GetComponentInParent<CarAI>();
            if (other != null && other != this && other.IsStopped) return true;
        }
        return false;
    }

    private void ReleaseZone()
    {
        if (claimedZone != null) TrafficSimulationManager.RemoveClaim(claimedZone, this);
        claimedZone = null;
        crossingCommitted = false;
    }

    private static float Planar(Vector3 v) { v.y = 0f; return v.magnitude; }

    /// <summary>Assign a new driver profile at runtime and re-roll its per-car values.</summary>
    public void SetProfile(DriverProfile p)
    {
        profile = p;
        profileInitialized = false;
        frustration = 0f;
        ReleaseZone(); // reset reservation state (used when toggling Perfect mode)
    }

    /// <summary>True if no OTHER car is inside the yield waypoint's conflict zone.</summary>
    private bool IsYieldZoneClear(Waypoint yieldPoint)
    {
        Vector3 center = yieldPoint.transform.position;
        Collider[] hits = Physics.OverlapSphere(center, yieldCheckRadius, carLayerMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h == null) continue;
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
            if (h.GetComponentInParent<CarAI>() != null) return false;
        }
        return true;
    }

    /// <summary>
    /// Tints the body renderer from slowColor (0 speed) to fastColor (max speed).
    /// Uses a MaterialPropertyBlock so it doesn't create per-car material instances.
    /// </summary>
    private void UpdateBodyColor()
    {
        if (bodyRenderer == null || mpb == null) return;

        Color c;
        // A profile can force a flat archetype colour so drivers are told apart at a glance;
        // otherwise (or with no profile) we tint from slowColor -> fastColor by speed.
        if (profile != null && profile.useBodyColorTint)
        {
            c = profile.bodyColorTint;
        }
        else
        {
            if (!colorBySpeed) return;
            float t = maxSpeed > 0.01f ? Mathf.Clamp01(currentSpeed / maxSpeed) : 0f;
            c = Color.Lerp(slowColor, fastColor, t);
        }

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

        // Forward drive: P-controller on speed error. Throttle sharpness comes from the
        // driver's awareness/aggression (currentThrottleMul, 1 when no profile is set).
        float speedError = currentSpeed - forwardVel;
        rb.AddForce(forward * speedError * driveGain * currentThrottleMul, ForceMode.Acceleration);

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
        CarAI other = collision.rigidbody.GetComponent<CarAI>();
        if (other == null) return;

        // Count each collision once per pair (only the lower-id car reports it).
        if (GetInstanceID() < other.GetInstanceID())
            TrafficSimulationManager.ReportCollision();

        // Drop the AI's target speed to whatever the impact left us with, so the
        // driver 'goes with' the shove for a moment instead of instantly re-throttling.
        float forwardVelAfterImpact = Vector3.Dot(rb.linearVelocity, transform.forward);
        currentSpeed = Mathf.Clamp(forwardVelAfterImpact, 0f, maxSpeed);
    }

    private bool SenseCarAhead(float range, out float distance, out CarAI carAhead)
    {
        distance = 0f;
        carAhead = null;
        Vector3 origin = transform.position + transform.forward * 0.5f + Vector3.up * 0.3f;
        if (Physics.SphereCast(origin, sensorRadius, transform.forward, out RaycastHit hit,
                range, carLayerMask, QueryTriggerInteraction.Ignore))
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
                ReplanRoute(); // a lane change moves us off the planned path; re-route to the goal.
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Recompute the shortest path from the current target to the destination. Called after a
    /// lane-change so the car rejoins a valid route. No-op when the car has no destination
    /// (ring / highway sandbox scenes just keep random-walking).
    /// </summary>
    public void ReplanRoute()
    {
        if (destination == null || currentTarget == null) return;
        var newRoute = Pathfinder.FindPath(currentTarget, destination, null);
        if (newRoute != null && newRoute.Count > 0)
        {
            route = newRoute;
            routeIndex = 0;
        }
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
        if (SimulationGizmoSettings.ShowCarSensors)
        {
            Gizmos.color = Color.magenta;
            Vector3 origin = transform.position + transform.forward * 0.5f + Vector3.up * 0.3f;
            Gizmos.DrawLine(origin, origin + transform.forward * sensorLength);
            Gizmos.DrawWireSphere(origin + transform.forward * sensorLength, sensorRadius);
        }

        // GPS route: draw the remaining planned path to the destination.
        if (SimulationGizmoSettings.ShowRoutes && route != null && route.Count > 0)
        {
            Gizmos.color = new Color(0.2f, 0.6f, 1f);
            for (int i = Mathf.Max(0, routeIndex); i < route.Count - 1; i++)
            {
                if (route[i] == null || route[i + 1] == null) continue;
                Gizmos.DrawLine(route[i].transform.position + Vector3.up * 0.2f,
                                route[i + 1].transform.position + Vector3.up * 0.2f);
            }
            if (destination != null)
                Gizmos.DrawWireCube(destination.transform.position + Vector3.up * 0.3f, Vector3.one * 0.8f);
        }
    }
}
