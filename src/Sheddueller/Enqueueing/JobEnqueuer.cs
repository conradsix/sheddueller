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
        SubmissionValidator.ValidateBatch(jobSnapshot);
        var jobIdsByItem = new Dictionary<JobEnqueueItem, Guid>(ReferenceEqualityComparer.Instance);
        foreach (var job in jobSnapshot)
        {
            jobIdsByItem.Add(job, Guid.NewGuid());
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
      ParsedJob parsedJob,
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
          .SerializeAsync(parsedJob.SerializableArguments, parsedJob.SerializableParameterTypes, cancellationToken)
          .ConfigureAwait(false);
        var serviceType = TypeNameFormatter.Format(parsedJob.ServiceType);
        var idempotencyKey = JobIdempotencyKeyGenerator.CreateKey(submission?.IdempotencyKind, parsedJob, serviceType, serializedArguments);

        var request = new EnqueueJobRequest(
          jobId,
          submission?.Priority ?? 0,
          serviceType,
          parsedJob.MethodName,
          parsedJob.MethodParameterTypeNames,
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
          InvocationTargetKind: parsedJob.InvocationTargetKind,
          MethodParameterBindings: parsedJob.MethodParameterBindings,
          IdempotencyKey: idempotencyKey,
          PrerequisiteJobIds: prerequisiteJobIds);

        return request;
    }
}
