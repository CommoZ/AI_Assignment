using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A driver population: a weighted mix of <see cref="DriverProfile"/> archetypes that a
/// <see cref="CarSpawner"/> draws from. Define the mix once as an asset and reuse it across
/// scenes, then vary the composition (e.g. 50% Average / 25% Aggressive / 20% Cautious /
/// 5% Distracted) to study how personality mix affects congestion.
///
/// <see cref="globalOverride"/> forces the whole population to a single archetype for a clean
/// control run without editing weights — set it to <see cref="perfectProfile"/> (via the
/// SimulationUI "Perfect mode" button) to run the perfect-driver baseline.
/// </summary>
[CreateAssetMenu(menuName = "Traffic/Driver Population", fileName = "DriverPopulation")]
public class DriverPopulation : ScriptableObject
{
    [System.Serializable]
    public struct Entry
    {
        public DriverProfile profile;
        [Min(0f)] public float weight;
    }

    [Tooltip("Weighted list of archetypes. Weights are relative (they don't need to sum to 1).")]
    public List<Entry> entries = new List<Entry>();

    [Tooltip("If set, PickWeighted always returns this profile, ignoring the weighted mix. Used for a control run.")]
    public DriverProfile globalOverride;

    [Tooltip("The 'perfect driver' archetype, referenced by the Perfect-mode toggle so the UI can flip the whole population to the control baseline.")]
    public DriverProfile perfectProfile;

    /// <summary>Pick an archetype: the override if set, otherwise a weighted-random entry.</summary>
    public DriverProfile PickWeighted() => PickWeighted(null);

    /// <summary>
    /// Weighted archetype pick. Pass a <see cref="System.Random"/> to draw from a reproducible
    /// stream (used by the spawner for deterministic demand); null uses UnityEngine.Random.
    /// </summary>
    public DriverProfile PickWeighted(System.Random rng)
    {
        if (globalOverride != null) return globalOverride;
        if (entries == null || entries.Count == 0) return null;

        float total = 0f;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].profile != null) total += Mathf.Max(0f, entries[i].weight);

        if (total <= 0f)
        {
            // No usable weights: fall back to the first assigned profile.
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].profile != null) return entries[i].profile;
            return null;
        }

        float roll = rng != null ? (float)rng.NextDouble() * total : Random.Range(0f, total);
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].profile == null) continue;
            roll -= Mathf.Max(0f, entries[i].weight);
            if (roll <= 0f) return entries[i].profile;
        }
        return entries[entries.Count - 1].profile;
    }
}
