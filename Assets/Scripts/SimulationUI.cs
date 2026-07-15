using UnityEngine;

/// <summary>
/// Runtime IMGUI panel to drive the simulation without any canvas/prefab setup. Groups:
///   - Time: pause/resume, time-scale.
///   - Live metrics: car count, avg speed, % stopped, frustration, throughput, trip time, collisions
///     + time-series graphs, CSV export.
///   - Scenario: seed + restart, demand pattern (Uniform / Hotspot / Commuter) + commuter phase.
///   - Environment: road grip (weather), global speed limit, rush-hour demand multiplier.
///   - Routing: congestion-aware routing toggle + weight.
///   - Incidents: break down a random car / clear all.
///   - Signals / city rebuild / Perfect mode.
/// Plus a second "Selected Car" inspector window (left-click a car): origin/destination, live +
/// editable speeds, break down/repair, and left-click a road to re-route it.
///
/// Add this component to any GameObject in the scene (e.g. the manager).
/// </summary>
public class SimulationUI : MonoBehaviour
{
    [Tooltip("Preferred panel height; the window shrinks to fit the viewport and scrolls instead.")]
    public float desiredHeight = 720f;
    public Rect windowRect = new Rect(10, 10, 320, 720);

    // Car inspector window (right side). Rect exposed so the selection controller can ignore clicks
    // that land on it; InspectorVisible tells it whether the window is currently shown.
    public Rect inspectorRect = new Rect(340, 10, 260, 300);
    public bool InspectorVisible => selection != null && selection.Selected != null;
    private CarSelectionController selection;

    private CarSpawner[] spawners;
    private TrafficLightGroup[] signals;
    private TrafficLight[] lights;
    private float sharedSpawnInterval = 1.5f;
    private float greenTime = 6f;
    private bool perfectMode;

    private CityGridBuilder city;   // present only in the city scene
    private int gridRows = 4;
    private int gridCols = 4;

    // Scenario / environment mirrors of the manager's live values.
    private string seedText = "12345";
    private int demandPatternIndex;           // 0 Uniform, 1 Hotspot, 2 Commuter
    private static readonly string[] PatternNames = { "Uniform", "Hotspot", "Commuter" };

    private Vector2 scroll;
    private Texture2D pixel;

    private void Start()
    {
        spawners = FindObjectsByType<CarSpawner>(FindObjectsSortMode.None);
        if (spawners.Length > 0)
        {
            sharedSpawnInterval = spawners[0].spawnInterval;
            demandPatternIndex = (int)spawners[0].demandPattern;
        }

        signals = FindObjectsByType<TrafficLightGroup>(FindObjectsSortMode.None);
        lights = FindObjectsByType<TrafficLight>(FindObjectsSortMode.None);
        if (signals.Length > 0) greenTime = signals[0].greenTime;

        var cities = FindObjectsByType<CityGridBuilder>(FindObjectsSortMode.None);
        city = cities.Length > 0 ? cities[0] : null;
        if (city != null) { gridRows = city.rows; gridCols = city.cols; }

        var mgr = TrafficSimulationManager.Instance;
        if (mgr != null) seedText = mgr.randomSeed.ToString();

        // Click-to-select + click-to-route works via a runtime-added controller (no scene regen).
        selection = FindFirstObjectByType<CarSelectionController>();
        if (selection == null) selection = gameObject.AddComponent<CarSelectionController>();

        // Scene-switch shortcuts (F1-F4 scenes, F5 reset) via a runtime-added component.
        if (FindFirstObjectByType<SceneSwitcher>() == null) gameObject.AddComponent<SceneSwitcher>();

        pixel = new Texture2D(1, 1);
        pixel.SetPixel(0, 0, Color.white);
        pixel.Apply();
    }

    private void OnGUI()
    {
        // Never let the window outgrow the viewport — the internal scroll view absorbs the
        // overflow instead. Re-clamped every frame so game-view resizes are handled live.
        windowRect.height = Mathf.Min(desiredHeight, Screen.height - 20f);
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, Screen.width - 40f);
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, Screen.height - 30f);

        windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Traffic Sim");

        // Car inspector (only while a car is selected).
        if (InspectorVisible)
        {
            inspectorRect.x = Mathf.Clamp(inspectorRect.x, 0f, Screen.width - 60f);
            inspectorRect.y = Mathf.Clamp(inspectorRect.y, 0f, Screen.height - 30f);
            inspectorRect = GUILayout.Window(GetInstanceID() + 1, inspectorRect, DrawInspector, "Selected Car");
        }
    }

    private void DrawWindow(int id)
    {
        var mgr = TrafficSimulationManager.Instance;
        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(windowRect.height - 28));

        GUILayout.Label("Scenes: F1 City · F2 Junctions · F3 Highway · F4 Ring · F5 reset");

        // ---- Time ----
        if (mgr != null)
        {
            if (GUILayout.Button(mgr.paused ? "Resume" : "Pause"))
                mgr.paused = !mgr.paused;
            GUILayout.Label($"Time scale: {mgr.timeScale:0.00}x");
            mgr.timeScale = GUILayout.HorizontalSlider(mgr.timeScale, 0f, 4f);
        }

        // ---- Live metrics ----
        if (mgr != null)
        {
            GUILayout.Space(4);
            GUILayout.Label($"Cars: {mgr.CarCount} / {mgr.maxCars}");
            GUILayout.Label($"Avg speed: {mgr.AverageSpeed:0.00} m/s");
            GUILayout.Label($"Stopped: {mgr.StoppedFraction * 100f:0}%   Frustration: {mgr.AverageFrustration * 100f:0}%");
            GUILayout.Label($"Throughput: {mgr.ThroughputPerMin:0} cars/min");
            GUILayout.Label($"Avg trip time: {mgr.AverageTripTime:0.0}s   Completed: {mgr.CompletedTrips}");

            GUILayout.Space(4);
            GUILayout.Label($"Max cars: {mgr.maxCars}");
            mgr.maxCars = Mathf.RoundToInt(GUILayout.HorizontalSlider(mgr.maxCars, 0, 200));
            if (GUILayout.Button("Clear all cars")) TrafficSimulationManager.ClearAllCars();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Collisions: {TrafficSimulationManager.TotalCollisions}");
            if (GUILayout.Button("Reset", GUILayout.Width(55))) mgr.ResetCollisions();
            if (GUILayout.Button("Export CSV", GUILayout.Width(90))) mgr.ExportCsv();
            GUILayout.EndHorizontal();

            var samples = mgr.Samples;
            DrawGraph(samples, s => s.collisions, new Color(1f, 0.35f, 0.3f, 0.9f), "collisions", "0");
            DrawGraph(samples, s => s.avgSpeed, new Color(0.4f, 0.8f, 1f, 0.9f), "avg speed m/s", "0.0");
            DrawGraph(samples, s => s.throughput, new Color(0.5f, 1f, 0.5f, 0.9f), "throughput /min", "0");
        }

        // ---- Scenario ----
        GUILayout.Space(6);
        GUILayout.Label("— Scenario —");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Seed:", GUILayout.Width(40));
        seedText = GUILayout.TextField(seedText, GUILayout.Width(90));
        if (GUILayout.Button("Restart scenario"))
        {
            if (mgr != null)
            {
                if (int.TryParse(seedText, out int seed)) mgr.randomSeed = seed;
                mgr.RestartScenario();
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("Demand pattern:");
        int newPattern = GUILayout.SelectionGrid(demandPatternIndex, PatternNames, 3);
        if (newPattern != demandPatternIndex)
        {
            demandPatternIndex = newPattern;
            if (spawners != null)
                foreach (var s in spawners) if (s != null) s.demandPattern = (DemandPattern)newPattern;
        }
        if (demandPatternIndex == (int)DemandPattern.Commuter && spawners != null && spawners.Length > 0)
        {
            bool inbound = spawners[0].inboundPhase;
            if (GUILayout.Button(inbound ? "Phase: morning inbound →" : "Phase: evening outbound ←"))
                foreach (var s in spawners) if (s != null) s.inboundPhase = !inbound;
        }

        // ---- Environment ----
        GUILayout.Space(6);
        GUILayout.Label("— Environment —");
        GUILayout.Label($"Road grip (weather): {TrafficSimulationManager.RoadGrip:0.00}");
        TrafficSimulationManager.RoadGrip = GUILayout.HorizontalSlider(TrafficSimulationManager.RoadGrip, 0.5f, 1f);

        float limit = TrafficSimulationManager.GlobalSpeedLimit;
        GUILayout.Label(limit > 0f ? $"Speed limit: {limit:0.0} m/s" : "Speed limit: off");
        limit = GUILayout.HorizontalSlider(limit, 0f, 25f);
        TrafficSimulationManager.GlobalSpeedLimit = limit < 0.5f ? 0f : limit; // snap low end to "off"

        GUILayout.Label($"Rush-hour demand: {TrafficSimulationManager.DemandMultiplier:0.00}x");
        TrafficSimulationManager.DemandMultiplier =
            GUILayout.HorizontalSlider(TrafficSimulationManager.DemandMultiplier, 0.25f, 4f);

        // ---- Routing ----
        GUILayout.Space(6);
        GUILayout.Label("— Routing —");
        TrafficSimulationManager.CongestionAwareRouting = GUILayout.Toggle(
            TrafficSimulationManager.CongestionAwareRouting, " Congestion-aware routing (avoid jams)");
        GUILayout.Label($"Congestion weight: {TrafficSimulationManager.CongestionWeight:0.0}");
        TrafficSimulationManager.CongestionWeight =
            GUILayout.HorizontalSlider(TrafficSimulationManager.CongestionWeight, 0f, 6f);

        // ---- Incidents ----
        GUILayout.Space(6);
        GUILayout.Label($"— Incidents —  ({TrafficSimulationManager.StalledCount} broken down)");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Break down random car")) TrafficSimulationManager.StallRandomCar();
        if (GUILayout.Button("Clear all", GUILayout.Width(80))) TrafficSimulationManager.ClearAllStalls();
        GUILayout.EndHorizontal();
        GUILayout.Label("Left-click a car to select it.");

        // ---- Spawn / Perfect / signals / city ----
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
        bool newPerfect = GUILayout.Toggle(perfectMode, " Perfect mode (perfect info, no lights)");
        if (newPerfect != perfectMode)
        {
            perfectMode = newPerfect;
            ApplyPerfectMode(perfectMode);
        }

        SimulationGizmoSettings.ShowRoutes =
            GUILayout.Toggle(SimulationGizmoSettings.ShowRoutes, " Show GPS routes (gizmos)");

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

        if (city != null)
        {
            GUILayout.Space(6);
            GUILayout.Label($"City grid: {gridRows} x {gridCols}");
            gridRows = Mathf.RoundToInt(GUILayout.HorizontalSlider(gridRows, 2, 8));
            gridCols = Mathf.RoundToInt(GUILayout.HorizontalSlider(gridCols, 2, 8));
            if (GUILayout.Button("Rebuild city"))
                RebuildCity();
        }

        GUILayout.EndScrollView();
        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }

    /// <summary>Inspector for the selected car: origin/destination, live speeds, breakdown, deselect.</summary>
    private void DrawInspector(int id)
    {
        CarAI car = selection != null ? selection.Selected : null;
        if (car == null) { GUI.DragWindow(new Rect(0, 0, 10000, 20)); return; }

        string profileName = car.profile != null ? car.profile.displayName : "(default)";
        GUILayout.Label($"Driver: {profileName}");
        GUILayout.Label($"Origin: {(car.originNode != null ? car.originNode.name : "?")}");
        GUILayout.Label($"Destination: {(car.destination != null ? car.destination.name : "random walk")}");
        GUILayout.Label($"Trip time: {car.TripTime:0.0}s");

        GUILayout.Space(4);
        GUILayout.Label($"Speed: {car.CurrentSpeed:0.0} / {car.maxSpeed:0.0} m/s");
        float sp = GUILayout.HorizontalSlider(car.CurrentSpeed, 0f, car.maxSpeed);
        if (!Mathf.Approximately(sp, car.CurrentSpeed)) car.SetCurrentSpeed(sp);

        GUILayout.Label($"Max speed: {car.maxSpeed:0.0} m/s");
        car.maxSpeed = GUILayout.HorizontalSlider(car.maxSpeed, 2f, 20f);

        GUILayout.Space(6);
        GUILayout.Label("Left-click a road to set destination.");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(car.Stalled ? "Repair" : "Break down")) car.ToggleStall();
        if (GUILayout.Button("Deselect")) selection.Deselect();
        GUILayout.EndHorizontal();

        if (GUILayout.Button("Delete car"))
        {
            selection.Deselect();   // clears the flag on the live car before it's destroyed
            car.Despawn();          // not an arrival — just removed
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
                if (s != null)
                {
                    s.spawnPoints = intersections;
                    s.RefreshDemandCache(); // spawnPoints changed -> recompute demand geometry
                }

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

    /// <summary>Draws a metric time-series as a bar chart from the manager's samples.</summary>
    private void DrawGraph(System.Collections.Generic.IReadOnlyList<TrafficSimulationManager.Sample> samples,
                           System.Func<TrafficSimulationManager.Sample, float> selector,
                           Color color, string label, string peakFmt)
    {
        Rect r = GUILayoutUtility.GetRect(1, 36, GUILayout.ExpandWidth(true));
        GUI.color = new Color(0f, 0f, 0f, 0.35f);
        GUI.DrawTexture(r, pixel);
        GUI.color = Color.white;

        if (samples == null || samples.Count == 0)
        {
            GUI.Label(r, $" {label}");
            return;
        }

        float max = 0.0001f;
        for (int i = 0; i < samples.Count; i++) max = Mathf.Max(max, selector(samples[i]));

        float barW = r.width / Mathf.Max(samples.Count, 1);
        GUI.color = color;
        for (int i = 0; i < samples.Count; i++)
        {
            float h = (selector(samples[i]) / max) * r.height;
            GUI.DrawTexture(new Rect(r.x + i * barW, r.yMax - h, Mathf.Max(1f, barW), h), pixel);
        }
        GUI.color = Color.white;
        GUI.Label(new Rect(r.x + 2, r.y, r.width, 18), $" {label}  (peak {max.ToString(peakFmt)})");
    }
}
