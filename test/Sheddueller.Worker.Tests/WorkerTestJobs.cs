namespace Sheddueller.Worker.Tests;

using Sheddueller.Serialization;
using Sheddueller.Storage;

internal static class WorkerTestJobs
{
    /// <summary>
    /// Creates a claimed job calling <typeparamref name="TJob"/>'s <paramref name="methodName"/> with only the
    /// scheduler cancellation token.
    /// </summary>
    public static ClaimedJob CreateClaimedJob<TJob>(string methodName)
      => CreateClaimedJob<TJob>(methodName, typeof(CancellationToken));

    /// <summary>
    /// Creates a claimed job calling <typeparamref name="TJob"/>'s <paramref name="methodName"/> with scheduler-owned
    /// arguments of the given types.
    /// </summary>
    public static ClaimedJob CreateClaimedJob<TJob>(string methodName, params Type[] parameterTypes)
      => new(
          Guid.NewGuid(),
          EnqueueSequence: 1,
          Priority: 0,
          ServiceType: typeof(TJob).AssemblyQualifiedName!,
          MethodName: methodName,
          MethodParameterTypes: [.. parameterTypes.Select(type => type.AssemblyQualifiedName!)],
          SerializedArguments: new SerializedJobPayload(SystemTextJsonJobPayloadSerializer.JsonContentType, "[]"u8.ToArray()),
          ConcurrencyGroupKeys: [],
          AttemptCount: 1,
          MaxAttempts: 1,
          LeaseToken: Guid.NewGuid(),
          LeaseExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(30),
          RetryBackoffKind: null,
          RetryBaseDelay: null,
          RetryMaxDelay: null,
          SourceScheduleKey: null,
          ScheduledFireAtUtc: null,
          MethodParameterBindings: [.. parameterTypes.Select(CreateBinding)]);

    private static JobMethodParameterBinding CreateBinding(Type parameterType)
      => parameterType == typeof(CancellationToken) ? new(JobMethodParameterBindingKind.CancellationToken)
        : parameterType == typeof(IProgress<decimal>) ? new(JobMethodParameterBindingKind.ProgressReporter)
        : throw new ArgumentException($"Test jobs take scheduler-owned arguments only, not '{parameterType}'.", nameof(parameterType));
}
