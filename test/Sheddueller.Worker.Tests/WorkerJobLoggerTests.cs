namespace Sheddueller.Worker.Tests;

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Sheddueller.Storage;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class WorkerJobLoggerTests
{
    [Fact]
    public async Task JobExecution_LoggerWritesDuringJob_RecordsDurableLogEvent()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = WorkerTestJobs.CreateClaimedJob<LoggingJob>(nameof(LoggingJob.RunAsync));
        var store = new SingleClaimJobStore(job);
        var eventSink = new RecordingJobEventSink();
        await using var provider = CreateProvider<LoggingJob>(
          store,
          eventSink,
          configureOptions: options => options.EnableJobLogCapture = true);
        var outsideLogger = provider.GetRequiredService<ILogger<LoggingJob>>();
        OutsideLog(outsideLogger, null);
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        eventSink.Requests.Any(request => HasEventId(request, "41")).ShouldBeFalse();
        var request = eventSink.Requests.Single(request => HasEventId(request, "42"));
        request.JobId.ShouldBe(job.JobId);
        request.Kind.ShouldBe(JobEventKind.Log);
        request.AttemptNumber.ShouldBe(job.AttemptCount);
        request.LogLevel.ShouldBe(JobLogLevel.Information);
        request.Message.ShouldBe("Processed item 123.");
        request.Fields.ShouldNotBeNull()["LoggerCategory"].ShouldBe(typeof(LoggingJob).FullName!.Replace('+', '.'));
        request.Fields["EventId"].ShouldBe("42");
        request.Fields["EventName"].ShouldBe("ProcessedItem");
        request.Fields["ItemId"].ShouldBe("123");
    }

    [Fact]
    public async Task JobExecution_LoggerWritesDuringJob_AddsJobMetadataScopeToExternalLoggers()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = WorkerTestJobs.CreateClaimedJob<LoggingJob>(nameof(LoggingJob.RunAsync));
        var store = new SingleClaimJobStore(job);
        var eventSink = new RecordingJobEventSink();
        using var logs = new ScopeRecordingLoggerProvider();
        await using var provider = CreateProvider<LoggingJob>(store, eventSink, loggerProvider: logs);
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        var entry = logs.Entries.Single(log => log.EventId.Id == 42);
        entry.ScopeProperties["ShedduellerJobId"].ShouldBe(job.JobId);
        entry.ScopeProperties["ShedduellerAttemptNumber"].ShouldBe(job.AttemptCount);
        entry.ScopeProperties["ShedduellerNodeId"].ShouldBe("worker-logger");
    }

    [Fact]
    public async Task JobExecution_LogCaptureDisabledByDefault_DoesNotRecordDurableLogEventButKeepsExternalScope()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = WorkerTestJobs.CreateClaimedJob<LoggingJob>(nameof(LoggingJob.RunAsync));
        var store = new SingleClaimJobStore(job);
        var eventSink = new RecordingJobEventSink();
        using var logs = new ScopeRecordingLoggerProvider();
        await using var provider = CreateProvider<LoggingJob>(store, eventSink, loggerProvider: logs);
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        eventSink.Requests.ShouldBeEmpty();
        var entry = logs.Entries.Single(log => log.EventId.Id == 42);
        entry.ScopeProperties["ShedduellerJobId"].ShouldBe(job.JobId);
        entry.ScopeProperties["ShedduellerAttemptNumber"].ShouldBe(job.AttemptCount);
        entry.ScopeProperties["ShedduellerNodeId"].ShouldBe("worker-logger");
    }

    [Fact]
    public async Task JobExecution_FireAndForgetLoggerAfterCompletion_DoesNotRecordDurableLogEvent()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = WorkerTestJobs.CreateClaimedJob<LateLoggingJob>(nameof(LateLoggingJob.RunAsync));
        var store = new SingleClaimJobStore(job);
        var eventSink = new RecordingJobEventSink();
        var coordinator = new LateLogCoordinator();
        await using var provider = CreateProvider<LateLoggingJob>(
          store,
          eventSink,
          coordinator,
          configureOptions: options => options.EnableJobLogCapture = true);
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        coordinator.AllowLateLog.SetResult();
        await coordinator.LateLogWritten.Task.WaitAsync(cancellationTokenSource.Token);
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        eventSink.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task JobExecution_LoggerSinkFails_StillCompletesJob()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = WorkerTestJobs.CreateClaimedJob<LoggingJob>(nameof(LoggingJob.RunAsync));
        var store = new SingleClaimJobStore(job);
        await using var provider = CreateProvider<LoggingJob>(
          store,
          new ThrowingJobEventSink(),
          configureOptions: options => options.EnableJobLogCapture = true);
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        var completed = await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        completed.JobId.ShouldBe(job.JobId);
    }

    [Fact]
    public async Task JobExecution_LoggerFormatterFails_StillCompletesJob()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = WorkerTestJobs.CreateClaimedJob<ThrowingFormatterJob>(nameof(ThrowingFormatterJob.RunAsync));
        var store = new SingleClaimJobStore(job);
        await using var provider = CreateProvider<ThrowingFormatterJob>(
          store,
          new RecordingJobEventSink(),
          configureOptions: options => options.EnableJobLogCapture = true);
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        var completed = await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        completed.JobId.ShouldBe(job.JobId);
    }

    [Fact]
    public async Task JobExecution_LoggerSinkBlocks_StillCompletesJob()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = WorkerTestJobs.CreateClaimedJob<LoggingJob>(nameof(LoggingJob.RunAsync));
        var store = new SingleClaimJobStore(job);
        var eventSink = new BlockingJobEventSink();
        await using var provider = CreateProvider<LoggingJob>(
          store,
          eventSink,
          configureOptions: options => options.EnableJobLogCapture = true);
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        var completed = await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        eventSink.AllowAppend.SetResult();
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        completed.JobId.ShouldBe(job.JobId);
    }

    [Fact]
    public async Task LogDispatcher_StoppedBeforeDispatchLoopRuns_FlushesQueuedEvents()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // BackgroundService runs ExecuteAsync on the thread pool and skips it when stopped before it starts. An immediate
        // stop lands there most of the time; repeat so the test doesn't depend on that.
        for (var i = 0; i < 20; i++)
        {
            var queue = new ShedduellerJobLogEventQueue();
            var eventSink = new RecordingJobEventSink();
            var request = new AppendJobEventRequest(Guid.NewGuid(), JobEventKind.Log, AttemptNumber: 1, JobLogLevel.Information, "queued");
            queue.TryEnqueue(request).ShouldBeTrue();
            using var dispatcher = new ShedduellerJobLogEventDispatcher(
              queue,
              eventSink,
              TimeProvider.System,
              NullLogger<ShedduellerJobLogEventDispatcher>.Instance);

            await dispatcher.StartAsync(cancellationTokenSource.Token);
            await dispatcher.StopAsync(cancellationTokenSource.Token);

            eventSink.Requests.ShouldHaveSingleItem().ShouldBe(request);
        }
    }

    private static ServiceProvider CreateProvider<TJob>(
        SingleClaimJobStore store,
        IJobEventSink eventSink,
        LateLogCoordinator? coordinator = null,
        ILoggerProvider? loggerProvider = null,
        Action<ShedduellerOptions>? configureOptions = null)
      where TJob : class
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        services.AddSingleton(eventSink);
        services.AddSingleton<IJobStore>(store);
        if (coordinator is not null)
        {
            services.AddSingleton(coordinator);
        }

        services.AddTransient<TJob>();
        services.AddShedduellerWorker(builder => builder.ConfigureOptions(options =>
        {
            options.NodeId = "worker-logger";
            options.IdlePollingInterval = TimeSpan.FromMilliseconds(10);
            options.HeartbeatInterval = TimeSpan.FromSeconds(5);
            options.LeaseDuration = TimeSpan.FromSeconds(30);
            configureOptions?.Invoke(options);
        }));

        return services.BuildServiceProvider();
    }

    private static bool HasEventId(AppendJobEventRequest request, string eventId)
      => request.Fields is not null
        && request.Fields.TryGetValue("EventId", out var actualEventId)
        && string.Equals(actualEventId, eventId, StringComparison.Ordinal);

    private static readonly Action<ILogger, Exception?> OutsideLog =
      LoggerMessage.Define(
        LogLevel.Information,
        new EventId(41, nameof(OutsideLog)),
        "outside log");

    private static readonly Action<ILogger, int, Exception?> ProcessedItem =
      LoggerMessage.Define<int>(
        LogLevel.Information,
        new EventId(42, nameof(ProcessedItem)),
        "Processed item {ItemId}.");

    private static readonly Action<ILogger, Exception?> LateLog =
      LoggerMessage.Define(
        LogLevel.Information,
        new EventId(43, nameof(LateLog)),
        "late log");

    private sealed class LoggingJob(ILogger<LoggingJob> logger)
    {
        public Task RunAsync(CancellationToken cancellationToken)
        {
            ProcessedItem(logger, 123, null);
            return Task.CompletedTask;
        }
    }

    private sealed class LateLoggingJob(
        ILogger<LateLoggingJob> logger,
        LateLogCoordinator coordinator)
    {
        public Task RunAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                await coordinator.AllowLateLog.Task.ConfigureAwait(false);
                LateLog(logger, null);
                coordinator.LateLogWritten.SetResult();
            }, CancellationToken.None);

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingFormatterJob(ILogger<ThrowingFormatterJob> logger)
    {
        public Task RunAsync(CancellationToken cancellationToken)
        {
            logger.Log<object?>(
              LogLevel.Information,
              new EventId(44, nameof(ThrowingFormatterJob)),
              state: null,
              exception: null,
              formatter: static (_, _) => throw new InvalidOperationException("formatter failed"));

            return Task.CompletedTask;
        }
    }

    private sealed class LateLogCoordinator
    {
        public TaskCompletionSource AllowLateLog { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LateLogWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ThrowingJobEventSink : IJobEventSink
    {
        public ValueTask<JobEvent> AppendAsync(
            AppendJobEventRequest request,
            CancellationToken cancellationToken = default)
          => throw new InvalidOperationException("append failed");
    }

    private sealed class ScopeRecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<ScopeLogEntry> _entries = new();
        private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

        public IReadOnlyList<ScopeLogEntry> Entries
          => [.. this._entries];

        public ILogger CreateLogger(string categoryName)
          => new ScopeRecordingLogger(categoryName, this);

        public void Dispose()
        {
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
          => this._scopeProvider = scopeProvider;

        private void Add<TState>(
            string categoryName,
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this._entries.Enqueue(new ScopeLogEntry(
              categoryName,
              logLevel,
              eventId,
              formatter(state, exception),
              ReadScopeProperties(this._scopeProvider)));
        }

        private static Dictionary<string, object?> ReadScopeProperties(IExternalScopeProvider scopeProvider)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            scopeProvider.ForEachScope(static (scope, state) =>
            {
                if (scope is not IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    return;
                }

                foreach (var (key, value) in pairs)
                {
                    state[key] = value;
                }
            }, properties);

            return properties;
        }

        private sealed class ScopeRecordingLogger(
            string categoryName,
            ScopeRecordingLoggerProvider provider) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
              where TState : notnull
              => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel)
              => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
              => provider.Add(categoryName, logLevel, eventId, state, exception, formatter);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record ScopeLogEntry(
        string CategoryName,
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, object?> ScopeProperties);
}
