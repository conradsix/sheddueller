namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;
using System.Text.Json;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Serialization;
using Sheddueller.Storage;

internal static class PostgresJobInvocationReader
{
    private const int SerializedArgumentDisplayByteLimit = 64 * 1024;

    private static readonly JsonSerializerOptions SerializedArgumentDisplayJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async ValueTask<JobInvocationInspection?> ReadInvocationAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        PostgresJobInspectionRow row,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select
              method_parameter_types,
              invocation_target_kind,
              method_parameter_bindings,
              serialized_arguments_content_type,
              serialized_arguments
          from {context.Names.Jobs}
          where job_id = @job_id;
          """;
        command.Parameters.AddWithValue("job_id", row.JobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var parameterTypes = reader.GetFieldValue<string[]>(0);
        var targetKind = PostgresConversion.ToInvocationTargetKind(reader.GetValue(1));
        var parameterBindings = JobMethodParameterBindingResolver.Normalize(
          parameterTypes,
          PostgresConversion.ToParameterBindings(reader.GetValue(2)));
        var payload = PostgresConversion.ToPayload(reader.GetValue(3), reader.GetValue(4));

        return CreateInvocation(row, targetKind, parameterTypes, parameterBindings, payload);
    }

    public static JobInvocationInspection CreateInvocation(
        PostgresJobInspectionRow row,
        JobInvocationTargetKind targetKind,
        string[] parameterTypes,
        IReadOnlyList<JobMethodParameterBinding> parameterBindings,
        SerializedJobPayload payload)
    {
        var serializedValueJson = new string?[parameterTypes.Length];
        var status = PopulateSerializedValueJson(payload, parameterBindings, serializedValueJson, out var statusMessage);
        var parameters = new JobInvocationParameterInspection[parameterTypes.Length];

        for (var i = 0; i < parameterTypes.Length; i++)
        {
            parameters[i] = new JobInvocationParameterInspection(
              i,
              parameterTypes[i],
              parameterBindings[i],
              serializedValueJson[i]);
        }

        var reconstructedCall = JobInvocationDisplayFormatter.Format(row.ServiceType, row.MethodName, parameters);

        return new JobInvocationInspection(
          targetKind,
          row.ServiceType,
          row.MethodName,
          reconstructedCall,
          parameters,
          payload.ContentType,
          payload.Data.LongLength,
          status,
          statusMessage);
    }

    public static JobSerializedArgumentsInspectionStatus PopulateSerializedValueJson(
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
