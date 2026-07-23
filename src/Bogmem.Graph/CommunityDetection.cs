namespace Bogmem.Graph;

public interface ICommunityDetector
{
    CommunityPartition Detect(
        ActorGraphSnapshot graph,
        CommunityDetectionOptions? options = null);
}

public sealed record CommunityDetectionOptions(
    double Resolution = 1,
    double MinimumEdgeWeight = 0,
    int MaxLevels = 32,
    int MaxLocalMovingPasses = 100,
    double MinimumGain = 1e-12);

public sealed record Community(
    int Id,
    IReadOnlyList<string> ActorIds,
    double InternalWeight);

public sealed class CommunityPartition
{
    public CommunityPartition(
        string algorithm,
        IReadOnlyDictionary<string, int> assignments,
        IReadOnlyList<Community> communities,
        double modularity,
        int levels,
        int localMovingPasses)
    {
        Algorithm = algorithm;
        Assignments = assignments;
        Communities = communities;
        Modularity = modularity;
        Levels = levels;
        LocalMovingPasses = localMovingPasses;
    }

    public string Algorithm { get; }
    public IReadOnlyDictionary<string, int> Assignments { get; }
    public IReadOnlyList<Community> Communities { get; }
    public double Modularity { get; }
    public int Levels { get; }
    public int LocalMovingPasses { get; }

    public int CommunityOf(string actorId) =>
        Assignments.TryGetValue(actorId, out var communityId)
            ? communityId
            : throw new KeyNotFoundException($"Actor '{actorId}' is not in this partition.");
}

/// <summary>
/// Deterministic Leiden-style community detection for weighted undirected
/// snapshots. Each level performs modularity-based local moving, refines every
/// proposed community into connected components, and aggregates the refined
/// graph before the next level. The connectivity refinement is the important
/// correctness improvement over the simple label-moving Louvain extension
/// currently shipped by BogDB.
///
/// The implementation deliberately avoids random scheduling so identical
/// memory windows produce identical assignments. Its algorithm identifier is
/// versioned because deterministic refinement is not bit-for-bit equivalent to
/// stochastic reference Leiden implementations.
/// </summary>
public sealed class LeidenCommunityDetector : ICommunityDetector
{
    public const string AlgorithmId = "leiden-deterministic-v1";

    public CommunityPartition Detect(
        ActorGraphSnapshot graph,
        CommunityDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new();
        Validate(options);

        var initial = GraphLevel.FromSnapshot(graph, options.MinimumEdgeWeight);
        if (initial.NodeCount == 0)
            return new(AlgorithmId, new Dictionary<string, int>(), [], 0, 0, 0);

        var current = initial;
        int[] finalPartition = Enumerable.Range(0, current.NodeCount).ToArray();
        var levels = 0;
        var totalPasses = 0;

        while (levels < options.MaxLevels)
        {
            levels++;
            var (partition, passes) = LocalMove(current, options);
            totalPasses += passes;
            finalPartition = RefineConnected(current, partition);
            var communityCount = finalPartition.Distinct().Count();
            if (communityCount == current.NodeCount || levels >= options.MaxLevels)
                break;
            current = current.Aggregate(finalPartition);
        }

        var rawGroups = Enumerable.Range(0, current.NodeCount)
            .GroupBy(node => finalPartition[node])
            .Select(group => group
                .SelectMany(node => current.Members[node])
                .OrderBy(actorId => actorId, StringComparer.Ordinal)
                .ToArray())
            .OrderBy(group => group[0], StringComparer.Ordinal)
            .ToArray();

        var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var communityId = 0; communityId < rawGroups.Length; communityId++)
        {
            foreach (var actorId in rawGroups[communityId])
                assignments[actorId] = communityId;
        }

        var communities = rawGroups
            .Select((actorIds, communityId) => new Community(
                communityId,
                actorIds,
                graph.Edges
                    .Where(edge =>
                        assignments.GetValueOrDefault(edge.SourceId, -1) == communityId &&
                        assignments.GetValueOrDefault(edge.TargetId, -1) == communityId)
                    .Sum(edge => edge.Weight)))
            .ToArray();
        var modularity = CalculateModularity(initial, assignments, options.Resolution);
        return new(AlgorithmId, assignments, communities, modularity, levels, totalPasses);
    }

    private static (int[] Partition, int Passes) LocalMove(
        GraphLevel graph,
        CommunityDetectionOptions options)
    {
        var partition = Enumerable.Range(0, graph.NodeCount).ToArray();
        if (graph.TotalEdgeWeight <= 0)
            return (partition, 0);

        var totals = graph.Degrees.ToArray();
        var denominator = 2 * graph.TotalEdgeWeight * graph.TotalEdgeWeight;
        var passes = 0;

        while (passes < options.MaxLocalMovingPasses)
        {
            passes++;
            var changed = false;
            for (var node = 0; node < graph.NodeCount; node++)
            {
                var degree = graph.Degrees[node];
                if (degree <= 0) continue;

                var oldCommunity = partition[node];
                totals[oldCommunity] -= degree;
                var weightsByCommunity = new Dictionary<int, double>();
                foreach (var (neighbor, weight) in graph.Adjacency[node])
                {
                    if (neighbor == node || weight <= 0) continue;
                    var community = partition[neighbor];
                    weightsByCommunity[community] =
                        weightsByCommunity.GetValueOrDefault(community) + weight;
                }

                var oldScore = InsertionScore(
                    weightsByCommunity.GetValueOrDefault(oldCommunity),
                    totals[oldCommunity],
                    degree,
                    graph.TotalEdgeWeight,
                    denominator,
                    options.Resolution);
                var bestCommunity = oldCommunity;
                var bestScore = oldScore;
                foreach (var candidate in weightsByCommunity.Keys
                             .Append(oldCommunity)
                             .Distinct()
                             .OrderBy(id => id))
                {
                    var score = InsertionScore(
                        weightsByCommunity.GetValueOrDefault(candidate),
                        totals[candidate],
                        degree,
                        graph.TotalEdgeWeight,
                        denominator,
                        options.Resolution);
                    if (score > bestScore + options.MinimumGain)
                    {
                        bestScore = score;
                        bestCommunity = candidate;
                    }
                }

                partition[node] = bestCommunity;
                totals[bestCommunity] += degree;
                changed |= bestCommunity != oldCommunity;
            }

            if (!changed) break;
        }

        return (Normalize(partition), passes);
    }

    private static double InsertionScore(
        double weightIntoCommunity,
        double communityDegree,
        double nodeDegree,
        double totalEdgeWeight,
        double denominator,
        double resolution) =>
        weightIntoCommunity / totalEdgeWeight -
        resolution * communityDegree * nodeDegree / denominator;

    private static int[] RefineConnected(GraphLevel graph, int[] partition)
    {
        var refined = Enumerable.Repeat(-1, graph.NodeCount).ToArray();
        var nextCommunity = 0;
        foreach (var proposed in partition.Distinct().OrderBy(id => id))
        {
            var members = Enumerable.Range(0, graph.NodeCount)
                .Where(node => partition[node] == proposed)
                .ToHashSet();
            while (members.Count > 0)
            {
                var start = members.Min();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                members.Remove(start);
                refined[start] = nextCommunity;
                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    foreach (var (neighbor, weight) in graph.Adjacency[node]
                                 .Where(pair => pair.Value > 0)
                                 .OrderBy(pair => pair.Key))
                    {
                        if (partition[neighbor] != proposed || !members.Remove(neighbor))
                            continue;
                        refined[neighbor] = nextCommunity;
                        queue.Enqueue(neighbor);
                    }
                }
                nextCommunity++;
            }
        }
        return Normalize(refined);
    }

    private static double CalculateModularity(
        GraphLevel graph,
        IReadOnlyDictionary<string, int> assignments,
        double resolution)
    {
        if (graph.TotalEdgeWeight <= 0) return 0;
        var actorToNode = graph.Members
            .SelectMany((actors, node) => actors.Select(actor => (actor, node)))
            .ToDictionary(pair => pair.actor, pair => pair.node, StringComparer.Ordinal);
        var degreeByCommunity = new Dictionary<int, double>();
        foreach (var (actor, community) in assignments)
            degreeByCommunity[community] =
                degreeByCommunity.GetValueOrDefault(community) + graph.Degrees[actorToNode[actor]];

        var internalByCommunity = new Dictionary<int, double>();
        foreach (var (edge, weight) in graph.Edges)
        {
            var sourceActor = graph.Members[edge.Source].First();
            var targetActor = graph.Members[edge.Target].First();
            var sourceCommunity = assignments[sourceActor];
            if (sourceCommunity == assignments[targetActor])
                internalByCommunity[sourceCommunity] =
                    internalByCommunity.GetValueOrDefault(sourceCommunity) + weight;
        }

        return assignments.Values.Distinct().Sum(community =>
        {
            var internalWeight = internalByCommunity.GetValueOrDefault(community);
            var degree = degreeByCommunity.GetValueOrDefault(community);
            return internalWeight / graph.TotalEdgeWeight -
                   resolution * Math.Pow(degree / (2 * graph.TotalEdgeWeight), 2);
        });
    }

    private static int[] Normalize(int[] partition)
    {
        var mapping = partition
            .Select((community, node) => (community, node))
            .GroupBy(pair => pair.community)
            .OrderBy(group => group.Min(pair => pair.node))
            .Select((group, normalized) => (group.Key, normalized))
            .ToDictionary(pair => pair.Key, pair => pair.normalized);
        return partition.Select(community => mapping[community]).ToArray();
    }

    private static void Validate(CommunityDetectionOptions options)
    {
        if (!double.IsFinite(options.Resolution) || options.Resolution <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.Resolution));
        if (!double.IsFinite(options.MinimumEdgeWeight) || options.MinimumEdgeWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumEdgeWeight));
        if (options.MaxLevels < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaxLevels));
        if (options.MaxLocalMovingPasses < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaxLocalMovingPasses));
        if (!double.IsFinite(options.MinimumGain) || options.MinimumGain < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumGain));
    }

    private sealed class GraphLevel
    {
        private GraphLevel(
            IReadOnlyList<HashSet<string>> members,
            Dictionary<EdgeKey, double> edges)
        {
            Members = members;
            Edges = edges;
            NodeCount = members.Count;
            Adjacency = Enumerable.Range(0, NodeCount)
                .Select(_ => new Dictionary<int, double>())
                .ToArray();
            Degrees = new double[NodeCount];
            foreach (var (edge, weight) in edges)
            {
                if (edge.Source == edge.Target)
                {
                    Adjacency[edge.Source][edge.Target] =
                        Adjacency[edge.Source].GetValueOrDefault(edge.Target) + weight;
                    Degrees[edge.Source] += 2 * weight;
                }
                else
                {
                    Adjacency[edge.Source][edge.Target] =
                        Adjacency[edge.Source].GetValueOrDefault(edge.Target) + weight;
                    Adjacency[edge.Target][edge.Source] =
                        Adjacency[edge.Target].GetValueOrDefault(edge.Source) + weight;
                    Degrees[edge.Source] += weight;
                    Degrees[edge.Target] += weight;
                }
            }
            TotalEdgeWeight = edges.Values.Sum();
        }

        public int NodeCount { get; }
        public IReadOnlyList<HashSet<string>> Members { get; }
        public Dictionary<EdgeKey, double> Edges { get; }
        public Dictionary<int, double>[] Adjacency { get; }
        public double[] Degrees { get; }
        public double TotalEdgeWeight { get; }

        public static GraphLevel FromSnapshot(
            ActorGraphSnapshot snapshot,
            double minimumEdgeWeight)
        {
            var actors = snapshot.Actors.OrderBy(actor => actor.Id, StringComparer.Ordinal).ToArray();
            var indexes = actors
                .Select((actor, index) => (actor.Id, index))
                .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
            var edges = new Dictionary<EdgeKey, double>();
            foreach (var edge in snapshot.Edges.Where(edge => edge.Weight >= minimumEdgeWeight))
            {
                var key = EdgeKey.Create(indexes[edge.SourceId], indexes[edge.TargetId]);
                edges[key] = edges.GetValueOrDefault(key) + edge.Weight;
            }
            return new(
                actors.Select(actor => new HashSet<string>([actor.Id], StringComparer.Ordinal)).ToArray(),
                edges);
        }

        public GraphLevel Aggregate(int[] partition)
        {
            var communityCount = partition.Distinct().Count();
            var members = Enumerable.Range(0, communityCount)
                .Select(_ => new HashSet<string>(StringComparer.Ordinal))
                .ToArray();
            for (var node = 0; node < NodeCount; node++)
                members[partition[node]].UnionWith(Members[node]);

            var edges = new Dictionary<EdgeKey, double>();
            foreach (var (edge, weight) in Edges)
            {
                var aggregated = EdgeKey.Create(partition[edge.Source], partition[edge.Target]);
                edges[aggregated] = edges.GetValueOrDefault(aggregated) + weight;
            }
            return new(members, edges);
        }
    }

    private readonly record struct EdgeKey(int Source, int Target)
    {
        public static EdgeKey Create(int left, int right) =>
            left <= right ? new(left, right) : new(right, left);
    }
}
