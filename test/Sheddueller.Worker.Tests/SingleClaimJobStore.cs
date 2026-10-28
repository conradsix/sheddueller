namespace Sheddueller.Worker.Tests;

using Sheddueller.Storage;

/// <summary>
/// A store that hands the worker one job, keeps its lease, and records how the job ended.
/// </summary>
internal sealed class SingleClaimJobStore(ClaimedJob job) : StubJobStore
{
    private int _claimed;

    public TaskCompletionSource<CompleteJobRequest> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<FailJobRequest> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ValueTask<ClaimJobResult> TryClaimNextAsync(
        ClaimJobRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult<ClaimJobResult>(
          Interlocked.Exchange(ref this._claimed, 1) == 0
            ? new ClaimJobResult.Claimed(job)
            : new ClaimJobResult.NoJobAvailable());

    public override ValueTask<bool> MarkCompletedAsync(
        CompleteJobRequest request,
        CancellationToken cancellationToken = default)
    {
        this.Completed.TrySetResult(request);
        return ValueTask.FromResult(true);
    }

    public override ValueTask<bool> MarkFailedAsync(
        FailJobRequest request,
        CancellationToken cancellationToken = default)
    {
        this.Failed.TrySetResult(request);
        return ValueTask.FromResult(true);
    }

    public override ValueTask<bool> RenewLeaseAsync(
        RenewLeaseRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(true);

    public override ValueTask<bool> ReleaseJobAsync(
        ReleaseJobRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(true);

    public override ValueTask<DateTimeOffset?> GetCancellationRequestedAtAsync(
        JobCancellationStatusRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult<DateTimeOffset?>(null);

    public override ValueTask<bool> MarkCancellationObservedAsync(
        ObserveJobCancellationRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(true);

    public override ValueTask RecordWorkerNodeHeartbeatAsync(
        WorkerNodeHeartbeatRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;

    public override ValueTask<int> RecoverExpiredLeasesAsync(
        RecoverExpiredLeasesRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(0);

    public override ValueTask<int> MaterializeDueRecurringSchedulesAsync(
        MaterializeDueRecurringSchedulesRequest request,
        CancellationToken cancellationToken = default)
      => ValueTask.FromResult(0);
}
