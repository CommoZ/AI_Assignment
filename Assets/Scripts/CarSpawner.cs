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

    [Header("Driver population")]
    [Tooltip("Weighted mix of driver archetypes to draw from. Leave empty to spawn plain cars using the prefab's own values.")]
    public DriverPopulation population;

    [Header("GPS routing")]
    [Tooltip("If ticked, each car is given a random destination and drives a shortest path (bidirectional A*) to it, then despawns on arrival. Off = the car random-walks the graph (ring / highway scenes).")]
    public bool assignDestinations = false;

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

        // Decide the first hop: along a planned route if we're doing GPS, else a random successor.
        List<Waypoint> plannedRoute = null;
        Waypoint destination = null;
        if (assignDestinations)
        {
            destination = TrafficSimulationManager.RandomNodeExcept(sp);
            if (destination != null)
            {
                plannedRoute = Pathfinder.FindPath(sp, destination, null);
                if (plannedRoute == null || plannedRoute.Count < 2) return; // no route: skip this spawn
            }
        }

        Waypoint next = plannedRoute != null ? plannedRoute[1] : sp.GetRandomNext();

        Quaternion rot = sp.transform.rotation;
        if (next != null)
        {
            Vector3 dir = next.transform.position - pos;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f) rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        CarAI car = Instantiate(carPrefab, pos, rot);
        car.currentTarget = next != null ? next : sp;

        // Route / destination.
        if (plannedRoute != null)
        {
            car.route = plannedRoute;
            car.destination = destination;
        }

        // Driver personality: pick an archetype and roll this car's top speed from it.
        if (population != null)
        {
            DriverProfile p = population.PickWeighted();
            if (p != null)
            {
                car.profile = p;
                car.maxSpeed *= p.RollSpeedFactor();
            }
        }
    }

    /// <summary>Change spawn rate at runtime (used by UI).</summary>
    public void SetSpawnInterval(float seconds)
    {
        spawnInterval = Mathf.Max(0.05f, seconds);
    }
}
