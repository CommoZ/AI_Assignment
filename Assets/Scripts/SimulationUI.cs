using UnityEngine;

/// <summary>
/// Runtime IMGUI panel so you can tweak the simulation without extra UI setup:
///   - Pause / resume, time-scale slider
///   - Spawn interval slider (drives all CarSpawners in the scene)
///   - Live jam stats: car count, average speed, % stopped
///
/// Just add this component to any GameObject in the scene (e.g. the manager).
/// No prefabs / canvases required.
/// </summary>
public class SimulationUI : MonoBehaviour
{
    public Rect windowRect = new Rect(10, 10, 260, 210);

    private CarSpawner[] spawners;
    private float sharedSpawnInterval = 1.5f;

    private void Start()
    {
        spawners = FindObjectsByType<CarSpawner>(FindObjectsSortMode.None);
        if (spawners.Length > 0) sharedSpawnInterval = spawners[0].spawnInterval;
    }

    private void OnGUI()
    {
        windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Traffic Sim");
    }

    private void DrawWindow(int id)
    {
        var mgr = TrafficSimulationManager.Instance;

        GUILayout.BeginHorizontal();
        if (mgr != null)
        {
            if (GUILayout.Button(mgr.paused ? "Resume" : "Pause"))
                mgr.paused = !mgr.paused;
        }
        GUILayout.EndHorizontal();

        if (mgr != null)
        {
            GUILayout.Label($"Time scale: {mgr.timeScale:0.00}x");
            mgr.timeScale = GUILayout.HorizontalSlider(mgr.timeScale, 0f, 4f);

            GUILayout.Space(6);
            GUILayout.Label($"Cars: {mgr.CarCount} / {mgr.maxCars}");
            GUILayout.Label($"Avg speed: {mgr.AverageSpeed:0.00} m/s");
            GUILayout.Label($"Stopped: {mgr.StoppedFraction * 100f:0}%");
        }

        GUILayout.Space(6);
        GUILayout.Label($"Spawn interval: {sharedSpawnInterval:0.00}s");
        float newInterval = GUILayout.HorizontalSlider(sharedSpawnInterval, 0.1f, 5f);
        if (!Mathf.Approximately(newInterval, sharedSpawnInterval))
        {
            sharedSpawnInterval = newInterval;
            if (spawners != null)
                foreach (var s in spawners) if (s != null) s.SetSpawnInterval(newInterval);
        }

        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }
}
