namespace Sheddueller.Worker.Tests;

using Sheddueller.Storage;

internal sealed class RecordingRetentionStore : IJobRetentionStore
{
    public TaskCompletionSource CleanupCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public JobRetentionCleanupRequest? Request { get; private set; }

    public ValueTask<JobRetentionCleanupResult> CleanupTerminalJobsAsync(
        JobRetentionCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        this.Request = request;
        this.CleanupCalled.TrySetResult();

        return ValueTask.FromResult(new JobRetentionCleanupResult(0));
    }
}
