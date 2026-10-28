namespace Sheddueller.Testing;

using System.Linq.Expressions;

using Sheddueller.Enqueueing;
using Sheddueller.Serialization;

/// <summary>
/// Test double for recording and matching jobs submitted through <see cref="IJobEnqueuer"/>.
/// </summary>
public sealed class FakeJobEnqueuer : IJobEnqueuer
{
    private readonly Lock _syncRoot = new();
    private readonly IJobPayloadSerializer _serializer;
    private readonly List<FakeEnqueuedJob> _jobs = [];
    private readonly Dictionary<string, FakeEnqueuedJob> _jobsByIdempotencyKey = new(StringComparer.Ordinal);
    private long _nextEnqueueSequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeJobEnqueuer"/> class.
    /// </summary>
    public FakeJobEnqueuer()
      : this(new SystemTextJsonJobPayloadSerializer())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeJobEnqueuer"/> class.
    /// </summary>
    /// <param name="serializer">The serializer used to compare captured job arguments.</param>
    public FakeJobEnqueuer(IJobPayloadSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        this._serializer = serializer;
    }

    /// <summary>
    /// Gets a snapshot of jobs recorded by this fake.
    /// </summary>
    public IReadOnlyList<FakeEnqueuedJob> EnqueuedJobs
    {
        get
        {
            lock (this._syncRoot)
            {
                return Array.AsReadOnly([.. this._jobs]);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueAsync(
      Expression<Func<CancellationToken, Task>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(serviceType: null, work, submission, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueAsync(
      Expression<Func<CancellationToken, ValueTask>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(serviceType: null, work, submission, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueAsync(
      Expression<Func<CancellationToken, IProgress<decimal>, Task>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(serviceType: null, work, submission, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueAsync(
      Expression<Func<CancellationToken, IProgress<decimal>, ValueTask>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(serviceType: null, work, submission, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueAsync<TService>(
      Expression<Func<TService, CancellationToken, Task>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(typeof(TService), work, submission, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueAsync<TService>(
      Expression<Func<TService, CancellationToken, ValueTask>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(typeof(TService), work, submission, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueAsync<TService>(
      Expression<Func<TService, CancellationToken, IProgress<decimal>, Task>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(typeof(TService), work, submission, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueAsync<TService>(
      Expression<Func<TService, CancellationToken, IProgress<decimal>, ValueTask>> work,
      JobSubmission? submission = null,
      CancellationToken cancellationToken = default)
      => this.EnqueueCoreAsync(typeof(TService), work, submission, cancellationToken);

    /// <inheritdoc />
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
        var preparedJobs = new PreparedJob[jobSnapshot.Length];
        var jobIdsByItem = new Dictionary<JobEnqueueItem, Guid>(ReferenceEqualityComparer.Instance);

        for (var i = 0; i < jobSnapshot.Length; i++)
        {
            var job = jobSnapshot[i];
            preparedJobs[i] = await this
                .PrepareJobAsync(job.ServiceType, job.Work, job.Submission, cancellationToken)
                .ConfigureAwait(false);
            jobIdsByItem.Add(job, preparedJobs[i].JobId);
        }

        lock (this._syncRoot)
        {
            var batchId = Guid.NewGuid();
            var jobIds = new Guid[preparedJobs.Length];

            for (var i = 0; i < preparedJobs.Length; i++)
            {
                // A batch with prerequisites uses no idempotency, so prerequisites are never deduplicated away.
                var prerequisiteJobIds = jobSnapshot[i].Prerequisites
                    .Select(prerequisite => jobIdsByItem[prerequisite])
                    .ToArray();
                jobIds[i] = this.RecordJob(preparedJobs[i], batchId, i, prerequisiteJobIds).JobId;
            }

            return jobIds;
        }
    }

    /// <summary>
    /// Finds recorded jobs that match a Task-returning job method call.
    /// </summary>
    public ValueTask<FakeJobMatch> MatchAsync(
      Expression<Func<CancellationToken, Task>> work,
      CancellationToken cancellationToken = default)
      => this.MatchCoreAsync(serviceType: null, work, cancellationToken);

    /// <summary>
    /// Finds recorded jobs that match a ValueTask-returning job method call.
    /// </summary>
    public ValueTask<FakeJobMatch> MatchAsync(
      Expression<Func<CancellationToken, ValueTask>> work,
      CancellationToken cancellationToken = default)
      => this.MatchCoreAsync(serviceType: null, work, cancellationToken);

    /// <summary>
    /// Finds recorded jobs that match a Task-returning job method call with scheduler-supplied progress reporting.
    /// </summary>
    public ValueTask<FakeJobMatch> MatchAsync(
      Expression<Func<CancellationToken, IProgress<decimal>, Task>> work,
      CancellationToken cancellationToken = default)
      => this.MatchCoreAsync(serviceType: null, work, cancellationToken);

    /// <summary>
    /// Finds recorded jobs that match a ValueTask-returning job method call with scheduler-supplied progress reporting.
    /// </summary>
    public ValueTask<FakeJobMatch> MatchAsync(
      Expression<Func<CancellationToken, IProgress<decimal>, ValueTask>> work,
      CancellationToken cancellationToken = default)
      => this.MatchCoreAsync(serviceType: null, work, cancellationToken);

    /// <summary>
    /// Finds recorded jobs that match a Task-returning service method call.
    /// </summary>
    public ValueTask<FakeJobMatch> MatchAsync<TService>(
      Expression<Func<TService, CancellationToken, Task>> work,
      CancellationToken cancellationToken = default)
      => this.MatchCoreAsync(typeof(TService), work, cancellationToken);

    /// <summary>
    /// Finds recorded jobs that match a ValueTask-returning service method call.
    /// </summary>
    public ValueTask<FakeJobMatch> MatchAsync<TService>(
      Expression<Func<TService, CancellationToken, ValueTask>> work,
      CancellationToken cancellationToken = default)
      => this.MatchCoreAsync(typeof(TService), work, cancellationToken);

    /// <summary>
    /// Finds recorded jobs that match a Task-returning service method call with scheduler-supplied progress reporting.
    /// </summary>
    public ValueTask<FakeJobMatch> MatchAsync<TService>(
      Expression<Func<TService, CancellationToken, IProgress<decimal>, Task>> work,
      CancellationToken cancellationToken = default)
      => this.MatchCoreAsync(typeof(TService), work, cancellationToken);

    /// <summary>
    /// Finds recorded jobs that match a ValueTask-returning service method call with scheduler-supplied progress reporting.
    /// </summary>
    public ValueTask<FakeJobMatch> MatchAsync<TService>(
      Expression<Func<TService, CancellationToken, IProgress<decimal>, ValueTask>> work,
      CancellationToken cancellationToken = default)
      => this.MatchCoreAsync(typeof(TService), work, cancellationToken);

    /// <summary>
    /// Removes all recorded jobs and resets enqueue sequence numbering.
    /// </summary>
    public void Clear()
    {
        lock (this._syncRoot)
        {
            this._jobs.Clear();
            this._jobsByIdempotencyKey.Clear();
            this._nextEnqueueSequence = 0;
        }
    }

    private async ValueTask<Guid> EnqueueCoreAsync(
      Type? serviceType,
      LambdaExpression work,
      JobSubmission? submission,
      CancellationToken cancellationToken)
    {
        var preparedJob = await this.PrepareJobAsync(serviceType, work, submission, cancellationToken).ConfigureAwait(false);

        lock (this._syncRoot)
        {
            return this.RecordJob(preparedJob, batchId: null, batchIndex: null, prerequisiteJobIds: []).JobId;
        }
    }

    private async ValueTask<FakeJobMatch> MatchCoreAsync(
      Type? serviceType,
      LambdaExpression work,
      CancellationToken cancellationToken)
    {
        var candidate = await FakeJobDefinition
            .CreateAsync(JobExpressionParser.Parse(serviceType, work), this._serializer, cancellationToken)
            .ConfigureAwait(false);
        FakeEnqueuedJob[] jobSnapshot;

        lock (this._syncRoot)
        {
            jobSnapshot = [.. this._jobs];
        }

        return new FakeJobMatch([.. jobSnapshot.Where(job => job.Definition.Matches(candidate))]);
    }

    private async ValueTask<PreparedJob> PrepareJobAsync(
      Type? serviceType,
      LambdaExpression work,
      JobSubmission? submission,
      CancellationToken cancellationToken)
    {
        var parsedJob = JobExpressionParser.Parse(serviceType, work);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSubmission = NormalizeSubmission(submission);
        var definition = await FakeJobDefinition.CreateAsync(parsedJob, this._serializer, cancellationToken).ConfigureAwait(false);

        return new PreparedJob(
          Guid.NewGuid(),
          definition,
          normalizedSubmission,
          definition.CreateIdempotencyKey(normalizedSubmission.IdempotencyKind));
    }

    // Like the store, an idempotent job whose key matches a recorded job returns that job instead of recording another.
    // The fake never runs jobs, so every recorded job counts as still queued.
    private FakeEnqueuedJob RecordJob(
      PreparedJob preparedJob,
      Guid? batchId,
      int? batchIndex,
      IReadOnlyList<Guid> prerequisiteJobIds)
    {
        if (preparedJob.IdempotencyKey is { } idempotencyKey
            && this._jobsByIdempotencyKey.TryGetValue(idempotencyKey, out var existingJob))
        {
            return existingJob;
        }

        var recordedJob = new FakeEnqueuedJob(
          preparedJob.JobId,
          this._nextEnqueueSequence++,
          batchId,
          batchIndex,
          preparedJob.Definition,
          preparedJob.Submission,
          prerequisiteJobIds,
          preparedJob.IdempotencyKey);
        this._jobs.Add(recordedJob);
        if (recordedJob.IdempotencyKey is not null)
        {
            this._jobsByIdempotencyKey.Add(recordedJob.IdempotencyKey, recordedJob);
        }

        return recordedJob;
    }

    private static JobSubmission NormalizeSubmission(JobSubmission? submission)
    {
        SubmissionValidator.ValidateIdempotency(submission);

        var groups = SubmissionValidator.NormalizeConcurrencyGroupKeys(submission?.ConcurrencyGroupKeys);
        var tags = SubmissionValidator.NormalizeJobTags(submission?.Tags);
        SubmissionValidator.ValidateRetryPolicy(submission?.RetryPolicy);

        return new JobSubmission(
          submission?.Priority ?? 0,
          groups,
          submission?.NotBeforeUtc?.ToUniversalTime(),
          submission?.RetryPolicy is { MaxAttempts: > 1 } retryPolicy ? retryPolicy : null,
          tags,
          submission?.IdempotencyKind ?? JobIdempotencyKind.None);
    }

    private sealed record PreparedJob(
      Guid JobId,
      FakeJobDefinition Definition,
      JobSubmission Submission,
      string? IdempotencyKey);
}
