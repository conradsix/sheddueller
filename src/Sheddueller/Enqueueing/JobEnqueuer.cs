namespace Sheddueller.Enqueueing;

using System.Linq.Expressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller.Runtime;
using Sheddueller.Serialization;
using Sheddueller.Storage;

internal sealed class JobEnqueuer(
  IJobStore store,
  IJobPayloadSerializer serializer,
  IOptions<ShedduellerOptions> options,
  TimeProvider timeProvider,
  IShedduellerWakeSignal wakeSignal,
  ILogger<JobEnqueuer> logger) : IJobEnqueuer
{
    public ValueTask<Guid> EnqueueAsync(
      Expression<Func<CancellationToken, Task>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(JobExpressionParser.Parse(work), submission, cancellationToken);

    public ValueTask<Guid> EnqueueAsync(
      Expression<Func<CancellationToken, ValueTask>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(JobExpressionParser.Parse(work), submission, cancellationToken);

    public ValueTask<Guid> EnqueueAsync(
      Expression<Func<CancellationToken, IProgress<decimal>, Task>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(JobExpressionParser.Parse(work), submission, cancellationToken);

    public ValueTask<Guid> EnqueueAsync(
      Expression<Func<CancellationToken, IProgress<decimal>, ValueTask>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(JobExpressionParser.Parse(work), submission, cancellationToken);

    public ValueTask<Guid> EnqueueAsync<TService>(
      Expression<Func<TService, CancellationToken, Task>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(JobExpressionParser.Parse(work), submission, cancellationToken);

    public ValueTask<Guid> EnqueueAsync<TService>(
      Expression<Func<TService, CancellationToken, ValueTask>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(JobExpressionParser.Parse(work), submission, cancellationToken);

    public ValueTask<Guid> EnqueueAsync<TService>(
      Expression<Func<TService, CancellationToken, IProgress<decimal>, Task>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(JobExpressionParser.Parse(work), submission, cancellationToken);

    public ValueTask<Guid> EnqueueAsync<TService>(
      Expression<Func<TService, CancellationToken, IProgress<decimal>, ValueTask>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(JobExpressionParser.Parse(work), submission, cancellationToken);

    public async ValueTask<IReadOnlyList<Guid>> EnqueueManyAsync(
      IReadOnlyList<JobEnqueueItem> jobs,
      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        cancellationToken.ThrowIfCancellationRequested();

        if (jobs.Count == 0)
        {
            return [];
        }

        var jobSnapshot = jobs.ToArray();
        ValidateDependencyGraph(jobSnapshot);
        var jobIdsByItem = new Dictionary<JobEnqueueItem, Guid>(ReferenceEqualityComparer.Instance);
        foreach (var job in jobSnapshot)
        {
            ArgumentNullException.ThrowIfNull(job, nameof(jobs));
            jobIdsByItem.Add(job, Guid.NewGuid());
        }

        var hasDependencies = jobSnapshot.Any(static job => job.Prerequisites.Count > 0);
        if (hasDependencies && jobSnapshot.Any(static job => job.Submission?.IdempotencyKind is not null and not JobIdempotencyKind.None))
        {
            throw new ArgumentException("Jobs in a dependency graph cannot use idempotency.", nameof(jobs));
        }

        var requests = new EnqueueJobRequest[jobSnapshot.Length];
        var enqueuedAtUtc = timeProvider.GetUtcNow();
        for (var i = 0; i < jobSnapshot.Length; i++)
        {
            var job = jobSnapshot[i];

            requests[i] = await this.CreateRequestAsync(
              JobExpressionParser.Parse(job.ServiceType, job.Work),
              job.Submission,
              jobIdsByItem[job],
              enqueuedAtUtc,
              [.. job.Prerequisites.Select(prerequisite => jobIdsByItem[prerequisite])],
              cancellationToken)
              .ConfigureAwait(false);
        }

        var results = await store.EnqueueManyAsync(requests, cancellationToken).ConfigureAwait(false);
        if (results.Count != requests.Length)
        {
            throw new InvalidOperationException("The job store returned a result count that does not match the submitted batch size.");
        }

        var enqueuedCount = results.Count(result => result.WasEnqueued);
        if (enqueuedCount > 0)
        {
            wakeSignal.Notify();
        }

        logger.JobsBatchEnqueued(requests.Length, enqueuedCount);

        var jobIds = new Guid[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            jobIds[i] = results[i].JobId;
        }

        return jobIds;
    }

    private async ValueTask<Guid> EnqueueCoreAsync(
      ParsedJob parsedJob,
      JobSubmission? submission,
      CancellationToken cancellationToken)
    {
        var request = await this.CreateRequestAsync(
          parsedJob,
          submission,
          Guid.NewGuid(),
          timeProvider.GetUtcNow(),
          prerequisiteJobIds: [],
          cancellationToken)
          .ConfigureAwait(false);
        var result = await store.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.WasEnqueued)
        {
            wakeSignal.Notify();
            logger.JobEnqueued(result.JobId, result.EnqueueSequence);
        }
        else
        {
            logger.JobEnqueueDeduplicated(result.JobId, result.EnqueueSequence);
        }

        return result.JobId;
    }

    private async ValueTask<EnqueueJobRequest> CreateRequestAsync(
      ParsedJob parsedTask,
      JobSubmission? submission,
      Guid jobId,
      DateTimeOffset enqueuedAtUtc,
      IReadOnlyList<Guid> prerequisiteJobIds,
      CancellationToken cancellationToken)
    {
        SubmissionValidator.ValidateIdempotency(submission);

        var groups = SubmissionValidator.NormalizeConcurrencyGroupKeys(submission?.ConcurrencyGroupKeys);
        var tags = SubmissionValidator.NormalizeJobTags(submission?.Tags);
        var retryPolicy = submission?.RetryPolicy ?? options.Value.DefaultRetryPolicy;
        var (maxAttempts, retryBackoffKind, retryBaseDelay, retryMaxDelay) = SubmissionValidator.NormalizeRetryPolicy(retryPolicy);
        var serializedArguments = await serializer
          .SerializeAsync(parsedTask.SerializableArguments, parsedTask.SerializableParameterTypes, cancellationToken)
          .ConfigureAwait(false);
        var serviceType = TypeNameFormatter.Format(parsedTask.ServiceType);
        var idempotencyKey = submission?.IdempotencyKind switch
        {
            JobIdempotencyKind.MethodAndArguments => JobIdempotencyKeyGenerator.CreateMethodAndArgumentsKey(
                parsedTask,
                serviceType,
                serializedArguments),
            _ => null,
        };

        var request = new EnqueueJobRequest(
          jobId,
          submission?.Priority ?? 0,
          serviceType,
          parsedTask.MethodName,
          parsedTask.MethodParameterTypeNames,
          serializedArguments,
          groups,
          enqueuedAtUtc,
          submission?.NotBeforeUtc?.ToUniversalTime(),
          maxAttempts,
          retryBackoffKind,
          retryBaseDelay,
          retryMaxDelay,
          SourceScheduleKey: null,
          ScheduledFireAtUtc: null,
          Tags: tags,
          InvocationTargetKind: parsedTask.InvocationTargetKind,
          MethodParameterBindings: parsedTask.MethodParameterBindings,
          IdempotencyKey: idempotencyKey,
          PrerequisiteJobIds: prerequisiteJobIds);

        return request;
    }

    private static void ValidateDependencyGraph(IReadOnlyList<JobEnqueueItem> jobs)
    {
        var submitted = new HashSet<JobEnqueueItem>(ReferenceEqualityComparer.Instance);
        foreach (var job in jobs)
        {
            ArgumentNullException.ThrowIfNull(job, nameof(jobs));
            if (!submitted.Add(job))
            {
                throw new ArgumentException("A job enqueue item cannot appear more than once in a batch.", nameof(jobs));
            }
        }

        foreach (var job in jobs)
        {
            foreach (var prerequisite in job.Prerequisites)
            {
                if (!submitted.Contains(prerequisite))
                {
                    throw new ArgumentException("Every prerequisite job must be included in the same batch.", nameof(jobs));
                }
            }
        }

        DependencyGraph.ValidateAcyclic<JobEnqueueItem>(jobs, static job => job.Prerequisites, ReferenceEqualityComparer.Instance, nameof(jobs));
    }
}
