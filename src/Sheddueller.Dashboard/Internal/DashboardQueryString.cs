namespace Sheddueller.Dashboard.Internal;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

/// <summary>
/// Reads and writes list-page state in the query string. The per-list filter queries build on these.
/// </summary>
internal static class DashboardQueryString
{
    public static Dictionary<string, StringValues> ParseUri(string uri)
      => QueryHelpers.ParseQuery(new Uri(uri, UriKind.Absolute).Query);

    /// <summary>
    /// The last value given for <paramref name="parameterName"/> that isn't blank, trimmed; <see langword="null" />
    /// when there is none.
    /// </summary>
    public static string? LastNonEmpty(
        Dictionary<string, StringValues> query,
        string parameterName)
    {
        if (!query.TryGetValue(parameterName, out var values))
        {
            return null;
        }

        for (var i = values.Count - 1; i >= 0; i--)
        {
            var candidate = values[i];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    public static bool IsTrue(
        Dictionary<string, StringValues> query,
        string parameterName)
      => LastNonEmpty(query, parameterName) is { } value
        && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
          || string.Equals(value, "1", StringComparison.Ordinal));

    /// <summary>
    /// A relative URL for <paramref name="path"/> with the given parameters, in order. Blank values are left out
    /// and the rest are trimmed.
    /// </summary>
    public static string Build(
        string path,
        IEnumerable<(string Name, string? Value)> parameters)
    {
        var queryParts = new List<string>();
        foreach (var (name, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            queryParts.Add(string.Concat(
              Uri.EscapeDataString(name),
              "=",
              Uri.EscapeDataString(value.Trim())));
        }

        return queryParts.Count == 0
          ? path
          : string.Concat(path, "?", string.Join("&", queryParts));
    }

    public static bool IsPath(
        string relativeUri,
        string path)
    {
        var queryIndex = relativeUri.IndexOfAny(['?', '#']);
        var relativePath = queryIndex < 0 ? relativeUri : relativeUri[..queryIndex];
        return string.Equals(relativePath.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase);
    }
}
