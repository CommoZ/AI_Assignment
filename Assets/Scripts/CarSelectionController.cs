using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Left-click a car to <b>select</b> it (drives the inspector panel in <see cref="SimulationUI"/>);
/// with a car selected, left-click anywhere on the map to <b>re-route</b> it to the nearest waypoint;
/// press <b>N</b> to spawn a car at the waypoint nearest the cursor (and select it);
/// press Escape to deselect. The selected car is tinted cyan and its remaining route is drawn as a
/// line. Uses the new Input System raycast (mirrors <see cref="ClickableTrafficLight"/>);
/// <see cref="CameraController"/> only uses the right button, so left-click is free.
///
/// Added at runtime by <see cref="SimulationUI"/> — no scene regeneration needed. Breakdowns moved to
/// the inspector's "Break down" button (this replaces the old click-to-stall IncidentController).
/// </summary>
public class CarSelectionController : MonoBehaviour
{
    public float clickRayDistance = 2000f;

    /// <summary>The currently selected car, or null. Read by the inspector UI.</summary>
    public CarAI Selected { get; private set; }

    [Tooltip("Seconds for the route line to grow from both ends and meet in the middle.")]
    public float routeGrowDuration = 0.7f;

    private SimulationUI ui;
    private LineRenderer routeLine;      // grows forward from the car
    private LineRenderer routeLineBack;  // grows backward from the destination
    private Transform destMarker;
    private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);

    // Route-reveal animation state (mirrors the bidirectional A*: two fronts meet in the centre).
    private float growT;                 // 0..1 reveal progress
    private CarAI growFor;               // car the current reveal belongs to
    private Waypoint growDest;           // destination it was revealed for (re-animate on re-route)
    private readonly List<Vector3> pathPts = new List<Vector3>();
    private readonly List<Vector3> segBuf = new List<Vector3>();
    private float[] cumLen = new float[64];

    private CarSpawner spawner;

    private void Start()
    {
        ui = Object.FindFirstObjectByType<SimulationUI>();
        spawner = Object.FindFirstObjectByType<CarSpawner>();
        SetupRouteLine();
        SetupDestinationMarker();
    }

    private void Update()
    {
        HandleClick();
        HandleKeys();
        UpdateRouteLine();
    }

    private void HandleClick()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

        Vector2 screenPos = mouse.position.ReadValue();
        if (IsOverPanel(screenPos)) return; // don't act on clicks that land on a control window

        Camera cam = Camera.main;
        if (cam == null) return;
        Ray ray = cam.ScreenPointToRay(screenPos);

        // Nearest car under the cursor?
        CarAI hitCar = null;
        float best = float.MaxValue;
        var hits = Physics.RaycastAll(ray, clickRayDistance, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var c = hits[i].collider != null ? hits[i].collider.GetComponentInParent<CarAI>() : null;
            if (c != null && hits[i].distance < best) { best = hits[i].distance; hitCar = c; }
        }

        if (hitCar != null) { Select(hitCar); return; }

        // Empty click with a car selected -> re-route it to the nearest node to the ground point.
        if (Selected != null && groundPlane.Raycast(ray, out float enter))
        {
            Waypoint wp = NearestNode(ray.GetPoint(enter));
            if (wp != null) Selected.TrySetDestination(wp);
        }
    }

    private void HandleKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.escapeKey.wasPressedThisFrame) Deselect();
        if (kb.nKey.wasPressedThisFrame) SpawnAtCursor();
    }

    /// <summary>N key: spawn a car at the waypoint nearest the cursor and select it.</summary>
    private void SpawnAtCursor()
    {
        if (spawner == null) return;
        Mouse mouse = Mouse.current;
        Camera cam = Camera.main;
        if (mouse == null || cam == null) return;

        Vector2 screenPos = mouse.position.ReadValue();
        if (IsOverPanel(screenPos)) return;

        Ray ray = cam.ScreenPointToRay(screenPos);
        if (!groundPlane.Raycast(ray, out float enter)) return;

        Waypoint wp = NearestNode(ray.GetPoint(enter));
        if (wp == null) return;
        CarAI car = spawner.SpawnManualAt(wp);
        if (car != null) Select(car);
    }

    public void Select(CarAI car)
    {
        if (Selected == car) return;
        if (Selected != null) Selected.IsSelected = false;
        Selected = car;
        if (Selected != null) Selected.IsSelected = true;
    }

    public void Deselect()
    {
        if (Selected != null) Selected.IsSelected = false;
        Selected = null;
    }

    private static Waypoint NearestNode(Vector3 point)
    {
        var nodes = TrafficSimulationManager.Nodes;
        Waypoint best = null;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            Waypoint w = nodes[i];
            if (w == null) continue;
            Vector3 d = w.transform.position - point; d.y = 0f;
            float s = d.sqrMagnitude;
            if (s < bestSqr) { bestSqr = s; best = w; }
        }
        return best;
    }

    private bool IsOverPanel(Vector2 screenPos)
    {
        if (ui == null) return false;
        Vector2 guiPos = new Vector2(screenPos.x, Screen.height - screenPos.y);
        return ui.windowRect.Contains(guiPos) || (ui.InspectorVisible && ui.inspectorRect.Contains(guiPos));
    }

    // ---- Route line ----

    private void SetupRouteLine()
    {
        routeLine = MakeRouteLine("SelectedRouteLine");
        routeLineBack = MakeRouteLine("SelectedRouteLineBack");
    }

    private LineRenderer MakeRouteLine(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.widthMultiplier = 0.35f;
        lr.numCornerVertices = 2;
        lr.material = MakeLineMaterial(new Color(0.1f, 0.9f, 1f));
        lr.textureMode = LineTextureMode.Stretch;
        lr.positionCount = 0;
        lr.useWorldSpace = true;
        return lr;
    }

    /// <summary>
    /// Runtime material via the project's primitive-clone pattern (see RoadRenderer.GetRoadMaterial):
    /// borrow a throwaway Quad's default material so it's valid under URP without Shader.Find.
    /// </summary>
    private static Material MakeLineMaterial(Color color)
    {
        var temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var baseMat = temp.GetComponent<MeshRenderer>().sharedMaterial;
        var mat = new Material(baseMat) { name = "RouteLineMat" };
        if (Application.isPlaying) Destroy(temp); else DestroyImmediate(temp);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        return mat;
    }

    /// <summary>Pulsing beacon at the selected car's final destination (Game-view visible).</summary>
    private void SetupDestinationMarker()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "SelectedDestinationMarker";
        go.transform.SetParent(transform, false);
        Destroy(go.GetComponent<Collider>());
        go.GetComponent<MeshRenderer>().material = MakeLineMaterial(new Color(0.1f, 0.9f, 1f));
        go.SetActive(false);
        destMarker = go.transform;
    }

    private void UpdateRouteLine()
    {
        if (routeLine == null) return;

        // Destination beacon: shown whenever the selected car has a GPS destination.
        Waypoint dest = Selected != null ? Selected.destination : null;
        if (destMarker != null)
        {
            bool show = dest != null;
            if (destMarker.gameObject.activeSelf != show) destMarker.gameObject.SetActive(show);
            if (show)
            {
                float pulse = 0.9f + 0.3f * Mathf.Sin(Time.time * 5f);
                destMarker.position = dest.transform.position + Vector3.up * 0.8f;
                destMarker.localScale = Vector3.one * pulse;
            }
        }

        if (Selected == null || Selected.route == null || Selected.route.Count == 0)
        {
            routeLine.positionCount = 0;
            routeLineBack.positionCount = 0;
            growFor = null;
            return;
        }

        // Restart the reveal when a new car is selected or the car is re-routed somewhere new
        // (routine congestion re-plans keep the same destination and don't re-animate).
        if (Selected != growFor || dest != growDest)
        {
            growT = 0f;
            growFor = Selected;
            growDest = dest;
        }
        growT = Mathf.MoveTowards(growT, 1f, Time.deltaTime / Mathf.Max(0.05f, routeGrowDuration));

        // Car position + each remaining route node, lifted above the road surface.
        var route = Selected.route;
        int start = Mathf.Clamp(Selected.RouteIndex, 0, route.Count);
        pathPts.Clear();
        pathPts.Add(Selected.transform.position + Vector3.up * 0.4f);
        for (int i = start; i < route.Count; i++)
        {
            Vector3 pos = route[i] != null ? route[i].transform.position : Selected.transform.position;
            pathPts.Add(pos + Vector3.up * 0.4f);
        }
        if (pathPts.Count < 2)
        {
            routeLine.positionCount = 0;
            routeLineBack.positionCount = 0;
            return;
        }

        // Cumulative arc length along the polyline.
        if (cumLen.Length < pathPts.Count) cumLen = new float[Mathf.NextPowerOfTwo(pathPts.Count)];
        cumLen[0] = 0f;
        for (int i = 1; i < pathPts.Count; i++)
            cumLen[i] = cumLen[i - 1] + Vector3.Distance(pathPts[i - 1], pathPts[i]);
        float total = cumLen[pathPts.Count - 1];

        // Two fronts grow toward the centre — the visual echo of the bidirectional search.
        float reveal = growT * total * 0.5f;
        FillSegment(routeLine, 0f, reveal);
        FillSegment(routeLineBack, total - reveal, total);
    }

    /// <summary>Render the polyline slice between arc lengths <paramref name="a"/> and <paramref name="b"/>.</summary>
    private void FillSegment(LineRenderer lr, float a, float b)
    {
        if (b - a < 0.01f) { lr.positionCount = 0; return; }
        segBuf.Clear();
        segBuf.Add(PointAtArc(a));
        for (int i = 1; i < pathPts.Count - 1; i++)
            if (cumLen[i] > a && cumLen[i] < b) segBuf.Add(pathPts[i]);
        segBuf.Add(PointAtArc(b));
        lr.positionCount = segBuf.Count;
        for (int i = 0; i < segBuf.Count; i++) lr.SetPosition(i, segBuf[i]);
    }

    private Vector3 PointAtArc(float s)
    {
        for (int i = 1; i < pathPts.Count; i++)
        {
            if (cumLen[i] >= s)
            {
                float seg = cumLen[i] - cumLen[i - 1];
                float t = seg > 0.0001f ? (s - cumLen[i - 1]) / seg : 0f;
                return Vector3.Lerp(pathPts[i - 1], pathPts[i], t);
            }
        }
        return pathPts[pathPts.Count - 1];
    }
}
