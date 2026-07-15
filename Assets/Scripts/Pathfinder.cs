using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pluggable cost of driving one edge (from one waypoint to a direct successor). The default is
/// physical distance; a future <c>CongestionCost</c> could return travel time from live traffic
/// density so cars route around jams (the "perfect information" experiment) without changing the
/// pathfinder or the cars.
/// </summary>
public interface IEdgeCost
{
    float Cost(Waypoint from, Waypoint to);
}

/// <summary>Straight-line length of the edge. Static shortest-path baseline.</summary>
public class DistanceCost : IEdgeCost
{
    public static readonly DistanceCost Default = new DistanceCost();
    public float Cost(Waypoint from, Waypoint to)
        => Vector3.Distance(from.transform.position, to.transform.position);
}

/// <summary>
/// Distance cost, but edges leading into <see cref="blocked"/> are made hugely expensive so the
/// path routes around it if any alternative exists (used to detour around a broken-down car). If
/// the blocked node is the only way through, a finite-but-costly path is still returned.
/// </summary>
public class AvoidNodeCost : IEdgeCost
{
    private readonly Waypoint blocked;
    private const float Penalty = 100000f;
    public AvoidNodeCost(Waypoint blocked) { this.blocked = blocked; }
    public float Cost(Waypoint from, Waypoint to)
    {
        float d = Vector3.Distance(from.transform.position, to.transform.position);
        return to == blocked ? d + Penalty : d;
    }
}

/// <summary>
/// Travel-cost that grows with live congestion at the destination node: a busy road costs more, so
/// A* naturally routes cars around jams (the "cars react to traffic" model). The congestion figure
/// comes from <see cref="TrafficSimulationManager.NodeCongestion"/> (a smoothed count of cars
/// heading to each node); the multiplier is <see cref="TrafficSimulationManager.CongestionWeight"/>.
/// Cost stays &gt;= physical distance, so the Euclidean heuristic in <see cref="Pathfinder"/> remains
/// admissible and the returned path is still optimal under this cost.
/// </summary>
public class CongestionCost : IEdgeCost
{
    public static readonly CongestionCost Default = new CongestionCost();
    public float Cost(Waypoint from, Waypoint to)
    {
        float d = Vector3.Distance(from.transform.position, to.transform.position);
        return d * (1f + TrafficSimulationManager.CongestionWeight * TrafficSimulationManager.NodeCongestion(to));
    }
}

/// <summary>
/// Bidirectional A* over the <see cref="Waypoint"/> graph.
///
/// Two A* searches run at once — one forward from <c>start</c> along <see cref="Waypoint.nextWaypoints"/>,
/// one backward from <c>goal</c> along the reverse edges — and meet in the middle, which explores
/// far fewer nodes than a single-ended search. The Euclidean straight-line distance is the
/// (admissible, consistent) heuristic, so the returned path is a true shortest path under the
/// supplied <see cref="IEdgeCost"/>.
///
/// The reverse-adjacency map is cached and only rebuilt when the scene's node count changes
/// (road graphs are static once built), so per-spawn planning stays cheap.
/// </summary>
public static class Pathfinder
{
    // Cached reverse adjacency (successor -> its predecessors), keyed by node count for invalidation.
    private static Dictionary<Waypoint, List<Waypoint>> predecessors;
    private static int predecessorsBuiltForCount = -1;

    public static List<Waypoint> FindPath(Waypoint start, Waypoint goal, IEdgeCost cost)
    {
        if (start == null || goal == null) return null;
        if (start == goal) return new List<Waypoint> { start };
        if (cost == null) cost = DistanceCost.Default;

        var preds = GetPredecessors();
        Vector3 sPos = start.transform.position;
        Vector3 gPos = goal.transform.position;

        var gF = new Dictionary<Waypoint, float>();
        var gB = new Dictionary<Waypoint, float>();
        var parentF = new Dictionary<Waypoint, Waypoint>(); // node -> predecessor toward start
        var parentB = new Dictionary<Waypoint, Waypoint>(); // node -> successor toward goal
        var closedF = new HashSet<Waypoint>();
        var closedB = new HashSet<Waypoint>();
        var openF = new MinHeap();
        var openB = new MinHeap();

        gF[start] = 0f;
        openF.Push(Heuristic(start, gPos), start);
        gB[goal] = 0f;
        openB.Push(Heuristic(goal, sPos), goal);

        float mu = float.PositiveInfinity; // best full-path cost found so far
        Waypoint meet = null;              // node where the two searches meet on that best path

        while (openF.Count > 0 && openB.Count > 0)
        {
            // With admissible heuristics, once the cheaper frontier's best f-value can't beat the
            // best path found, neither side can improve — stop.
            if (Mathf.Min(openF.PeekPriority(), openB.PeekPriority()) >= mu) break;

            if (openF.PeekPriority() <= openB.PeekPriority())
            {
                openF.Pop(out _, out Waypoint u);
                if (!closedF.Add(u)) continue;

                var succ = u.nextWaypoints;
                if (succ != null)
                {
                    for (int i = 0; i < succ.Count; i++)
                    {
                        Waypoint v = succ[i];
                        if (v == null) continue;
                        float ng = gF[u] + cost.Cost(u, v);
                        if (!gF.TryGetValue(v, out float old) || ng < old)
                        {
                            gF[v] = ng;
                            parentF[v] = u;
                            openF.Push(ng + Heuristic(v, gPos), v);
                            if (gB.TryGetValue(v, out float gb) && ng + gb < mu)
                            {
                                mu = ng + gb;
                                meet = v;
                            }
                        }
                    }
                }
            }
            else
            {
                openB.Pop(out _, out Waypoint u);
                if (!closedB.Add(u)) continue;

                if (preds.TryGetValue(u, out List<Waypoint> plist))
                {
                    for (int i = 0; i < plist.Count; i++)
                    {
                        Waypoint v = plist[i]; // edge v -> u exists in the forward graph
                        if (v == null) continue;
                        float ng = gB[u] + cost.Cost(v, u);
                        if (!gB.TryGetValue(v, out float old) || ng < old)
                        {
                            gB[v] = ng;
                            parentB[v] = u;
                            openB.Push(ng + Heuristic(v, sPos), v);
                            if (gF.TryGetValue(v, out float gf) && gf + ng < mu)
                            {
                                mu = gf + ng;
                                meet = v;
                            }
                        }
                    }
                }
            }
        }

        if (meet == null) return null; // disconnected: no route

        // Reconstruct start .. meet (follow forward parents) then meet .. goal (follow backward parents).
        var path = new List<Waypoint>();
        var head = new List<Waypoint>();
        for (Waypoint n = meet; n != null; )
        {
            head.Add(n);
            if (n == start) break;
            parentF.TryGetValue(n, out n);
        }
        head.Reverse();
        path.AddRange(head);

        for (Waypoint n = meet; parentB.TryGetValue(n, out Waypoint nx) && nx != null; )
        {
            path.Add(nx);
            n = nx;
            if (n == goal) break;
        }
        return path;
    }

    private static float Heuristic(Waypoint n, Vector3 target)
        => Vector3.Distance(n.transform.position, target);

    private static Dictionary<Waypoint, List<Waypoint>> GetPredecessors()
    {
        var nodes = TrafficSimulationManager.Nodes;
        if (predecessors != null && predecessorsBuiltForCount == nodes.Count)
            return predecessors;

        var map = new Dictionary<Waypoint, List<Waypoint>>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            Waypoint w = nodes[i];
            if (w == null || w.nextWaypoints == null) continue;
            for (int j = 0; j < w.nextWaypoints.Count; j++)
            {
                Waypoint n = w.nextWaypoints[j];
                if (n == null) continue;
                if (!map.TryGetValue(n, out List<Waypoint> list))
                {
                    list = new List<Waypoint>();
                    map[n] = list;
                }
                list.Add(w);
            }
        }
        predecessors = map;
        predecessorsBuiltForCount = nodes.Count;
        return map;
    }

    /// <summary>Drop the cached reverse graph (call after rebuilding roads at runtime).</summary>
    public static void InvalidateCache()
    {
        predecessors = null;
        predecessorsBuiltForCount = -1;
    }

    /// <summary>
    /// Minimal binary min-heap of (priority, waypoint). Supports duplicate pushes (lazy
    /// decrease-key); stale entries are skipped by the closed-set check in the search loop.
    /// </summary>
    private class MinHeap
    {
        private readonly List<float> priorities = new List<float>();
        private readonly List<Waypoint> items = new List<Waypoint>();

        public int Count => items.Count;

        public float PeekPriority() => priorities[0];

        public void Push(float priority, Waypoint item)
        {
            priorities.Add(priority);
            items.Add(item);
            int i = items.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (priorities[parent] <= priorities[i]) break;
                Swap(i, parent);
                i = parent;
            }
        }

        public bool Pop(out float priority, out Waypoint item)
        {
            if (items.Count == 0) { priority = 0f; item = null; return false; }
            priority = priorities[0];
            item = items[0];

            int last = items.Count - 1;
            priorities[0] = priorities[last];
            items[0] = items[last];
            priorities.RemoveAt(last);
            items.RemoveAt(last);

            int i = 0;
            int n = items.Count;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                if (l < n && priorities[l] < priorities[smallest]) smallest = l;
                if (r < n && priorities[r] < priorities[smallest]) smallest = r;
                if (smallest == i) break;
                Swap(i, smallest);
                i = smallest;
            }
            return true;
        }

        private void Swap(int a, int b)
        {
            (priorities[a], priorities[b]) = (priorities[b], priorities[a]);
            (items[a], items[b]) = (items[b], items[a]);
        }
    }
}
