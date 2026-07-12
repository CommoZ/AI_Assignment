using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor menu tools that build the test assets and scenes for the traffic sim, so everything is
/// wired correctly (manager, spawner, driver population, UI, cameras) without hand-editing scene
/// files. Menus live under "Traffic Sim/".
///
///  - Generate Driver Assets: writes five DriverProfile archetypes + a DriverPopulation to
///    Assets/DriverProfiles.
///  - Generate City Scene: a signalised grid demonstrating GPS routing (bidirectional A*).
///  - Generate Junctions Scene: a 4-way intersection and a roundabout for right-of-way / cornering.
/// </summary>
public static class TrafficDemoGenerator
{
    const string ProfilesFolder = "Assets/DriverProfiles";
    const string ScenesFolder = "Assets/Scenes";
    const string CarPrefabPath = "Assets/Prefabs/CarPrefab.prefab";

    [MenuItem("Traffic Sim/Generate Driver Assets")]
    public static void GenerateDriverAssetsMenu() => GenerateDriverAssets();

    public static DriverPopulation GenerateDriverAssets()
    {
        if (!AssetDatabase.IsValidFolder(ProfilesFolder))
            AssetDatabase.CreateFolder("Assets", "DriverProfiles");

        var cautious   = MakeProfile("Cautious",   0.20f, 0.80f, 0.90f, 0.4f, 0.9f, new Color(0.3f, 0.5f, 1f));
        var average    = MakeProfile("Average",    0.50f, 0.50f, 0.80f, 0.2f, 0.6f, Color.white, tint: false);
        var aggressive = MakeProfile("Aggressive", 0.85f, 0.20f, 0.70f, 0.15f, 0.35f, new Color(1f, 0.3f, 0.2f));
        var distracted = MakeProfile("Distracted", 0.40f, 0.50f, 0.35f, 0.6f, 1.2f, new Color(1f, 0.85f, 0.2f));
        var perfect    = MakePerfect();

        var pop = LoadOrCreate<DriverPopulation>($"{ProfilesFolder}/Mixed.asset");
        pop.entries = new List<DriverPopulation.Entry>
        {
            new DriverPopulation.Entry { profile = average,    weight = 0.5f },
            new DriverPopulation.Entry { profile = aggressive, weight = 0.2f },
            new DriverPopulation.Entry { profile = cautious,   weight = 0.2f },
            new DriverPopulation.Entry { profile = distracted, weight = 0.1f },
        };
        pop.perfectProfile = perfect;
        pop.globalOverride = null;
        EditorUtility.SetDirty(pop);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[TrafficDemoGenerator] Driver assets written to " + ProfilesFolder);
        return pop;
    }

    [MenuItem("Traffic Sim/Generate City Scene")]
    public static void GenerateCityScene()
    {
        var pop = GenerateDriverAssets();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        int rows = 4, cols = 4;
        float spacing = 25f;
        Vector3 center = new Vector3((cols - 1) * spacing * 0.5f, 0f, (rows - 1) * spacing * 0.5f);

        CreateGround(center, 40f);
        CreateCamera(center + new Vector3(0f, 90f, -55f), new Vector3(56f, 0f, 0f));
        CreateSun();
        CreateSystems(maxCars: 40);

        var cityGo = new GameObject("City");
        var city = cityGo.AddComponent<CityGridBuilder>();
        city.rows = rows; city.cols = cols; city.spacing = spacing;
        city.useTrafficLights = true;
        city.Rebuild();

        var spawner = CreateSpawner(pop, assignDestinations: true);
        spawner.spawnPoints = city.GetIntersections();
        spawner.spawnInterval = 1.4f;

        CreateRoadSurface();

        SaveScene(scene, $"{ScenesFolder}/City.unity");
        Debug.Log("[TrafficDemoGenerator] City scene generated. Press Play; toggle 'Show GPS routes' and select a car.");
    }

    [MenuItem("Traffic Sim/Generate Junctions Scene")]
    public static void GenerateJunctionsScene()
    {
        var pop = GenerateDriverAssets();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        CreateGround(new Vector3(15f, 0f, 0f), 12f);
        CreateCamera(new Vector3(15f, 50f, -42f), new Vector3(50f, 0f, 0f));
        CreateSun();
        CreateSystems(maxCars: 30);

        // A signalised 4-way at the origin.
        var interGo = new GameObject("Intersection_4Way");
        var inter = interGo.AddComponent<IntersectionBuilder>();
        inter.type = IntersectionBuilder.JunctionType.FourWay;
        inter.useTrafficLights = true;
        inter.Rebuild();

        // A roundabout a little to the east.
        var roundGo = new GameObject("Roundabout");
        roundGo.transform.position = new Vector3(30f, 0f, 0f);
        var round = roundGo.AddComponent<RoundaboutBuilder>();
        round.armCount = 4;
        round.Rebuild();

        // Spawn at every entry approach; random-walk through the junctions (no destinations).
        var spawnPoints = new List<Waypoint>();
        spawnPoints.AddRange(CollectEntries(interGo));
        spawnPoints.AddRange(CollectEntries(roundGo));

        var spawner = CreateSpawner(pop, assignDestinations: false);
        spawner.spawnPoints = spawnPoints;
        spawner.spawnInterval = 1.2f;

        CreateRoadSurface();

        SaveScene(scene, $"{ScenesFolder}/Junctions.unity");
        Debug.Log("[TrafficDemoGenerator] Junctions scene generated. Watch yielding at the roundabout and the signal cycle at the 4-way.");
    }

    // ---- asset helpers ----

    static DriverProfile MakeProfile(string name, float aggression, float patience, float awareness,
                                     float reactMin, float reactMax, Color tintColor, bool tint = true)
    {
        var p = LoadOrCreate<DriverProfile>($"{ProfilesFolder}/{name}.asset");
        p.displayName = name;
        p.isPerfect = false;
        p.aggression = aggression;
        p.patience = patience;
        p.awareness = awareness;
        p.reactionTimeMin = reactMin;
        p.reactionTimeMax = reactMax;
        p.useBodyColorTint = tint;
        p.bodyColorTint = tintColor;
        EditorUtility.SetDirty(p);
        return p;
    }

    static DriverProfile MakePerfect()
    {
        var p = LoadOrCreate<DriverProfile>($"{ProfilesFolder}/Perfect.asset");
        p.displayName = "Perfect";
        p.isPerfect = true;
        p.awareness = 1f;
        p.useBodyColorTint = false; // keep speed colouring; don't recolour perfect cars
        EditorUtility.SetDirty(p);
        return p;
    }

    static T LoadOrCreate<T>(string path) where T : ScriptableObject
    {
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (existing != null) return existing;
        var asset = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    // ---- scene object helpers ----

    static void CreateGround(Vector3 center, float scale)
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = new Vector3(center.x, 0f, center.z);
        ground.transform.localScale = Vector3.one * scale; // plane is 10 units, so *scale metres
    }

    static void CreateCamera(Vector3 pos, Vector3 euler)
    {
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.farClipPlane = 1000f;
        camGo.AddComponent<AudioListener>();
        camGo.AddComponent<CameraController>(); // WASD + right-drag look + scroll zoom
        camGo.transform.position = pos;
        camGo.transform.rotation = Quaternion.Euler(euler);
    }

    static void CreateSun()
    {
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
    }

    static void CreateSystems(int maxCars)
    {
        var go = new GameObject("Systems");
        var mgr = go.AddComponent<TrafficSimulationManager>();
        mgr.maxCars = maxCars;
        go.AddComponent<SimulationGizmoSettings>();
        go.AddComponent<SimulationUI>();
    }

    static void CreateRoadSurface()
    {
        var go = new GameObject("Roads");
        go.AddComponent<RoadRenderer>().Rebuild();
    }

    static CarSpawner CreateSpawner(DriverPopulation pop, bool assignDestinations)
    {
        var go = new GameObject("Spawner");
        var spawner = go.AddComponent<CarSpawner>();
        spawner.carPrefab = AssetDatabase.LoadAssetAtPath<CarAI>(CarPrefabPath);
        if (spawner.carPrefab == null)
            Debug.LogWarning($"[TrafficDemoGenerator] Car prefab not found at {CarPrefabPath}; assign one on the Spawner.");
        spawner.population = pop;
        spawner.assignDestinations = assignDestinations;
        return spawner;
    }

    static List<Waypoint> CollectEntries(GameObject root)
    {
        var list = new List<Waypoint>();
        foreach (var c in root.GetComponentsInChildren<RoadConnector>())
            if (c.kind == RoadConnector.Kind.Entry && c.node != null) list.Add(c.node);
        return list;
    }

    static void SaveScene(Scene scene, string path)
    {
        if (!AssetDatabase.IsValidFolder(ScenesFolder))
            AssetDatabase.CreateFolder("Assets", "Scenes");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, path);
    }
}
