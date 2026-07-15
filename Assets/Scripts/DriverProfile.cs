using UnityEngine;

/// <summary>
/// A driver personality archetype. This is a ScriptableObject asset, so a handful of
/// archetypes (Cautious / Average / Aggressive / Distracted / Perfect) can be authored once
/// and shared by every car that rolls that archetype. All the "how does this trait change the
/// way the car drives" math lives here as pure accessor methods, so <see cref="CarAI"/> stays a
/// thin consumer and swapping archetypes never touches driving code.
///
/// Traits (iteration 2):
///  - Reaction time : delay between a hazard appearing and the driver responding.
///  - Aggression    : follow distance, throttle eagerness, over-speeding, overtaking keenness.
///  - Patience       : how fast frustration builds while stuck.
///  - Awareness/skill: sensor range + control smoothness (low awareness = late, jerky reactions).
///
/// Mood is dynamic: a per-car <c>frustration</c> value (0..1, tracked on the car) rises while
/// blocked and decays while flowing, and is fed into the accessors below to raise the driver's
/// *effective* aggression.
///
/// <see cref="isPerfect"/> flips the archetype into the experimental control baseline: zero
/// reaction delay, full awareness, exact target speeds, optimal gaps, and no frustration.
/// </summary>
[CreateAssetMenu(menuName = "Traffic/Driver Profile", fileName = "DriverProfile")]
public class DriverProfile : ScriptableObject
{
    [Header("Identity")]
    public string displayName = "Average";
    [Tooltip("If ticked, cars using this profile are painted bodyColorTint instead of coloured by speed. Handy to tell archetypes apart at a glance.")]
    public bool useBodyColorTint = false;
    public Color bodyColorTint = Color.white;

    [Header("Perfect driver (control baseline)")]
    [Tooltip("If true: zero reaction delay, full awareness, exact target speeds, optimal gaps, no frustration. Use as the 'perfect knowledge, no mistakes' control group.")]
    public bool isPerfect = false;

    [Header("Reaction time (seconds)")]
    [Min(0f)] public float reactionTimeMin = 0.2f;
    [Min(0f)] public float reactionTimeMax = 0.6f;

    [Header("Personality (0..1)")]
    [Range(0f, 1f)] public float aggression = 0.5f;
    [Range(0f, 1f)] public float patience = 0.5f;
    [Range(0f, 1f)] public float awareness = 0.8f;

    [Header("Speed spread (x base max speed)")]
    [Tooltip("Each car rolls its top speed as a random factor in this range times the prefab's base maxSpeed.")]
    public float speedMinFactor = 0.9f;
    public float speedMaxFactor = 1.1f;

    [Header("Mood / frustration")]
    [Tooltip("Frustration gained per second while blocked (before patience scaling).")]
    public float frustrationRiseRate = 0.25f;
    [Tooltip("Frustration lost per second while flowing.")]
    public float frustrationDecayRate = 0.4f;
    [Range(0f, 1f)]
    [Tooltip("How much full (1.0) frustration adds to effective aggression.")]
    public float maxFrustrationAggressionBonus = 0.5f;

    // ---- Tuning constants: how personality maps to behaviour ----
    const float CautiousGap  = 1.7f;   // follow-gap multiplier at aggression 0 (keeps big gaps)
    const float TightGap     = 0.55f;  // follow-gap multiplier at aggression 1 (tailgates)
    const float PerfectGap   = 1.0f;   // tuned safe-but-efficient gap
    const float MaxOverspeed = 0.15f;  // aggression 1 targets +15% over its own max speed

    /// <summary>Aggression after the frustration bonus, clamped 0..1.</summary>
    public float EffectiveAggression(float frustration)
    {
        if (isPerfect) return 0.5f; // perfect uses exact rules elsewhere; this is only a fallback
        return Mathf.Clamp01(aggression + frustration * maxFrustrationAggressionBonus);
    }

    /// <summary>Per-car reaction delay (s). Low awareness stretches it; perfect drivers = 0.</summary>
    public float RollReactionTime()
    {
        if (isPerfect) return 0f;
        float t = Random.Range(reactionTimeMin, Mathf.Max(reactionTimeMin, reactionTimeMax));
        return t * Mathf.Lerp(1.6f, 1f, awareness);
    }

    /// <summary>Per-car top-speed factor (x base max speed).</summary>
    public float RollSpeedFactor()
    {
        return Random.Range(speedMinFactor, Mathf.Max(speedMinFactor, speedMaxFactor));
    }

    /// <summary>Per-car top-speed factor drawn from a caller-supplied RNG (for reproducible demand).</summary>
    public float RollSpeedFactor(System.Random rng)
    {
        float hi = Mathf.Max(speedMinFactor, speedMaxFactor);
        return speedMinFactor + (float)rng.NextDouble() * (hi - speedMinFactor);
    }

    /// <summary>Multiplier on the base following distance. &lt;1 tailgates, &gt;1 keeps a big gap.</summary>
    public float FollowGapMultiplier(float frustration)
    {
        if (isPerfect) return PerfectGap;
        return Mathf.Lerp(CautiousGap, TightGap, EffectiveAggression(frustration));
    }

    /// <summary>Multiplier on the car's own max speed for its desired cruise speed.</summary>
    public float DesiredSpeedFactor(float frustration)
    {
        if (isPerfect) return 1f;
        return 1f + MaxOverspeed * EffectiveAggression(frustration);
    }

    /// <summary>Multiplier on throttle/brake responsiveness. Low awareness = weaker/jerkier control.</summary>
    public float ThrottleResponse(float frustration)
    {
        if (isPerfect) return 1f;
        float aggr  = Mathf.Lerp(0.75f, 1.3f, EffectiveAggression(frustration));
        float skill = Mathf.Lerp(0.7f, 1f, awareness);
        return aggr * skill;
    }

    /// <summary>Multiplier on sensor range from awareness. Perfect/fully-aware = full range.</summary>
    public float SensorRangeMultiplier()
    {
        if (isPerfect) return 1f;
        return Mathf.Lerp(0.55f, 1f, awareness);
    }

    /// <summary>How much slower (m/s) a car ahead must be before this driver wants to overtake. Lower = keener.</summary>
    public float OvertakeSpeedMargin(float frustration)
    {
        if (isPerfect) return 1.5f;
        return Mathf.Lerp(3.5f, 0.4f, EffectiveAggression(frustration));
    }

    /// <summary>Advance a car's frustration given whether it is currently blocked/stopped.</summary>
    public float UpdateFrustration(float current, bool blocked, float dt)
    {
        if (isPerfect) return 0f;
        if (blocked) current += frustrationRiseRate * Mathf.Lerp(1.5f, 0.4f, patience) * dt;
        else         current -= frustrationDecayRate * dt;
        return Mathf.Clamp01(current);
    }
}
