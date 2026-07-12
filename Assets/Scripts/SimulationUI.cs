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
    public Rect windowRect = new Rect(10, 10, 300, 620);

    private CarSpawner[] spawners;
    private TrafficLightGroup[] signals;
    private TrafficLight[] lights;
    private float sharedSpawnInterval = 1.5f;
    private float greenTime = 6f;
    private bool perfectMode;

    private CityGridBuilder city;   // present only in the city scene
    private int gridRows = 4;
    private int gridCols = 4;

    private Texture2D pixel;

    private void Start()
    {
        spawners = FindObjectsByType<CarSpawner>(FindObjectsSortMode.None);
        if (spawners.Length > 0) sharedSpawnInterval = spawners[0].spawnInterval;

        signals = FindObjectsByType<TrafficLightGroup>(FindObjectsSortMode.None);
        lights = FindObjectsByType<TrafficLight>(FindObjectsSortMode.None);
        if (signals.Length > 0) greenTime = signals[0].greenTime;

        var cities = FindObjectsByType<CityGridBuilder>(FindObjectsSortMode.None);
        city = cities.Length > 0 ? cities[0] : null;
        if (city != null) { gridRows = city.rows; gridCols = city.cols; }

        pixel = new Texture2D(1, 1);
        pixel.SetPixel(0, 0, Color.white);
        pixel.Apply();
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
            GUILayout.Label($"Avg frustration: {mgr.AverageFrustration * 100f:0}%");

            GUILayout.Space(6);
            GUILayout.Label($"Max cars: {mgr.maxCars}");
            mgr.maxCars = Mathf.RoundToInt(GUILayout.HorizontalSlider(mgr.maxCars, 0, 200));
            if (GUILayout.Button("Clear all cars")) TrafficSimulationManager.ClearAllCars();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Collisions: {TrafficSimulationManager.TotalCollisions}");
            if (GUILayout.Button("Reset", GUILayout.Width(60))) mgr.ResetCollisions();
            GUILayout.EndHorizontal();
            DrawCollisionGraph(mgr.CollisionHistory);
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

        GUILayout.Space(6);
        // Perfect mode: force every spawner's population to its "perfect driver" control
        // baseline (zero reaction lag, exact speeds, no mistakes). Great A/B against the mix.
        bool newPerfect = GUILayout.Toggle(perfectMode, " Perfect mode (perfect info, no lights)");
        if (newPerfect != perfectMode)
        {
            perfectMode = newPerfect;
            ApplyPerfectMode(perfectMode);
        }

        // Editor route visualisation toggle.
        SimulationGizmoSettings.ShowRoutes =
            GUILayout.Toggle(SimulationGizmoSettings.ShowRoutes, " Show GPS routes (gizmos)");

        // Traffic-signal green time (applies live to every signal group in the scene).
        if (signals != null && signals.Length > 0)
        {
            GUILayout.Space(6);
            GUILayout.Label($"Signal green time: {greenTime:0.0}s");
            float g = GUILayout.HorizontalSlider(greenTime, 2f, 15f);
            if (!Mathf.Approximately(g, greenTime))
            {
                greenTime = g;
                foreach (var s in signals) if (s != null) s.greenTime = g;
            }
        }

        // City grid rebuild (city scene only).
        if (city != null)
        {
            GUILayout.Space(6);
            GUILayout.Label($"City grid: {gridRows} x {gridCols}");
            gridRows = Mathf.RoundToInt(GUILayout.HorizontalSlider(gridRows, 2, 8));
            gridCols = Mathf.RoundToInt(GUILayout.HorizontalSlider(gridCols, 2, 8));
            if (GUILayout.Button("Rebuild city"))
                RebuildCity();
        }

        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }

    /// <summary>
    /// Rebuild the city grid at the chosen size at runtime: clear cars, regenerate the road
    /// network, drop the pathfinder's cached graph, and re-point spawners at the new intersections.
    /// </summary>
    private void RebuildCity()
    {
        if (city == null) return;
        TrafficSimulationManager.ClearAllCars();
        city.rows = gridRows;
        city.cols = gridCols;
        city.Rebuild();
        Pathfinder.InvalidateCache();

        var intersections = city.GetIntersections();
        if (spawners != null)
            foreach (var s in spawners)
                if (s != null) s.spawnPoints = intersections;

        // Redraw the visible road surface for the new grid.
        foreach (var rr in FindObjectsByType<RoadRenderer>(FindObjectsSortMode.None))
            if (rr != null) rr.Rebuild();
    }

    /// <summary>
    /// Flip every spawner's population between its normal weighted mix and the perfect-driver
    /// control baseline by toggling the population's globalOverride to perfectProfile.
    /// New cars spawned after this call use the chosen mode.
    /// </summary>
    private void ApplyPerfectMode(bool on)
    {
        // Perfect-info cars negotiate junctions by reservation, so traffic lights aren't needed.
        TrafficSimulationManager.ReservationsEnabled = on;
        TrafficSimulationManager.ClearZoneReservations();

        // Turn the signals off (and force them green) while in Perfect mode; restore them after.
        if (signals != null)
            foreach (var g in signals) if (g != null) g.enabled = !on;
        if (lights != null)
            foreach (var l in lights)
            {
                if (l == null) continue;
                l.manualControl = on;
                if (on) l.SetState(TrafficLight.State.Green);
            }

        DriverPopulation pop = null;
        if (spawners != null)
            foreach (var s in spawners)
            {
                if (s == null || s.population == null) continue;
                pop = s.population;
                s.population.globalOverride = on ? s.population.perfectProfile : null;
            }
        if (pop == null) return;

        // Perfect drivers shouldn't be recoloured; keep the speed colouring.
        if (pop.perfectProfile != null) pop.perfectProfile.useBodyColorTint = false;

        // Apply to cars already on the road, not just future spawns, so the effect is immediate.
        if (on && pop.perfectProfile != null) TrafficSimulationManager.ApplyProfileToAll(pop.perfectProfile);
        else if (!on) TrafficSimulationManager.ReassignFromPopulation(pop);
    }

    /// <summary>Draws a cumulative-collisions-over-time area chart.</summary>
    private void DrawCollisionGraph(System.Collections.Generic.IReadOnlyList<int> history)
    {
        Rect r = GUILayoutUtility.GetRect(1, 60, GUILayout.ExpandWidth(true));
        // Background.
        GUI.color = new Color(0f, 0f, 0f, 0.35f);
        GUI.DrawTexture(r, pixel);
        GUI.color = Color.white;

        if (history == null || history.Count == 0)
        {
            GUI.Label(r, " collisions over time");
            return;
        }

        int max = 1;
        for (int i = 0; i < history.Count; i++) max = Mathf.Max(max, history[i]);

        float barW = r.width / Mathf.Max(history.Count, 1);
        GUI.color = new Color(1f, 0.35f, 0.3f, 0.9f);
        for (int i = 0; i < history.Count; i++)
        {
            float h = (history[i] / (float)max) * r.height;
            GUI.DrawTexture(new Rect(r.x + i * barW, r.yMax - h, Mathf.Max(1f, barW), h), pixel);
        }
        GUI.color = Color.white;
        GUI.Label(new Rect(r.x + 2, r.y, r.width, 18), $" peak {max}");
    }
}
