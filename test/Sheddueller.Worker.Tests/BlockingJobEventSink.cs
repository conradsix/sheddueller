namespace Sheddueller.Worker.Tests;

using Sheddueller.Storage;

/// <summary>
/// A sink whose appends wait until the test allows them, standing in for a slow event store.
/// </summary>
internal sealed class BlockingJobEventSink : IJobEventSink
{
    public TaskCompletionSource AllowAppend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<JobEvent> AppendAsync(
        AppendJobEventRequest request,
        CancellationToken cancellationToken = default)
    {
        await this.AllowAppend.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new JobEvent(
          Guid.NewGuid(),
          request.JobId,
          EventSequence: 1,
          request.Kind,
          DateTimeOffset.UtcNow,
          request.AttemptNumber,
          request.LogLevel,
          request.Message,
          request.ProgressPercent,
          request.Fields);
    }
}
