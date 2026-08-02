using EmergencyAllocation.Core.Solving;

namespace EmergencyAllocation.Infrastructure.Solver;

public static class RoutePlanner
{
    public static RouteInfo FindRoute(
        IReadOnlyList<RoadState> roads,
        decimal vehicleHeightMeters,
        string fromNode,
        string toNode)
    {
        if (string.Equals(fromNode, toNode, StringComparison.Ordinal))
        {
            return new RouteInfo(new[] { fromNode }, 0, true, null);
        }

        var adjacency = BuildAdjacency(roads, vehicleHeightMeters, out var blockedRoads);

        var distances = new Dictionary<string, int>();
        var previous = new Dictionary<string, (string Previous, string RoadId)>();
        var queue = new PriorityQueue<string, int>();

        distances[fromNode] = 0;
        queue.Enqueue(fromNode, 0);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (string.Equals(current, toNode, StringComparison.Ordinal))
            {
                break;
            }

            if (!adjacency.TryGetValue(current, out var edges))
            {
                continue;
            }

            var currentDistance = distances[current];
            foreach (var edge in edges)
            {
                var candidate = currentDistance + edge.TravelTimeMinutes;
                if (!distances.TryGetValue(edge.To, out var existing) || candidate < existing)
                {
                    distances[edge.To] = candidate;
                    previous[edge.To] = (current, edge.RoadId);
                    queue.Enqueue(edge.To, candidate);
                }
            }
        }

        if (!distances.ContainsKey(toNode))
        {
            var reason = BuildBlockReason(roads, vehicleHeightMeters, fromNode, toNode, blockedRoads);
            return new RouteInfo(Array.Empty<string>(), int.MaxValue, false, reason);
        }

        var path = ReconstructPath(previous, fromNode, toNode);
        return new RouteInfo(path, distances[toNode], true, null);
    }

    private static Dictionary<string, List<Edge>> BuildAdjacency(
        IReadOnlyList<RoadState> roads,
        decimal vehicleHeightMeters,
        out List<RoadState> blockedRoads)
    {
        var adjacency = new Dictionary<string, List<Edge>>();
        blockedRoads = new List<RoadState>();

        foreach (var road in roads)
        {
            if (!road.IsOpen)
            {
                blockedRoads.Add(road);
                continue;
            }

            if (vehicleHeightMeters > road.HeightLimitMeters)
            {
                blockedRoads.Add(road);
                continue;
            }

            AddEdge(adjacency, road.FromNode, road.ToNode, road.TravelTimeMinutes, road.Id);
            AddEdge(adjacency, road.ToNode, road.FromNode, road.TravelTimeMinutes, road.Id);
        }

        return adjacency;
    }

    private static void AddEdge(
        Dictionary<string, List<Edge>> adjacency,
        string from,
        string to,
        int time,
        string roadId)
    {
        if (!adjacency.TryGetValue(from, out var list))
        {
            list = new List<Edge>();
            adjacency[from] = list;
        }

        list.Add(new Edge(to, time, roadId));
    }

    private static List<string> ReconstructPath(
        Dictionary<string, (string Previous, string RoadId)> previous,
        string fromNode,
        string toNode)
    {
        var path = new List<string>();
        var current = toNode;
        while (!string.Equals(current, fromNode, StringComparison.Ordinal))
        {
            path.Add(current);
            current = previous[current].Previous;
        }

        path.Add(fromNode);
        path.Reverse();
        return path;
    }

    private static string BuildBlockReason(
        IReadOnlyList<RoadState> roads,
        decimal vehicleHeightMeters,
        string fromNode,
        string toNode,
        List<RoadState> blockedRoads)
    {
        var closed = blockedRoads.Where(r => !r.IsOpen)
            .Select(r => string.IsNullOrEmpty(r.ClosedByEventId)
                ? r.Id
                : $"{r.Id}(事件{r.ClosedByEventId})")
            .ToList();
        var tooLow = blockedRoads
            .Where(r => r.IsOpen && vehicleHeightMeters > r.HeightLimitMeters)
            .Select(r => $"{r.Id}(限高{r.HeightLimitMeters}m<车高{vehicleHeightMeters}m)")
            .ToList();

        var reasons = new List<string> { $"无法从 {fromNode} 到达 {toNode}" };
        if (closed.Count > 0)
        {
            reasons.Add($"中断路段: {string.Join(", ", closed)}");
        }

        if (tooLow.Count > 0)
        {
            reasons.Add($"限高不满足: {string.Join(", ", tooLow)}");
        }

        if (closed.Count == 0 && tooLow.Count == 0)
        {
            reasons.Add("路网中不存在连通路径");
        }

        return string.Join("；", reasons);
    }

    private readonly record struct Edge(string To, int TravelTimeMinutes, string RoadId);
}
