using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Generates a visible road surface from the waypoint graph so roads appear in the Game view
/// (the waypoints themselves are editor-only gizmos). For every edge (waypoint -> next) it lays a
/// flat quad of <see cref="roadWidth"/> just above the ground; overlapping quads naturally fill
/// intersections and roundabouts. All quads are combined into one mesh on a child object.
///
/// Works for any builder (city / junction / roundabout / ramp) because it only reads the graph.
/// Rebuilds automatically at play start, on demand via the context menu, and is re-triggered by
/// the SimulationUI "Rebuild city" button.
/// </summary>
[ExecuteAlways]
public class RoadRenderer : MonoBehaviour
{
    [Tooltip("Width of the road surface (metres).")]
    public float roadWidth = 4f;
    [Tooltip("Height of the surface above the ground (keep below the car height ~0.25).")]
    public float surfaceHeight = 0.05f;
    [Tooltip("Road colour.")]
    public Color roadColor = new Color(0.16f, 0.16f, 0.19f);
    [Tooltip("Rebuild the mesh automatically when the scene starts playing.")]
    public bool rebuildOnStart = true;

    private Material roadMaterial;

    private void Start()
    {
        if (rebuildOnStart && Application.isPlaying) Rebuild();
    }

    [ContextMenu("Rebuild road mesh")]
    public void Rebuild()
    {
        // Remove any previous surface child.
        var existing = transform.Find("RoadSurface");
        if (existing != null)
        {
            if (Application.isPlaying) Destroy(existing.gameObject);
            else DestroyImmediate(existing.gameObject);
        }

        var waypoints = FindObjectsByType<Waypoint>(FindObjectsSortMode.None);
        if (waypoints.Length == 0) return;

        var verts = new List<Vector3>();
        var tris = new List<int>();
        float half = roadWidth * 0.5f;

        foreach (var wp in waypoints)
        {
            if (wp == null || wp.nextWaypoints == null) continue;
            foreach (var next in wp.nextWaypoints)
            {
                if (next == null) continue;
                Vector3 a = wp.transform.position; a.y = surfaceHeight;
                Vector3 b = next.transform.position; b.y = surfaceHeight;
                Vector3 dir = b - a; dir.y = 0f;
                if (dir.sqrMagnitude < 0.0004f) continue;
                dir.Normalize();
                Vector3 side = Vector3.Cross(Vector3.up, dir) * half;

                int i0 = verts.Count;
                verts.Add(a - side); // 0
                verts.Add(a + side); // 1
                verts.Add(b + side); // 2
                verts.Add(b - side); // 3
                // Double-sided so the surface shows regardless of winding / view angle.
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 1);
                tris.Add(i0); tris.Add(i0 + 3); tris.Add(i0 + 2);
                tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
            }
        }

        if (verts.Count == 0) return;

        var mesh = new Mesh { name = "RoadSurfaceMesh" };
        mesh.indexFormat = IndexFormat.UInt32; // cities can exceed 65k vertices
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        var normals = new Vector3[verts.Count];
        for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
        mesh.normals = normals;
        mesh.RecalculateBounds();

        var go = new GameObject("RoadSurface");
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = GetRoadMaterial();
    }

    /// <summary>A URP-compatible dark material, cloned from Unity's default so it renders in URP.</summary>
    private Material GetRoadMaterial()
    {
        if (roadMaterial != null) return roadMaterial;

        // Borrow the default material from a throwaway primitive (guaranteed valid under URP).
        var temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var baseMat = temp.GetComponent<MeshRenderer>().sharedMaterial;
        roadMaterial = new Material(baseMat) { name = "RoadSurfaceMat" };
        if (Application.isPlaying) Destroy(temp); else DestroyImmediate(temp);

        if (roadMaterial.HasProperty("_BaseColor")) roadMaterial.SetColor("_BaseColor", roadColor);
        if (roadMaterial.HasProperty("_Color")) roadMaterial.SetColor("_Color", roadColor);
        return roadMaterial;
    }
}
