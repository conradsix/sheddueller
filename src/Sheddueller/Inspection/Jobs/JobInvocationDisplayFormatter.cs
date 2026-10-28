namespace Sheddueller.Inspection.Jobs;

using System.Globalization;
using System.Text;
using System.Text.Json;

using Sheddueller.Storage;

internal static class JobInvocationDisplayFormatter
{
    public static string Format(
        string serviceType,
        string methodName,
        IReadOnlyList<JobInvocationParameterInspection> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(parameters);

        var handler = string.Create(CultureInfo.InvariantCulture, $"{ShortTypeName(serviceType)}.{methodName}");
        if (parameters.Count == 0)
        {
            return string.Concat(handler, "()");
        }

        if (parameters.Count <= 2)
        {
            return string.Create(
              CultureInfo.InvariantCulture,
              $"{handler}({string.Join(", ", parameters.Select(FormatArgument))})");
        }

        var builder = new StringBuilder();
        builder.Append(handler);
        builder.AppendLine("(");

        for (var i = 0; i < parameters.Count; i++)
        {
            builder.Append("    ");
            builder.Append(FormatArgument(parameters[i]));
            if (i < parameters.Count - 1)
            {
                builder.Append(',');
                builder.AppendLine();
            }
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string FormatArgument(JobInvocationParameterInspection parameter)
      => parameter.Binding.Kind switch
      {
          JobMethodParameterBindingKind.Serialized => FormatSerializedArgument(parameter),
          JobMethodParameterBindingKind.CancellationToken => "CancellationToken",
          JobMethodParameterBindingKind.JobContext => "Job.Context",
          JobMethodParameterBindingKind.ProgressReporter => "IProgress<Decimal>",
          JobMethodParameterBindingKind.Service => string.Create(
            CultureInfo.InvariantCulture,
            $"Job.Resolve<{ShortTypeName(parameter.Binding.ServiceType ?? parameter.ParameterType)}>()"),
          _ => string.Create(CultureInfo.InvariantCulture, $"<{parameter.Binding.Kind}>"),
      };

    private static string FormatSerializedArgument(JobInvocationParameterInspection parameter)
    {
        if (parameter.SerializedValueJson is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"<serialized {ShortTypeName(parameter.ParameterType)}>");
        }

        try
        {
            using var document = JsonDocument.Parse(parameter.SerializedValueJson);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => FormatStringLiteral(document.RootElement.GetString() ?? string.Empty),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => document.RootElement.GetRawText(),
                JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(document.RootElement),
                _ => string.Create(CultureInfo.InvariantCulture, $"<serialized {ShortTypeName(parameter.ParameterType)}>"),
            };
        }
        catch (JsonException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"<serialized {ShortTypeName(parameter.ParameterType)}>");
        }
    }

    internal static string FormatStringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var character in value)
        {
            _ = character switch
            {
                '"' => builder.Append("\\\""),
                '\\' => builder.Append("\\\\"),
                '\0' => builder.Append("\\0"),
                '\a' => builder.Append("\\a"),
                '\b' => builder.Append("\\b"),
                '\f' => builder.Append("\\f"),
                '\n' => builder.Append("\\n"),
                '\r' => builder.Append("\\r"),
                '\t' => builder.Append("\\t"),
                '\v' => builder.Append("\\v"),
                _ when char.IsControl(character) => builder.Append(
                    string.Create(CultureInfo.InvariantCulture, $"\\u{(int)character:X4}")),
                _ => builder.Append(character),
            };
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string ShortTypeName(string typeName)
    {
        var typeDelimiterIndex = typeName.IndexOf(',', StringComparison.Ordinal);
        if (typeDelimiterIndex >= 0)
        {
            typeName = typeName[..typeDelimiterIndex];
        }

        var separatorIndex = Math.Max(
          typeName.LastIndexOf('.'),
          typeName.LastIndexOf('+'));
        return separatorIndex < 0 || separatorIndex == typeName.Length - 1
          ? typeName
          : typeName[(separatorIndex + 1)..];
    }
}
