namespace Sheddueller.Dashboard.Internal;

using Sheddueller.Inspection.Nodes;

/// <summary>
/// The nodes list filters: the health state, read by the store, and node ID text, matched against the loaded nodes.
/// </summary>
internal sealed class DashboardNodeFilters
{
    public string State { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public NodeHealthState? StateFilter
      => Enum.TryParse<NodeHealthState>(this.State, out var state) ? state : null;

    public bool CopyFrom(DashboardNodeFilters other)
    {
        if (string.Equals(this.State, other.State, StringComparison.Ordinal)
            && string.Equals(this.NodeId, other.NodeId, StringComparison.Ordinal))
        {
            return false;
        }

        this.State = other.State;
        this.NodeId = other.NodeId;
        return true;
    }

    public IReadOnlyList<NodeInspectionSummary> ApplyClientFilter(IReadOnlyList<NodeInspectionSummary> nodes)
      => string.IsNullOrWhiteSpace(this.NodeId)
        ? nodes
        : [.. nodes.Where(node => node.NodeId.Contains(this.NodeId, StringComparison.OrdinalIgnoreCase))];

    public NodeInspectionQuery ToQuery(
        int pageSize,
        string? continuationToken)
      => new(this.StateFilter, pageSize, continuationToken);
}

/// <summary>
/// The nodes list's query string, and links to it.
/// </summary>
internal static class DashboardNodeFilterQuery
{
    public const string Path = "nodes";
    public const string StateParameter = "state";
    public const string NodeParameter = "node";

    public static DashboardNodeFilters ParseUri(string uri)
    {
        var query = DashboardQueryString.ParseUri(uri);
        var state = DashboardQueryString.LastNonEmpty(query, StateParameter);
        return new DashboardNodeFilters
        {
            State = Enum.TryParse<NodeHealthState>(state, ignoreCase: true, out var parsedState) && Enum.IsDefined(parsedState)
              ? parsedState.ToString()
              : string.Empty,
            NodeId = DashboardQueryString.LastNonEmpty(query, NodeParameter) ?? string.Empty,
        };
    }

    public static string ToHref(DashboardNodeFilters filters)
      => DashboardQueryString.Build(
        Path,
        [
            (StateParameter, filters.State),
            (NodeParameter, filters.NodeId),
        ]);

    /// <summary>
    /// The nodes list filtered to one node ID.
    /// </summary>
    public static string NodeHref(string nodeId)
      => ToHref(new DashboardNodeFilters { NodeId = nodeId });
}
