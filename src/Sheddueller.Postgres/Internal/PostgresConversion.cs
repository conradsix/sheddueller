namespace Sheddueller.Postgres.Internal;

using System.Globalization;

using Sheddueller.Serialization;
using Sheddueller.Storage;

internal static class PostgresConversion
{
    public static long? ToMilliseconds(TimeSpan? value)
      => value is null ? null : checked((long)value.Value.TotalMilliseconds);

    public static TimeSpan? FromMilliseconds(object value)
      => value is DBNull ? null : TimeSpan.FromMilliseconds(Convert.ToInt64(value, CultureInfo.InvariantCulture));

    public static string? ToText(RetryBackoffKind? value)
      => value?.ToString();

    public static RetryBackoffKind? ToRetryBackoffKind(object value)
      => value is DBNull ? null : Enum.Parse<RetryBackoffKind>((string)value);

    public static string ToText(JobState value)
      => value.ToString();

    public static JobState ToJobState(object value)
      => Enum.Parse<JobState>((string)value);

    public static string ToText(RecurringOverlapMode value)
      => value.ToString();

    public static RecurringOverlapMode ToRecurringOverlapMode(object value)
      => Enum.Parse<RecurringOverlapMode>((string)value);

    public static string ToText(JobEventKind value)
      => value.ToString();

    public static JobEventKind ToJobEventKind(object value)
      => Enum.Parse<JobEventKind>((string)value);

    public static string ToText(ScheduleOccurrenceKind value)
      => value.ToString();

    public static ScheduleOccurrenceKind? ToScheduleOccurrenceKind(object value)
      => value is DBNull ? null : Enum.Parse<ScheduleOccurrenceKind>((string)value);

    public static string ToText(JobInvocationTargetKind value)
      => value.ToString();

    public static JobInvocationTargetKind ToInvocationTargetKind(object value)
      => value is DBNull ? JobInvocationTargetKind.Instance : Enum.Parse<JobInvocationTargetKind>((string)value);

    public static string ToText(JobMethodParameterBinding value)
      => value.Kind == JobMethodParameterBindingKind.Service
        ? string.Concat(value.Kind.ToString(), "\n", value.ServiceType)
        : value.Kind.ToString();

    public static IReadOnlyList<JobMethodParameterBinding>? ToParameterBindings(object value)
    {
        if (value is DBNull)
        {
            return null;
        }

        var bindings = (string[])value;
        var result = new JobMethodParameterBinding[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            result[i] = ToParameterBinding(bindings[i]);
        }

        return result;
    }

    public static string ToText(JobLogLevel value)
      => value.ToString();

    public static JobLogLevel? ToJobLogLevel(object value)
      => value is DBNull ? null : Enum.Parse<JobLogLevel>((string)value);

    public static DateTimeOffset ToDateTimeOffset(object value)
      => value switch
      {
          DateTimeOffset dateTimeOffset => dateTimeOffset,
          DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
          _ => throw new InvalidOperationException($"Value of type '{value.GetType()}' is not a PostgreSQL timestamp."),
      };

    public static SerializedJobPayload ToPayload(object contentType, object data)
      => new((string)contentType, (byte[])data);

    public static RetryPolicy? ToRetryPolicy(bool configured, int maxAttempts, object backoffKind, object baseDelayMs, object maxDelayMs)
    {
        if (!configured || maxAttempts <= 1)
        {
            return null;
        }

        return new RetryPolicy(
          maxAttempts,
          Enum.Parse<RetryBackoffKind>((string)backoffKind),
          TimeSpan.FromMilliseconds(Convert.ToInt64(baseDelayMs, CultureInfo.InvariantCulture)),
          FromMilliseconds(maxDelayMs));
    }

    private static JobMethodParameterBinding ToParameterBinding(string value)
    {
        const string ServicePrefix = "Service\n";

        if (value.StartsWith(ServicePrefix, StringComparison.Ordinal))
        {
            return new JobMethodParameterBinding(
              JobMethodParameterBindingKind.Service,
              value[ServicePrefix.Length..]);
        }

        return new JobMethodParameterBinding(Enum.Parse<JobMethodParameterBindingKind>(value));
    }
}
