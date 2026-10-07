namespace Sheddueller.Enqueueing;

internal static class DependencyGraph
{
    // Callers validate membership first so their boundary-specific errors retain their ordering.
    public static void ValidateAcyclic<TNode>(
        IEnumerable<TNode> nodes,
        Func<TNode, IEnumerable<TNode>> prerequisites,
        IEqualityComparer<TNode> comparer,
        string parameterName)
        where TNode : notnull
    {
        var visiting = new HashSet<TNode>(comparer);
        var visited = new HashSet<TNode>(comparer);
        foreach (var node in nodes)
        {
            visit(node);
        }

        void visit(TNode node)
        {
            if (visited.Contains(node))
            {
                return;
            }

            if (!visiting.Add(node))
            {
                throw new ArgumentException("Job dependency graphs cannot contain cycles.", parameterName);
            }

            foreach (var prerequisite in prerequisites(node))
            {
                visit(prerequisite);
            }

            visiting.Remove(node);
            visited.Add(node);
        }
    }
}
