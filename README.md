# Sheddueller

**_Sheddueller_** - a .NET 10 task scheduler for applications that need durable background jobs, delayed work, recurring schedules, retries, dynamic concurrency groups, and a small operational dashboard.

**Sheddueller** - someone who duels with sheds. Think _The Big Lebowski_, but with a PC instead of a White Russian, sword-fighting a small and meaner version of _Howl's Moving Castle_.

![Sheddueller hero](https://raw.githubusercontent.com/conradsix/sheddueller/38186df123ce54ce1e3d476c866087a7b7333278/assets/hero.png)

## Motivation

I like [Hangfire](https://www.hangfire.io/). I've used it in a lot of projects. It sits in that pragmatic small-team space between rolling your own background services and cloudslop enterprise hell.

If what you need is fire-and-forget jobs, delayed jobs, retries, and a useful dashboard, Hangfire is a solid option. I've got a long way with it. The thing I needed for my current project, though, was **concurrency groups**.

A concurrency group is exactly what it sounds like: a group with a maximum number of concurrent jobs. You assign a job to one or more concurrency groups, and that job can only be claimed by a worker when there is a free slot in every group to which it belongs.

In my case, I am ingesting vacation rental data from various sources. Lots of these sources are rate limited, so you can't just enqueue hundreds of data refreshes against them and hope for the best. They will ban you, throttle you, or send a nasty email (if they are French). It's a primitive industry.

By putting all ingestion jobs for a specific API into a single concurrency group, I can ensure that no matter how many workers are running in the cluster, the number of concurrent fetches against that API is bounded by a number I've specified.

[Laravel Horizon](https://laravel.com/docs/13.x/horizon) has a similar concept, expressed through queues, and it is very useful in practice.

Obviously the `Ghost in a Bottle` has changed things. What would normally be a multi-month nightmare project that I simply couldn't justify becomes a short hack. So Sheddueller also includes a few other things I find annoying when working with background jobs, like not being able to search for jobs easily in the dashboard, and not being able to add diagnostic logging directly to the job view. Sometimes you just want the logs next to the thing that failed. Revolutionary stuff.

This package has not yet been stress tested or battle hardened, so use with caution. I would advise against using it in important production systems until it has been put through its paces.

## Packages

| Package                 | NuGet                                                  | Use it for                                                                                   |
| ----------------------- | ------------------------------------------------------ | -------------------------------------------------------------------------------------------- |
| `Sheddueller`           | [![NuGet][nuget-sheddueller-badge]][nuget-sheddueller] | Core enqueueing, schedule management, runtime options, and abstractions.                     |
| `Sheddueller.Postgres`  | [![NuGet][nuget-postgres-badge]][nuget-postgres]       | PostgreSQL-backed storage, wake signals, inspection readers, and schema migrations.          |
| `Sheddueller.Worker`    | [![NuGet][nuget-worker-badge]][nuget-worker]           | Hosted worker execution loop for nodes that should claim and run jobs.                       |
| `Sheddueller.Dashboard` | [![NuGet][nuget-dashboard-badge]][nuget-dashboard]     | Embedded ASP.NET Core dashboard for jobs, schedules, nodes, metrics, and concurrency groups. |
| `Sheddueller.Testing`   | [![NuGet][nuget-testing-badge]][nuget-testing]         | Test fakes and capture helpers.                                                              |

[nuget-sheddueller-badge]: https://img.shields.io/nuget/vpre/Sheddueller
[nuget-sheddueller]: https://www.nuget.org/packages/Sheddueller/
[nuget-postgres-badge]: https://img.shields.io/nuget/vpre/Sheddueller.Postgres
[nuget-postgres]: https://www.nuget.org/packages/Sheddueller.Postgres/
[nuget-worker-badge]: https://img.shields.io/nuget/vpre/Sheddueller.Worker
[nuget-worker]: https://www.nuget.org/packages/Sheddueller.Worker/
[nuget-dashboard-badge]: https://img.shields.io/nuget/vpre/Sheddueller.Dashboard
[nuget-dashboard]: https://www.nuget.org/packages/Sheddueller.Dashboard/
[nuget-testing-badge]: https://img.shields.io/nuget/vpre/Sheddueller.Testing
[nuget-testing]: https://www.nuget.org/packages/Sheddueller.Testing/

## Install

```bash
dotnet add package Sheddueller
dotnet add package Sheddueller.Postgres
dotnet add package Sheddueller.Worker
```

For a web dashboard:

```bash
dotnet add package Sheddueller.Dashboard
```

For tests:

```bash
dotnet add package Sheddueller.Testing
```

## Configure

Register `AddSheddueller(...)` in processes that only submit work or manage schedules. Register `AddShedduellerWorker(...)` in processes that should also execute jobs.

`WorkerOptions` below is an application options type; the callback can read any service registered with DI.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sheddueller;
using Sheddueller.Postgres;

builder.Services.Configure<WorkerOptions>(
    builder.Configuration.GetSection("Worker"));
builder.Services.AddTransient<EmailJobs>();

builder.Services.AddShedduellerWorker(sheddueller => sheddueller
    .UsePostgres(
        serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            return configuration.GetConnectionString("Sheddueller")
                ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:Sheddueller' is required.");
        },
        (serviceProvider, postgres) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            postgres.SchemaName = configuration["Sheddueller:Postgres:SchemaName"] ?? "sheddueller";
        })
    .ConfigureOptions((serviceProvider, options) =>
    {
        var worker = serviceProvider.GetRequiredService<IOptions<WorkerOptions>>().Value;
        options.NodeId = Environment.MachineName;
        options.MaxConcurrentExecutionsPerNode = worker.MaxConcurrentExecutions;
        options.DefaultRetryPolicy = new RetryPolicy(
            MaxAttempts: 3,
            BackoffKind: RetryBackoffKind.Exponential,
            BaseDelay: TimeSpan.FromSeconds(5),
            MaxDelay: TimeSpan.FromMinutes(1));
    }));
```

Schema migrations are explicit:

```csharp
await app.ApplyShedduellerPostgresMigrationsAsync();
```

Run migrations during deployment or before starting workers against a new schema. Normal startup validates the configured provider; it does not silently create the schema.

Use `UsePostgres(postgres => postgres.DataSource = dataSource)` when an application needs to share or own a prebuilt `NpgsqlDataSource`; in that mode, the application also owns disposal.

The operational store keeps active jobs plus a bounded searchable terminal window. By default, background retention cleanup keeps completed jobs for 24 hours and failed or canceled jobs for 7 days, then deletes those terminal job rows and their tags, concurrency groups, and events. Configure `ShedduellerOptions.JobRetention` to change the windows, set a state retention to `null` to keep that state indefinitely, or set `Enabled = false` to disable cleanup.

Concurrency groups independently enforce active-job capacity and an optional smooth job-start rate across the cluster. Capacity limits use a persisted override over a code-defined default over the built-in default of `1`. Rate limits use a persisted override over a code-defined default over a built-in unlimited rate.

Use `IConcurrencyGroupManager.SetDefaultLimitAsync(...)` and `SetDefaultRateLimitAsync(...)` from startup or deployment seeding code so dashboard edits survive restarts. Rate permits are evenly spaced: a rate of two starts per second admits one claim every 500 milliseconds and does not accumulate burst credit while idle. Every successful claim consumes a rate permit, including retries and reclaims.

```csharp
await concurrencyGroups.SetDefaultLimitAsync("provider:happy-holiday-homes", 1);
await concurrencyGroups.SetDefaultRateLimitAsync(
  "provider:happy-holiday-homes",
  new ConcurrencyGroupRateLimit(2, TimeSpan.FromSeconds(1)));
```

Use `SetLimitAsync(...)` and `SetRateLimitAsync(...)` for limited live overrides. `SetUnlimitedRateLimitAsync(...)` explicitly disables a code-defined rate, while `ClearRateLimitOverrideAsync(...)` returns to the code default. `ClearLimitOverrideAsync(...)` performs the equivalent reset for capacity.

## Enqueue Jobs

Job methods return `Task` or `ValueTask` and receive the scheduler-owned `CancellationToken`. Use constructor-injected `ILogger<T>` for durable job logs, `Job.Context` when a handler needs the job id or attempt number, and scheduler-supplied `IProgress<decimal>` for durable progress updates.

```csharp
using Microsoft.Extensions.Logging;

public sealed class EmailJobs(ILogger<EmailJobs> logger)
{
    public async Task SendWelcomeAsync(
        Guid userId,
        IProgress<decimal> progress,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sending welcome email for user {UserId}.", userId);
        await SendEmailAsync(userId, cancellationToken);
        progress.Report(100);
    }
}

var jobId = await enqueuer.EnqueueAsync<EmailJobs>(
    (jobs, ct, progress) => jobs.SendWelcomeAsync(userId, progress, ct),
    new JobSubmission(
        Priority: 10,
        ConcurrencyGroupKeys: ["email"],
        RetryPolicy: new RetryPolicy(3, RetryBackoffKind.Exponential, TimeSpan.FromSeconds(5)),
        Tags: [new JobTag("email", "send-welcome")]),
    cancellationToken);
```

Sheddueller captures job logs by registering a Microsoft `ILoggerProvider`. If the application uses Serilog as the host logger and still wants Sheddueller's provider to receive log events, enable provider forwarding:

```csharp
builder.Host.UseSerilog(
    (_, _, loggerConfiguration) => loggerConfiguration.WriteTo.Logger(Log.Logger),
    preserveStaticLogger: true,
    writeToProviders: true);
```

Because Sheddueller's capture is a Microsoft `ILoggerProvider`, Microsoft logging filters still apply. Configure `Logging`/`ILoggingBuilder` filters alongside any Serilog filtering rules if you want to control which job logs are stored by Sheddueller.

Set `ShedduellerOptions.EnableJobLogCapture = true` to enable durable capture of `ILogger<T>` job logs. This is disabled by default. The worker still opens the job logging scope, so external providers can continue receiving `ShedduellerJobId`, `ShedduellerAttemptNumber`, and `ShedduellerNodeId`.

Use `NotBeforeUtc` for delayed jobs. Use `JobIdempotencyKind.MethodAndArguments` to reuse an existing queued job with the same target method and serialized arguments.

Submit dependency graphs as one atomic batch. `DependsOn` blocks a job until every referenced job in the same batch is terminal; completed, failed, and canceled prerequisites all satisfy the dependency. Graphs may contain arbitrary fan-out, fan-in, and depth.

```csharp
var fetchProfile = JobEnqueueItem.Create<ImportJobs>(
    (jobs, ct) => jobs.FetchProfileAsync(managerId, ct));
var fetchRates = JobEnqueueItem.Create<ImportJobs>(
    (jobs, ct) => jobs.FetchRatesAsync(managerId, ct));
var aggregate = JobEnqueueItem.Create<ImportJobs>(
        (jobs, ct) => jobs.AggregateAsync(managerId, ct))
    .DependsOn([fetchProfile, fetchRates]);

await enqueuer.EnqueueManyAsync([fetchProfile, fetchRates, aggregate], cancellationToken);
```

Every prerequisite must be present in the submitted batch. Cycles, self-dependencies, duplicate items, missing prerequisites, and idempotency within dependency graphs are rejected before enqueueing.

## Recurring Schedules

Recurring schedules are keyed definitions. Calling `CreateOrUpdateAsync` at startup is the intended reconciliation model.

```csharp
await schedules.CreateOrUpdateAsync<EmailJobs>(
    "email:daily-digest",
    "0 2 * * *",
    (jobs, ct, progress) => jobs.SendDailyDigestAsync(progress, ct),
    new RecurringScheduleOptions(
        Priority: 5,
        ConcurrencyGroupKeys: ["email"],
        OverlapMode: RecurringOverlapMode.Skip),
    cancellationToken);
```

Cron expressions use the standard five-field format and are evaluated in UTC.

## Dashboard

```csharp
builder.Services.AddShedduellerDashboard(options =>
{
    options.EventRetention = TimeSpan.FromDays(14);
    options.JobViews =
    [
        new ShedduellerDashboardJobView("Active Work")
        {
            States = [JobState.Queued, JobState.Claimed],
            Columns =
            [
                new(ShedduellerDashboardJobColumnKind.JobId),
                new(ShedduellerDashboardJobColumnKind.Enqueued),
                new(ShedduellerDashboardJobColumnKind.State),
                new(ShedduellerDashboardJobColumnKind.Queue),
                new(ShedduellerDashboardJobColumnKind.Handler),
                new(ShedduellerDashboardJobColumnKind.Tag, "provider", "Provider"),
                new(ShedduellerDashboardJobColumnKind.Groups),
                new(ShedduellerDashboardJobColumnKind.Progress),
                new(ShedduellerDashboardJobColumnKind.Attempts),
            ],
        },
        new ShedduellerDashboardJobView("Failures")
        {
            States = [JobState.Failed],
            Sort = JobInspectionSort.NewestFirst,
            Columns =
            [
                new(ShedduellerDashboardJobColumnKind.JobId),
                new(ShedduellerDashboardJobColumnKind.TerminalTime),
                new(ShedduellerDashboardJobColumnKind.Handler),
                new(ShedduellerDashboardJobColumnKind.Tag, "provider", "Provider"),
                new(ShedduellerDashboardJobColumnKind.Tag, "manager", "Manager"),
                new(ShedduellerDashboardJobColumnKind.Tag, "external-listing", "External Listing"),
                new(ShedduellerDashboardJobColumnKind.Disposition),
                new(ShedduellerDashboardJobColumnKind.Attempts),
            ],
        },
        new ShedduellerDashboardJobView("Listings")
        {
            TagContains = "domain:listing",
            Sort = JobInspectionSort.NewestFirst,
            Columns =
            [
                new(ShedduellerDashboardJobColumnKind.JobId),
                new(ShedduellerDashboardJobColumnKind.Enqueued),
                new(ShedduellerDashboardJobColumnKind.State),
                new(ShedduellerDashboardJobColumnKind.Handler),
                new(ShedduellerDashboardJobColumnKind.Tag, "provider", "Provider"),
                new(ShedduellerDashboardJobColumnKind.Tag, "manager", "Manager"),
                new(ShedduellerDashboardJobColumnKind.Tag, "listing-id", "Listing"),
                new(ShedduellerDashboardJobColumnKind.Tag, "external-listing", "External Listing"),
                new(ShedduellerDashboardJobColumnKind.Disposition),
            ],
        },
    ];
    options.DefaultJobViewName = "Active Work";
});

app.UseAntiforgery();
app.MapShedduellerDashboard("/sheddueller");
```

The dashboard uses the configured Sheddueller provider and can be hosted by a worker process or a client-only web process. Configured job views are shared, read-only presets. Dashboard users can copy or create personal views that persist in browser storage for the dashboard's mounted path. A view restores filters, sort order, visible column order, per-column width overrides, and tag-derived columns; promoted tags are removed from the remaining Tags cell. Column widths can be set in host presets with the optional `Width` property or resized and auto-fitted from the dashboard.

## Testing

`Sheddueller.Testing` replaces enqueueing and schedule management with capture-friendly fakes.

```csharp
services.AddShedduellerTesting();

var capture = provider.GetRequiredService<CapturingJobEnqueuer>().Capture();
await subject.DoSomethingThatEnqueuesAsync();

var matches = await capture.Fake.MatchAsync<EmailJobs>(
    (jobs, ct, progress) => jobs.SendWelcomeAsync(userId, progress, ct));
```

The same package includes `FakeJobEnqueuer`, `FakeRecurringScheduleManager`, and async-context-aware capture services for dependency-injected tests.

## Sample

From the repository root:

```bash
docker compose up -d postgres
dotnet run --project samples/Sheddueller.SampleHost
```

Open `http://localhost:5000/` for the launcher and `http://localhost:5000/sheddueller` for the dashboard.
