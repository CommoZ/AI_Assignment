using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Singleton that keeps a registry of all live cars and exposes simple jam metrics
/// (average speed, count, percentage stopped). Also exposes global controls: pause,
/// time-scale multiplier, and max car cap.
/// </summary>
public class TrafficSimulationManager : MonoBehaviour
{
    public static TrafficSimulationManager Instance { get; private set; }

    [Header("Global limits")]
    public int maxCars = 60;

    [Header("Time controls")]
    [Range(0f, 4f)] public float timeScale = 1f;
    public bool paused;

    private static readonly List<CarAI> cars = new List<CarAI>();

    public int CarCount => cars.Count;

    public float AverageSpeed
    {
        get
        {
            if (cars.Count == 0) return 0f;
            float sum = 0f;
            foreach (var c in cars) if (c != null) sum += c.CurrentSpeed;
            return sum / cars.Count;
        }
    }

    /// <summary>Fraction of cars currently stopped (0..1). A rough jam indicator.</summary>
    public float StoppedFraction
    {
        get
        {
            if (cars.Count == 0) return 0f;
            int stopped = 0;
            foreach (var c in cars) if (c != null && c.IsStopped) stopped++;
            return (float)stopped / cars.Count;
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Update()
    {
        Time.timeScale = paused ? 0f : timeScale;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Time.timeScale = 1f;
    }

    public static void Register(CarAI c)
    {
        if (c != null && !cars.Contains(c)) cars.Add(c);
    }

    public static void Unregister(CarAI c)
    {
        cars.Remove(c);
    }
}
