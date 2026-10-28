namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;
using System.Text.Json;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Serialization;
using Sheddueller.Storage;

/// <summary>
/// Turns a job's stored invocation into the inspection view of it: each parameter with its binding and, where the
/// payload can be shown, its serialized value.
/// </summary>
/// <remarks>
/// Pure: it reads no database state, so it is tested without one.
/// </remarks>
internal static class PostgresJobInvocationFormatter
{
    /// <summary>
    /// The largest serialized arguments payload that inspection parses for display.
    /// </summary>
    public const int SerializedArgumentDisplayByteLimit = 64 * 1024;

    private static readonly JsonSerializerOptions SerializedArgumentDisplayJsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <param name="targetKind">Whether the job invokes an instance or a static method.</param>
    /// <param name="serviceType">The type the job's method is invoked on.</param>
    /// <param name="methodName">The job's method.</param>
    /// <param name="parameterTypes">The method's parameter types, in order.</param>
    /// <param name="storedParameterBindings">
    /// The stored parameter bindings, or null for a job stored before bindings were recorded, whose bindings are
    /// inferred from <paramref name="parameterTypes" />.
    /// </param>
    /// <param name="payload">The serialized arguments of the job's serialized parameters.</param>
    public static JobInvocationInspection Format(
        JobInvocationTargetKind targetKind,
        string serviceType,
        string methodName,
        IReadOnlyList<string> parameterTypes,
        IReadOnlyList<JobMethodParameterBinding>? storedParameterBindings,
        SerializedJobPayload payload)
    {
        var parameterBindings = JobMethodParameterBindingResolver.Normalize(parameterTypes, storedParameterBindings);
        var serializedValueJson = new string?[parameterTypes.Count];
        var status = PopulateSerializedValueJson(payload, parameterBindings, serializedValueJson, out var statusMessage);
        var parameters = new JobInvocationParameterInspection[parameterTypes.Count];

        for (var i = 0; i < parameterTypes.Count; i++)
        {
            parameters[i] = new JobInvocationParameterInspection(
              i,
              parameterTypes[i],
              parameterBindings[i],
              serializedValueJson[i]);
        }

        var reconstructedCall = JobInvocationDisplayFormatter.Format(serviceType, methodName, parameters);

        return new JobInvocationInspection(
          targetKind,
          serviceType,
          methodName,
          reconstructedCall,
          parameters,
          payload.ContentType,
          payload.Data.LongLength,
          status,
          statusMessage);
    }

    private static JobSerializedArgumentsInspectionStatus PopulateSerializedValueJson(
        SerializedJobPayload payload,
        IReadOnlyList<JobMethodParameterBinding> parameterBindings,
        string?[] serializedValueJson,
        out string? statusMessage)
    {
        if (!string.Equals(payload.ContentType, SystemTextJsonJobPayloadSerializer.JsonContentType, StringComparison.Ordinal))
        {
            statusMessage = string.Create(
              CultureInfo.InvariantCulture,
              $"Serialized arguments use unsupported content type '{payload.ContentType}'.");
            return JobSerializedArgumentsInspectionStatus.UnsupportedContentType;
        }

        if (payload.Data.LongLength > SerializedArgumentDisplayByteLimit)
        {
            statusMessage = string.Create(
              CultureInfo.InvariantCulture,
              $"Serialized arguments are {payload.Data.LongLength:N0} bytes, exceeding the {SerializedArgumentDisplayByteLimit:N0} byte display limit.");
            return JobSerializedArgumentsInspectionStatus.TooLarge;
        }

        var serializedParameterIndexes = parameterBindings
          .Select((binding, index) => (binding, index))
          .Where(parameter => parameter.binding.Kind == JobMethodParameterBindingKind.Serialized)
          .Select(parameter => parameter.index)
          .ToArray();

        try
        {
            using var document = JsonDocument.Parse(payload.Data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                statusMessage = "Serialized arguments payload is not a JSON array.";
                return JobSerializedArgumentsInspectionStatus.InvalidPayload;
            }

            if (root.GetArrayLength() != serializedParameterIndexes.Length)
            {
                statusMessage = string.Create(
                  CultureInfo.InvariantCulture,
                  $"Serialized arguments contain {root.GetArrayLength()} values for {serializedParameterIndexes.Length} serialized parameters.");
                return JobSerializedArgumentsInspectionStatus.ArgumentCountMismatch;
            }

            var argumentIndex = 0;
            foreach (var element in root.EnumerateArray())
            {
                serializedValueJson[serializedParameterIndexes[argumentIndex]] = JsonSerializer.Serialize(element, SerializedArgumentDisplayJsonOptions);
                argumentIndex++;
            }

            statusMessage = null;
            return JobSerializedArgumentsInspectionStatus.Displayable;
        }
        catch (JsonException)
        {
            statusMessage = "Serialized arguments payload is not valid JSON.";
            return JobSerializedArgumentsInspectionStatus.InvalidPayload;
        }
    }
}
