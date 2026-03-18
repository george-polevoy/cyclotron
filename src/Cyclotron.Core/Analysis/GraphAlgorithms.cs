namespace Cyclotron.Core.Analysis;

internal static class GraphAlgorithms
{
    public static IReadOnlyList<IReadOnlyList<string>> FindStronglyConnectedComponents(
        IReadOnlyDictionary<string, HashSet<string>> adjacency)
    {
        var components = new List<IReadOnlyList<string>>();
        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in adjacency.Keys)
        {
            if (!indices.ContainsKey(node))
            {
                Visit(node);
            }
        }

        return components;

        void Visit(string node)
        {
            indices[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var neighbor in adjacency[node])
            {
                if (!indices.ContainsKey(neighbor))
                {
                    Visit(neighbor);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[neighbor]);
                }
                else if (onStack.Contains(neighbor))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indices[neighbor]);
                }
            }

            if (lowLinks[node] != indices[node])
            {
                return;
            }

            var component = new List<string>();
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            }
            while (!string.Equals(current, node, StringComparison.Ordinal));

            components.Add(component);
        }
    }

    public static IReadOnlyDictionary<string, double> ComputeBetweennessCentrality(
        IReadOnlyDictionary<string, HashSet<string>> adjacency)
    {
        var nodes = adjacency.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var scores = nodes.ToDictionary(node => node, _ => 0d, StringComparer.Ordinal);

        foreach (var source in nodes)
        {
            var stack = new Stack<string>();
            var predecessors = nodes.ToDictionary(node => node, _ => new List<string>(), StringComparer.Ordinal);
            var pathCounts = nodes.ToDictionary(node => node, _ => 0d, StringComparer.Ordinal);
            var distances = nodes.ToDictionary(node => node, _ => -1, StringComparer.Ordinal);

            pathCounts[source] = 1d;
            distances[source] = 0;

            var queue = new Queue<string>();
            queue.Enqueue(source);

            while (queue.Count > 0)
            {
                var vertex = queue.Dequeue();
                stack.Push(vertex);

                foreach (var neighbor in adjacency[vertex])
                {
                    if (distances[neighbor] < 0)
                    {
                        distances[neighbor] = distances[vertex] + 1;
                        queue.Enqueue(neighbor);
                    }

                    if (distances[neighbor] == distances[vertex] + 1)
                    {
                        pathCounts[neighbor] += pathCounts[vertex];
                        predecessors[neighbor].Add(vertex);
                    }
                }
            }

            var dependency = nodes.ToDictionary(node => node, _ => 0d, StringComparer.Ordinal);

            while (stack.Count > 0)
            {
                var vertex = stack.Pop();
                foreach (var predecessor in predecessors[vertex])
                {
                    if (pathCounts[vertex] == 0)
                    {
                        continue;
                    }

                    dependency[predecessor] += (pathCounts[predecessor] / pathCounts[vertex]) * (1d + dependency[vertex]);
                }

                if (!string.Equals(vertex, source, StringComparison.Ordinal))
                {
                    scores[vertex] += dependency[vertex];
                }
            }
        }

        return scores;
    }
}
