namespace Sheddueller.Worker.Internal;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sheddueller;
using Sheddueller.Enqueueing;
using Sheddueller.Runtime;
using Sheddueller.Serialization;
using Sheddueller.Storage;

internal sealed class JobInvoker(
    IServiceScopeFactory scopeFactory,
    IShedduellerNodeIdProvider nodeIdProvider,
    IJobEventSink jobEventSink,
    ILogger logger,
    ILogger<JobContext> jobContextLogger)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IShedduellerNodeIdProvider _nodeIdProvider = nodeIdProvider;
    private readonly IJobEventSink _jobEventSink = jobEventSink;
    private readonly ILogger _logger = logger;
    private readonly ILogger<JobContext> _jobContextLogger = jobContextLogger;

    public async ValueTask InvokeAsync(ClaimedJob job, CancellationToken executionToken)
    {
        var serviceType = TypeNameFormatter.Resolve(job.ServiceType);
        var methodParameterTypes = job.MethodParameterTypes.Select(TypeNameFormatter.Resolve).ToArray();
        var parameterBindings = NormalizeParameterBindings(methodParameterTypes, job.MethodParameterBindings);
        var serializableParameterTypes = methodParameterTypes
          .Where((_, index) => parameterBindings[index].Kind == JobMethodParameterBindingKind.Serialized)
          .ToArray();
        var jobContext = new JobContext(job.JobId, job.AttemptCount, executionToken);

        var scope = this._scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            using var loggingScope = this._logger.BeginScope(CreateJobLoggingScope(job, this._nodeIdProvider.NodeId));
            using var logCapture = JobLogCaptureContext.Begin(job.JobId, job.AttemptCount);

            var (target, bindingFlags) = job.InvocationTargetKind switch
            {
                JobInvocationTargetKind.Static => (
                  Target: null,
                  BindingFlags: System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                JobInvocationTargetKind.Instance => (
                  Target: scope.ServiceProvider.GetRequiredService(serviceType),
                  BindingFlags: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                _ => throw new InvalidOperationException($"Job invocation target kind '{job.InvocationTargetKind}' is not supported."),
            };
            var method = serviceType.GetMethod(
                job.MethodName,
                bindingFlags,
                binder: null,
                types: methodParameterTypes,
                modifiers: null);

            _ = method ?? throw new InvalidOperationException($"Could not resolve job method '{job.MethodName}' on service type '{serviceType}'.");

            var deserializedArguments = await scope.ServiceProvider
                .GetRequiredService<IJobPayloadSerializer>()
                .DeserializeAsync(job.SerializedArguments, serializableParameterTypes, executionToken)
                .ConfigureAwait(false);
            var progressReporter = new DecimalJobProgressReporter(
              job.JobId,
              job.AttemptCount,
              this._jobEventSink,
              this._jobContextLogger);
            var invocationArguments = BuildInvocationArguments(
              scope.ServiceProvider,
              methodParameterTypes,
              parameterBindings,
              deserializedArguments,
              jobContext,
              progressReporter,
              executionToken);
            object? result;

            try
            {
                result = method.Invoke(target, invocationArguments);
            }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }

            switch (result)
            {
                case Task taskResult:
                    await taskResult.ConfigureAwait(false);
                    break;
                case ValueTask valueTaskResult:
                    await valueTaskResult.ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("Job method returned an unsupported result.");
            }
        }
    }

    private static Dictionary<string, object?> CreateJobLoggingScope(
        ClaimedJob job,
        string nodeId)
      => new(StringComparer.Ordinal)
      {
          ["ShedduellerJobId"] = job.JobId,
          ["ShedduellerAttemptNumber"] = job.AttemptCount,
          ["ShedduellerNodeId"] = nodeId,
      };

    private static object?[] BuildInvocationArguments(
        IServiceProvider serviceProvider,
        Type[] methodParameterTypes,
        IReadOnlyList<JobMethodParameterBinding> parameterBindings,
        IReadOnlyList<object?> deserializedArguments,
        IJobContext jobContext,
        DecimalJobProgressReporter progressReporter,
        CancellationToken executionToken)
    {
        if (methodParameterTypes.Length != parameterBindings.Count)
        {
            throw new InvalidOperationException("Job parameter binding count did not match target method parameters.");
        }

        var invocationArguments = new object?[methodParameterTypes.Length];
        var deserializedIndex = 0;

        for (var i = 0; i < methodParameterTypes.Length; i++)
        {
            switch (parameterBindings[i].Kind)
            {
                case JobMethodParameterBindingKind.CancellationToken:
                    invocationArguments[i] = executionToken;
                    continue;

                case JobMethodParameterBindingKind.JobContext:
                    invocationArguments[i] = jobContext;
                    continue;

                case JobMethodParameterBindingKind.ProgressReporter:
                    invocationArguments[i] = GetProgressReporter(methodParameterTypes[i], progressReporter);
                    continue;

                case JobMethodParameterBindingKind.Service:
                    invocationArguments[i] = ResolveBoundService(serviceProvider, methodParameterTypes[i], parameterBindings[i]);
                    continue;

                case JobMethodParameterBindingKind.Serialized:
                    invocationArguments[i] = deserializedArguments[deserializedIndex];
                    deserializedIndex++;
                    continue;

                default:
                    throw new InvalidOperationException($"Job parameter binding kind '{parameterBindings[i].Kind}' is not supported.");
            }
        }

        if (deserializedIndex != deserializedArguments.Count)
        {
            throw new InvalidOperationException("Job payload argument count did not match target method parameters.");
        }

        return invocationArguments;
    }

    private static DecimalJobProgressReporter GetProgressReporter(
        Type parameterType,
        DecimalJobProgressReporter progressReporter)
    {
        if (parameterType != typeof(IProgress<decimal>))
        {
            throw new InvalidOperationException($"Progress reporter parameter type '{parameterType}' is not supported.");
        }

        return progressReporter;
    }

    private static object ResolveBoundService(
        IServiceProvider serviceProvider,
        Type parameterType,
        JobMethodParameterBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.ServiceType))
        {
            throw new InvalidOperationException("Service parameter binding did not include a service type.");
        }

        var serviceType = TypeNameFormatter.Resolve(binding.ServiceType);
        if (!parameterType.IsAssignableFrom(serviceType))
        {
            throw new InvalidOperationException($"Resolved service type '{serviceType}' is not assignable to method parameter type '{parameterType}'.");
        }

        return serviceProvider.GetRequiredService(serviceType);
    }

    private static IReadOnlyList<JobMethodParameterBinding> NormalizeParameterBindings(
        Type[] methodParameterTypes,
        IReadOnlyList<JobMethodParameterBinding>? parameterBindings)
      => JobMethodParameterBindingResolver.Normalize(methodParameterTypes, parameterBindings);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Job progress telemetry is best-effort by v4 design.")]
    private sealed class DecimalJobProgressReporter(
        Guid jobId,
        int attemptNumber,
        IJobEventSink eventSink,
        ILogger logger) : IProgress<decimal>
    {
        public void Report(decimal value)
        {
            var request = new AppendJobEventRequest(
              jobId,
              JobEventKind.Progress,
              attemptNumber,
              ProgressPercent: (double)Math.Clamp(value, 0, 100));

            try
            {
                eventSink.AppendAsync(request).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                logger.JobEventAppendFailed(exception, jobId);
            }
        }
    }

}
