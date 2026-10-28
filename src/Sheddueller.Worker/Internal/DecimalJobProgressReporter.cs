namespace Sheddueller.Worker.Internal;

using Sheddueller.Storage;

/// <summary>
/// The <see cref="IProgress{T}"/> handed to a running job. Progress is queued for the job event dispatcher like captured
/// logs, so reporting never waits on the event sink; the queue keeps each job's reports in order.
/// </summary>
internal sealed class DecimalJobProgressReporter(
    Guid jobId,
    int attemptNumber,
    ShedduellerJobLogEventQueue jobEventQueue) : IProgress<decimal>
{
    public void Report(decimal value)
      => _ = jobEventQueue.TryEnqueue(new AppendJobEventRequest(
          jobId,
          JobEventKind.Progress,
          attemptNumber,
          ProgressPercent: (double)Math.Clamp(value, 0, 100)));
}
