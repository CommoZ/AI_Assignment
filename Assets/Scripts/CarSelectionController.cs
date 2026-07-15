using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Left-click a car to <b>select</b> it (drives the inspector panel in <see cref="SimulationUI"/>);
/// with a car selected, left-click anywhere on the map to <b>re-route</b> it to the nearest waypoint;
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

    private SimulationUI ui;
    private LineRenderer routeLine;
    private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);

    private void Start()
    {
        ui = Object.FindFirstObjectByType<SimulationUI>();
        SetupRouteLine();
    }

    private void Update()
    {
        HandleClick();
        HandleDeselectKey();
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

    private void HandleDeselectKey()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) Deselect();
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
        var go = new GameObject("SelectedRouteLine");
        go.transform.SetParent(transform, false);
        routeLine = go.AddComponent<LineRenderer>();
        routeLine.widthMultiplier = 0.35f;
        routeLine.numCornerVertices = 2;
        routeLine.material = MakeLineMaterial(new Color(0.1f, 0.9f, 1f));
        routeLine.textureMode = LineTextureMode.Stretch;
        routeLine.positionCount = 0;
        routeLine.useWorldSpace = true;
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

    private void UpdateRouteLine()
    {
        if (routeLine == null) return;
        if (Selected == null || Selected.route == null || Selected.route.Count == 0)
        {
            routeLine.positionCount = 0;
            return;
        }

        // Car position + each remaining route node, lifted above the road surface.
        var route = Selected.route;
        int start = Mathf.Clamp(Selected.RouteIndex, 0, route.Count);
        int count = 1 + Mathf.Max(0, route.Count - start);
        routeLine.positionCount = count;
        routeLine.SetPosition(0, Selected.transform.position + Vector3.up * 0.4f);
        int p = 1;
        for (int i = start; i < route.Count; i++)
        {
            Vector3 pos = route[i] != null ? route[i].transform.position : Selected.transform.position;
            routeLine.SetPosition(p++, pos + Vector3.up * 0.4f);
        }
    }
}
