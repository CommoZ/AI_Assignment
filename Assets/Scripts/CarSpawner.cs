using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns car prefabs at one of several spawn waypoints at a configurable rate.
/// A global cap (from <see cref="TrafficSimulationManager"/>) prevents runaway spawning.
/// </summary>
public class CarSpawner : MonoBehaviour
{
    [Tooltip("Prefab that has a CarAI component on it.")]
    public CarAI carPrefab;

    [Tooltip("Possible starting waypoints. A random one is chosen for each spawn.")]
    public List<Waypoint> spawnPoints = new List<Waypoint>();

    [Tooltip("Seconds between spawn attempts.")]
    [Min(0.05f)] public float spawnInterval = 1.5f;

    [Tooltip("Minimum clear distance around a spawn point before spawning there.")]
    public float minClearRadius = 2f;

    private float timer;

    private void Update()
    {
        if (carPrefab == null || spawnPoints.Count == 0) return;

        timer += Time.deltaTime;
        if (timer < spawnInterval) return;
        timer = 0f;

        var mgr = TrafficSimulationManager.Instance;
        if (mgr != null && mgr.CarCount >= mgr.maxCars) return;

        // Try a few random spawn points until we find one whose vicinity has no other car.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Waypoint sp = spawnPoints[Random.Range(0, spawnPoints.Count)];
            if (sp == null) continue;
            if (IsBlockedByCar(sp.transform.position)) continue;

            SpawnAt(sp);
            return;
        }
    }

    private bool IsBlockedByCar(Vector3 position)
    {
        Collider[] hits = Physics.OverlapSphere(position, minClearRadius, ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            if (h == null) continue;
            if (h.GetComponentInParent<CarAI>() != null) return true;
        }
        return false;
    }

    private void SpawnAt(Waypoint sp)
    {
        Vector3 pos = sp.transform.position;
        Waypoint next = sp.GetRandomNext();
        Quaternion rot = sp.transform.rotation;
        if (next != null)
        {
            Vector3 dir = next.transform.position - pos;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f) rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        CarAI car = Instantiate(carPrefab, pos, rot);
        car.currentTarget = next != null ? next : sp;
    }

    /// <summary>Change spawn rate at runtime (used by UI).</summary>
    public void SetSpawnInterval(float seconds)
    {
        spawnInterval = Mathf.Max(0.05f, seconds);
    }
}
