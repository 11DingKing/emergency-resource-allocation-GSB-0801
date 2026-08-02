using EmergencyAllocation.Domain.Solver;

namespace EmergencyAllocation.Domain.Solver;

public sealed class RoadGraph
{
    private readonly Dictionary<string, List<Edge>> _adj = new(StringComparer.Ordinal);

    public RoadGraph(IEnumerable<SolverRoad> roads, double vehicleHeightMeters)
    {
        foreach (var r in roads)
        {
            if (!r.IsOpen) continue;
            if (r.HeightLimitMeters.HasValue && vehicleHeightMeters > r.HeightLimitMeters.Value)
                continue;
            AddEdge(r.FromNodeId, r.ToNodeId, r);
        }
    }

    private void AddEdge(string from, string to, SolverRoad road)
    {
        if (!_adj.TryGetValue(from, out var list))
        {
            list = new List<Edge>();
            _adj[from] = list;
        }
        list.Add(new Edge(to, road));
    }

    public SolverRoute? ShortestPath(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return new SolverRoute(Array.Empty<SolverRouteStep>(), 0, new[] { from });
        }

        var dist = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
        var prev = new Dictionary<string, (string Node, SolverRoad Road)>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pq = new SortedSet<Tuple<int, string, int>>(Comparer<Tuple<int, string, int>>.Create((a, b) =>
        {
            var c = a.Item1.CompareTo(b.Item1);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Item2, b.Item2);
            if (c != 0) return c;
            return a.Item3.CompareTo(b.Item3);
        }));
        pq.Add(Tuple.Create(0, from, 0));
        int tieCounter = 0;

        while (pq.Count > 0)
        {
            var min = pq.Min!;
            var d = min.Item1;
            var u = min.Item2;
            pq.Remove(min);
            if (!visited.Add(u)) continue;
            if (string.Equals(u, to, StringComparison.Ordinal)) break;

            if (!_adj.TryGetValue(u, out var edges)) continue;
            foreach (var e in edges.OrderBy(x => x.Road.Code, StringComparer.Ordinal))
            {
                if (visited.Contains(e.To)) continue;
                var nd = d + e.Road.TravelTimeMinutes;
                if (!dist.TryGetValue(e.To, out var old) || nd < old)
                {
                    dist[e.To] = nd;
                    prev[e.To] = (u, e.Road);
                    pq.Add(Tuple.Create(nd, e.To, ++tieCounter));
                }
            }
        }

        if (!dist.ContainsKey(to)) return null;

        var steps = new List<SolverRouteStep>();
        var nodes = new List<string>();
        var cur = to;
        nodes.Add(cur);
        while (prev.TryGetValue(cur, out var p))
        {
            steps.Add(new SolverRouteStep(p.Road.Code, p.Node, cur, p.Road.TravelTimeMinutes));
            cur = p.Node;
            nodes.Add(cur);
        }
        steps.Reverse();
        nodes.Reverse();
        return new SolverRoute(steps, dist[to], nodes);
    }

    private readonly record struct Edge(string To, SolverRoad Road);
}
