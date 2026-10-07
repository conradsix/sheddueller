namespace Sheddueller.Worker.Tests;

using Sheddueller.Storage;

internal class WorkerTestJobStore(ClaimedJob job) : IJobStore
{
    private int _claimed;

    public TaskCompletionSource<ReleaseJobRequest> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<ObserveJobCancellationRequest> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<CompleteJobRequest> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<FailJobRequest> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<EnqueueJobResult> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<EnqueueJobResult>> EnqueueManyAsync(
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<ClaimJobResult> TryClaimNextAsync(
        ClaimJobRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult<ClaimJobResult>(
          Interlocked.Exchange(ref this._claimed, 1) == 0
            ? new ClaimJobResult.Claimed(job)
            : new ClaimJobResult.NoJobAvailable());

    public ValueTask<bool> MarkCompletedAsync(
        CompleteJobRequest request,
        CancellationToken cancellationToken = default)
    {
        this.Completed.TrySetResult(request);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> MarkFailedAsync(
        FailJobRequest request,
        CancellationToken cancellationToken = default)
    {
        this.Failed.TrySetResult(request);
        return ValueTask.FromResult(true);
    }

    public virtual ValueTask<bool> RenewLeaseAsync(
        RenewLeaseRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(true);

    public ValueTask<bool> ReleaseJobAsync(
        ReleaseJobRequest request,
        CancellationToken cancellationToken = default)
    {
        this.Released.TrySetResult(request);
        return ValueTask.FromResult(true);
    }

    public ValueTask<int> RecoverExpiredLeasesAsync(
        RecoverExpiredLeasesRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(0);

    public ValueTask<JobCancellationResult> CancelAsync(
        CancelJobRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(JobCancellationResult.NotFound);

    public ValueTask<int> CancelQueuedJobsAsync(
        CancelQueuedJobsRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(0);

    public virtual ValueTask<DateTimeOffset?> GetCancellationRequestedAtAsync(
        JobCancellationStatusRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult<DateTimeOffset?>(null);

    public ValueTask<bool> MarkCancellationObservedAsync(
        ObserveJobCancellationRequest request,
        CancellationToken cancellationToken = default)
    {
        this.CancellationObserved.TrySetResult(request);
        return ValueTask.FromResult(true);
    }

    public ValueTask RecordWorkerNodeHeartbeatAsync(
        WorkerNodeHeartbeatRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;

    public ValueTask SetConcurrencyLimitAsync(
        SetConcurrencyLimitRequest request,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask SetConcurrencyDefaultLimitAsync(
        SetConcurrencyDefaultLimitRequest request,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask ClearConcurrencyLimitOverrideAsync(
        ClearConcurrencyLimitOverrideRequest request,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<int?> GetConfiguredConcurrencyLimitAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateRecurringScheduleAsync(
        UpsertRecurringScheduleRequest request,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<RecurringScheduleTriggerResult> TriggerRecurringScheduleAsync(
        TriggerRecurringScheduleRequest request,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<bool> DeleteRecurringScheduleAsync(
        string scheduleKey,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<bool> PauseRecurringScheduleAsync(
        string scheduleKey,
        DateTimeOffset pausedAtUtc,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<bool> ResumeRecurringScheduleAsync(
        string scheduleKey,
        DateTimeOffset resumedAtUtc,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<RecurringScheduleInfo?> GetRecurringScheduleAsync(
        string scheduleKey,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<RecurringScheduleInfo>> ListRecurringSchedulesAsync(
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public ValueTask<int> MaterializeDueRecurringSchedulesAsync(
        MaterializeDueRecurringSchedulesRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(0);
}
