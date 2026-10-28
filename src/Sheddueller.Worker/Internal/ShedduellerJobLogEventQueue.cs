namespace Sheddueller.Worker.Internal;

using System.Threading.Channels;

using Sheddueller.Storage;

/// <summary>
/// Hands captured job logs and progress from running jobs to <see cref="ShedduellerJobLogEventDispatcher"/>. Job events
/// are best-effort telemetry, so the queue is bounded: when the sink falls behind, new events are dropped rather than
/// held in memory or allowed to block a job. Drops are counted for the dispatcher to report.
/// </summary>
internal sealed class ShedduellerJobLogEventQueue
{
    internal const int DefaultCapacity = 10_000;

    private readonly Channel<AppendJobEventRequest> _channel;
    private long _droppedCount;

    public ShedduellerJobLogEventQueue()
      : this(DefaultCapacity)
    {
    }

    internal ShedduellerJobLogEventQueue(int capacity)
    {
        this.Capacity = capacity;

        // Wait mode makes TryWrite fail when the queue is full, without waiting, so a drop is seen and counted.
        this._channel = Channel.CreateBounded<AppendJobEventRequest>(
          new BoundedChannelOptions(capacity)
          {
              AllowSynchronousContinuations = false,
              FullMode = BoundedChannelFullMode.Wait,
              SingleReader = true,
              SingleWriter = false,
          });
    }

    public int Capacity { get; }

    public ChannelReader<AppendJobEventRequest> Reader
      => this._channel.Reader;

    public bool TryEnqueue(AppendJobEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (this._channel.Writer.TryWrite(request))
        {
            return true;
        }

        // Not logged here: this runs inside the job's logging, so a log would be captured and queued in turn.
        Interlocked.Increment(ref this._droppedCount);
        return false;
    }

    /// <summary>
    /// Returns how many events were dropped since the last call.
    /// </summary>
    public long TakeDroppedCount()
      => Interlocked.Exchange(ref this._droppedCount, 0);
}
