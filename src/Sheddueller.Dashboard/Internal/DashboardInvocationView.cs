namespace Sheddueller.Dashboard.Internal;

using System.Globalization;
using System.Text;
using System.Text.Json;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

/// <summary>
/// One argument of a job's method call: its stored value plus the parameter name when it could be resolved.
/// </summary>
internal sealed record DashboardInvocationArgument(
    int Index,
    string? Name,
    string ParameterType,
    JobMethodParameterBinding Binding,
    string? ValueJson)
{
    /// <summary>
    /// Plain data supplied by the caller, as opposed to arguments the scheduler provides (cancellation token,
    /// progress reporter, job context, injected services).
    /// </summary>
    public bool IsData
      => this.Binding.Kind == JobMethodParameterBindingKind.Serialized;
}

/// <summary>
/// Builds the highlighted method call and argument values shown on the job page.
/// </summary>
internal static class DashboardInvocationView
{
    private const string Ellipsis = "…";

    public static IReadOnlyList<DashboardInvocationArgument> Arguments(
        JobInvocationInspection invocation,
        IReadOnlyList<string>? parameterNames)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        return
        [
            .. invocation.Parameters
              .OrderBy(parameter => parameter.ParameterIndex)
              .Select(parameter => new DashboardInvocationArgument(
                parameter.ParameterIndex,
                parameterNames is not null && parameter.ParameterIndex >= 0 && parameter.ParameterIndex < parameterNames.Count
                  ? parameterNames[parameter.ParameterIndex]
                  : null,
                parameter.ParameterType,
                parameter.Binding,
                parameter.SerializedValueJson)),
        ];
    }

    /// <summary>
    /// The whole call, e.g. <c>RateLoader.LoadRates(propertyId: 5, from: "2026-11-01")</c>, as highlighted tokens.
    /// The receiver is the type the method is called on, or <see langword="null" /> for the bare method name.
    /// </summary>
    public static IReadOnlyList<CodeToken> CallTokens(
        string? receiver,
        string methodName,
        IReadOnlyList<DashboardInvocationArgument> arguments,
        bool valuesAvailable,
        bool dataOnly,
        int? maxValueLength,
        bool oneArgumentPerLine)
    {
        var shown = Shown(arguments, dataOnly);
        var tokens = new List<CodeToken>();
        if (receiver is not null)
        {
            tokens.Add(new(receiver, CodeTokenKind.Type));
            tokens.Add(new(".", CodeTokenKind.Punctuation));
        }

        tokens.Add(new(methodName, CodeTokenKind.Method));
        tokens.Add(new("(", CodeTokenKind.Punctuation));

        for (var index = 0; index < shown.Count; index++)
        {
            if (oneArgumentPerLine)
            {
                tokens.Add(new("\n    ", CodeTokenKind.Plain));
            }
            else if (index > 0)
            {
                tokens.Add(new(" ", CodeTokenKind.Plain));
            }

            tokens.AddRange(ArgumentTokens(shown[index], valuesAvailable, maxValueLength, trailingComma: index < shown.Count - 1));
        }

        if (oneArgumentPerLine && shown.Count > 0)
        {
            tokens.Add(new("\n", CodeTokenKind.Plain));
        }

        tokens.Add(new(")", CodeTokenKind.Punctuation));
        return tokens;
    }

    /// <summary>
    /// The arguments as separate token groups (each with its trailing comma), so a layout can wrap between them.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<CodeToken>> ArgumentGroups(
        IReadOnlyList<DashboardInvocationArgument> arguments,
        bool valuesAvailable,
        bool dataOnly,
        int? maxValueLength)
    {
        var shown = Shown(arguments, dataOnly);
        return
        [
            .. shown.Select((argument, index) =>
              ArgumentTokens(argument, valuesAvailable, maxValueLength, trailingComma: index < shown.Count - 1)),
        ];
    }

    /// <summary>
    /// The call as plain text with full values, for copying.
    /// </summary>
    public static string CallText(
        string? receiver,
        string methodName,
        IReadOnlyList<DashboardInvocationArgument> arguments,
        bool valuesAvailable,
        bool dataOnly)
    {
        var shown = Shown(arguments, dataOnly);
        var tokens = CallTokens(receiver, methodName, shown, valuesAvailable, dataOnly: false, maxValueLength: null, oneArgumentPerLine: shown.Count > 2);
        return Text(tokens);
    }

    public static string Text(IEnumerable<CodeToken> tokens)
      => string.Concat(tokens.Select(token => token.Text));

    /// <summary>
    /// A JSON value pretty-printed and split into highlighted tokens. Invalid JSON is returned as plain text.
    /// </summary>
    public static IReadOnlyList<CodeToken> JsonTokens(string? json, bool indented = true)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var tokens = new List<CodeToken>();
            AppendJson(tokens, document.RootElement, depth: 0, indented);
            return tokens;
        }
        catch (JsonException)
        {
            return [new(json, CodeTokenKind.Plain)];
        }
    }

    private static List<DashboardInvocationArgument> Shown(IReadOnlyList<DashboardInvocationArgument> arguments, bool dataOnly)
      => dataOnly ? [.. arguments.Where(argument => argument.IsData)] : [.. arguments];

    private static List<CodeToken> ArgumentTokens(
        DashboardInvocationArgument argument,
        bool valuesAvailable,
        int? maxValueLength,
        bool trailingComma)
    {
        var tokens = new List<CodeToken>();
        if (argument.Name is { } name)
        {
            tokens.Add(new(name, argument.IsData ? CodeTokenKind.Parameter : CodeTokenKind.Muted));
            tokens.Add(new(":", argument.IsData ? CodeTokenKind.Punctuation : CodeTokenKind.Muted));
            tokens.Add(new(" ", CodeTokenKind.Plain));
        }

        tokens.AddRange(Truncate(ValueTokens(argument, valuesAvailable), maxValueLength));
        if (trailingComma)
        {
            tokens.Add(new(",", CodeTokenKind.Punctuation));
        }

        return tokens;
    }

    private static List<CodeToken> ValueTokens(DashboardInvocationArgument argument, bool valuesAvailable)
    {
        if (!argument.IsData)
        {
            return [new(SchedulerSuppliedValue(argument), CodeTokenKind.Muted)];
        }

        if (!valuesAvailable || argument.ValueJson is null)
        {
            return [new(Ellipsis, CodeTokenKind.Muted)];
        }

        try
        {
            using var document = JsonDocument.Parse(argument.ValueJson);
            var root = document.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.String => [new(JobInvocationDisplayFormatter.FormatStringLiteral(root.GetString() ?? string.Empty), CodeTokenKind.StringLiteral)],
                JsonValueKind.Number => [new(root.GetRawText(), CodeTokenKind.NumberLiteral)],
                JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => [new(root.GetRawText(), CodeTokenKind.Keyword)],
                _ => [.. JsonTokens(argument.ValueJson, indented: false)],
            };
        }
        catch (JsonException)
        {
            return [new(Ellipsis, CodeTokenKind.Muted)];
        }
    }

    private static string SchedulerSuppliedValue(DashboardInvocationArgument argument)
      => argument.Binding.Kind switch
      {
          JobMethodParameterBindingKind.CancellationToken => "CancellationToken",
          JobMethodParameterBindingKind.JobContext => "Job.Context",
          JobMethodParameterBindingKind.ProgressReporter => "IProgress<Decimal>",
          JobMethodParameterBindingKind.Service => string.Create(
            CultureInfo.InvariantCulture,
            $"Job.Resolve<{DashboardFormat.ShortTypeName(argument.Binding.ServiceType ?? argument.ParameterType)}>()"),
          _ => DashboardFormat.ShortTypeName(argument.ParameterType),
      };

    // Cuts a value to at most maxLength characters (plus an ellipsis), keeping the highlighting of what remains.
    private static List<CodeToken> Truncate(List<CodeToken> tokens, int? maxLength)
    {
        if (maxLength is not { } limit || tokens.Sum(token => token.Text.Length) <= limit)
        {
            return tokens;
        }

        var result = new List<CodeToken>();
        var remaining = limit;
        foreach (var token in tokens)
        {
            if (token.Text.Length <= remaining)
            {
                result.Add(token);
                remaining -= token.Text.Length;
                continue;
            }

            if (remaining > 0)
            {
                result.Add(token with { Text = token.Text[..remaining] });
            }

            break;
        }

        result.Add(new(Ellipsis, CodeTokenKind.Muted));
        return result;
    }

    private static void AppendJson(List<CodeToken> tokens, JsonElement element, int depth, bool indented)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                AppendContainer(
                  tokens,
                  "{",
                  "}",
                  [.. element.EnumerateObject()],
                  depth,
                  indented,
                  (property, childDepth) =>
                  {
                      tokens.Add(new(JsonSerializer.Serialize(property.Name), CodeTokenKind.Property));
                      tokens.Add(new(":", CodeTokenKind.Punctuation));
                      if (indented)
                      {
                          tokens.Add(new(" ", CodeTokenKind.Plain));
                      }

                      AppendJson(tokens, property.Value, childDepth, indented);
                  });
                break;
            case JsonValueKind.Array:
                AppendContainer(
                  tokens,
                  "[",
                  "]",
                  [.. element.EnumerateArray()],
                  depth,
                  indented,
                  (item, childDepth) => AppendJson(tokens, item, childDepth, indented));
                break;
            case JsonValueKind.String:
                tokens.Add(new(element.GetRawText(), CodeTokenKind.StringLiteral));
                break;
            case JsonValueKind.Number:
                tokens.Add(new(element.GetRawText(), CodeTokenKind.NumberLiteral));
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                tokens.Add(new(element.GetRawText(), CodeTokenKind.Keyword));
                break;
            default:
                tokens.Add(new(element.GetRawText(), CodeTokenKind.Plain));
                break;
        }
    }

    private static void AppendContainer<T>(
        List<CodeToken> tokens,
        string open,
        string close,
        IReadOnlyList<T> items,
        int depth,
        bool indented,
        Action<T, int> appendItem)
    {
        tokens.Add(new(open, CodeTokenKind.Punctuation));
        if (items.Count == 0)
        {
            tokens.Add(new(close, CodeTokenKind.Punctuation));
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (indented)
            {
                tokens.Add(new(Indent(depth + 1), CodeTokenKind.Plain));
            }

            appendItem(items[index], depth + 1);
            if (index < items.Count - 1)
            {
                tokens.Add(new(",", CodeTokenKind.Punctuation));
            }
        }

        if (indented)
        {
            tokens.Add(new(Indent(depth), CodeTokenKind.Plain));
        }

        tokens.Add(new(close, CodeTokenKind.Punctuation));
    }

    private static string Indent(int depth)
      => new StringBuilder("\n").Append(' ', depth * 2).ToString();
}
