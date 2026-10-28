namespace Sheddueller.Worker.Internal;

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sheddueller;
using Sheddueller.Enqueueing;
using Sheddueller.Runtime;
using Sheddueller.Serialization;
using Sheddueller.Storage;

/// <summary>
/// Resolves a claimed job's target method, binds its arguments, and invokes it in a fresh service scope.
/// </summary>
internal sealed class JobInvoker(
    IServiceScopeFactory scopeFactory,
    ShedduellerJobLogEventQueue jobEventQueue,
    string nodeId,
    ILogger logger)
{
    private const BindingFlags StaticMethodBindingFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags InstanceMethodBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ShedduellerJobLogEventQueue _jobEventQueue = jobEventQueue;
    private readonly string _nodeId = nodeId;
    private readonly ILogger _logger = logger;

    // Looking types and methods up by name is slow, and a worker runs a small, fixed set of job methods. Failed lookups
    // throw before anything is cached, so they are retried by the next job.
    private readonly ConcurrentDictionary<string, Type> _types = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<JobMethodKey, JobMethod> _methods = new();

    public async ValueTask InvokeAsync(ClaimedJob job, CancellationToken executionToken)
    {
        var jobMethod = this._methods.GetOrAdd(new JobMethodKey(job), static (key, invoker) => invoker.ResolveJobMethod(key), this);
        var parameterBindings = JobMethodParameterBindingResolver.Normalize(jobMethod.ParameterTypes, job.MethodParameterBindings);
        var serializableParameterTypes = jobMethod.ParameterTypes
          .Where((_, index) => parameterBindings[index].Kind == JobMethodParameterBindingKind.Serialized)
          .ToArray();
        var jobContext = new JobContext(job.JobId, job.AttemptCount, executionToken);

        var scope = this._scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            using var loggingScope = this._logger.BeginScope(CreateJobLoggingScope(job, this._nodeId));
            using var logCapture = JobLogCaptureContext.Begin(job.JobId, job.AttemptCount);

            var target = job.InvocationTargetKind switch
            {
                JobInvocationTargetKind.Static => null,
                JobInvocationTargetKind.Instance => scope.ServiceProvider.GetRequiredService(jobMethod.ServiceType),
                _ => throw new InvalidOperationException($"Job invocation target kind '{job.InvocationTargetKind}' is not supported."),
            };
            var method = jobMethod.Method
              ?? throw new InvalidOperationException($"Could not resolve job method '{job.MethodName}' on service type '{jobMethod.ServiceType}'.");

            var deserializedArguments = await scope.ServiceProvider
                .GetRequiredService<IJobPayloadSerializer>()
                .DeserializeAsync(job.SerializedArguments, serializableParameterTypes, executionToken)
                .ConfigureAwait(false);
            var progressReporter = new DecimalJobProgressReporter(job.JobId, job.AttemptCount, this._jobEventQueue);
            var invocationArguments = this.BuildInvocationArguments(
              scope.ServiceProvider,
              jobMethod.ParameterTypes,
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
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
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

    private JobMethod ResolveJobMethod(JobMethodKey key)
    {
        var serviceType = this.ResolveType(key.ServiceType);
        var parameterTypes = key.MethodParameterTypes.Select(this.ResolveType).ToArray();
        var method = key.InvocationTargetKind switch
        {
            JobInvocationTargetKind.Static => serviceType.GetMethod(key.MethodName, StaticMethodBindingFlags, binder: null, parameterTypes, modifiers: null),
            JobInvocationTargetKind.Instance => serviceType.GetMethod(key.MethodName, InstanceMethodBindingFlags, binder: null, parameterTypes, modifiers: null),

            // Rejected before the method is needed.
            _ => null,
        };

        return new JobMethod(serviceType, parameterTypes, method);
    }

    private Type ResolveType(string typeName)
      => this._types.GetOrAdd(typeName, static typeName => TypeNameFormatter.Resolve(typeName));

    private static Dictionary<string, object?> CreateJobLoggingScope(
        ClaimedJob job,
        string nodeId)
      => new(StringComparer.Ordinal)
      {
          ["ShedduellerJobId"] = job.JobId,
          ["ShedduellerAttemptNumber"] = job.AttemptCount,
          ["ShedduellerNodeId"] = nodeId,
      };

    private object?[] BuildInvocationArguments(
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
                    invocationArguments[i] = this.ResolveBoundService(serviceProvider, methodParameterTypes[i], parameterBindings[i]);
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

    private object ResolveBoundService(
        IServiceProvider serviceProvider,
        Type parameterType,
        JobMethodParameterBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.ServiceType))
        {
            throw new InvalidOperationException("Service parameter binding did not include a service type.");
        }

        var serviceType = this.ResolveType(binding.ServiceType);
        if (!parameterType.IsAssignableFrom(serviceType))
        {
            throw new InvalidOperationException($"Resolved service type '{serviceType}' is not assignable to method parameter type '{parameterType}'.");
        }

        return serviceProvider.GetRequiredService(serviceType);
    }

    /// <summary>
    /// A job method's resolved service type, parameter types, and method; <see cref="Method"/> is null when no method
    /// matches.
    /// </summary>
    private sealed record JobMethod(
        Type ServiceType,
        Type[] ParameterTypes,
        MethodInfo? Method);

    /// <summary>
    /// Identifies a job method by the names a claimed job stores.
    /// </summary>
    private sealed class JobMethodKey(ClaimedJob job) : IEquatable<JobMethodKey>
    {
        public string ServiceType { get; } = job.ServiceType;

        public string MethodName { get; } = job.MethodName;

        public JobInvocationTargetKind InvocationTargetKind { get; } = job.InvocationTargetKind;

        public IReadOnlyList<string> MethodParameterTypes { get; } = job.MethodParameterTypes;

        public bool Equals(JobMethodKey? other)
          => other is not null
            && string.Equals(this.ServiceType, other.ServiceType, StringComparison.Ordinal)
            && string.Equals(this.MethodName, other.MethodName, StringComparison.Ordinal)
            && this.InvocationTargetKind == other.InvocationTargetKind
            && this.MethodParameterTypes.SequenceEqual(other.MethodParameterTypes, StringComparer.Ordinal);

        public override bool Equals(object? obj)
          => this.Equals(obj as JobMethodKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(this.ServiceType, StringComparer.Ordinal);
            hash.Add(this.MethodName, StringComparer.Ordinal);
            hash.Add(this.InvocationTargetKind);
            foreach (var parameterType in this.MethodParameterTypes)
            {
                hash.Add(parameterType, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }
}
