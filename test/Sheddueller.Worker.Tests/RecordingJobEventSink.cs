namespace Sheddueller.Worker.Tests;

using System.Collections.Concurrent;

using Sheddueller.Storage;

internal sealed class RecordingJobEventSink : IJobEventSink
{
    private readonly ConcurrentQueue<AppendJobEventRequest> _requests = new();
    private long _eventSequence;

    public IReadOnlyList<AppendJobEventRequest> Requests
      => [.. this._requests];

    public ValueTask<JobEvent> AppendAsync(
        AppendJobEventRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        this._requests.Enqueue(request);
        return ValueTask.FromResult(new JobEvent(
          Guid.NewGuid(),
          request.JobId,
          Interlocked.Increment(ref this._eventSequence),
          request.Kind,
          DateTimeOffset.UtcNow,
          request.AttemptNumber,
          request.LogLevel,
          request.Message,
          request.ProgressPercent,
          request.Fields));
    }
}
