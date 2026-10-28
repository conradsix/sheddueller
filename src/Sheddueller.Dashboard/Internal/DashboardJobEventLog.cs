namespace Sheddueller.Dashboard.Internal;

using Sheddueller.Storage;

/// <summary>
/// The job detail event list. Events are kept in ascending sequence order and de-duplicated by event id or
/// sequence. Reader pages and live pushes are merged incrementally; <see cref="ReadThroughSequence"/> tracks how far
/// the durable log is known to be loaded without gaps, so the next read only asks for what is missing.
/// </summary>
internal sealed class DashboardJobEventLog(int pageSize)
{
    private readonly List<JobEvent> _events = [];

    public int PageSize { get; } = pageSize > 0
      ? pageSize
      : throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be positive.");

    public IReadOnlyList<JobEvent> Events
      => this._events;

    /// <summary>
    /// Every durable event with a sequence at or below this value is loaded. <see langword="null"/> until the first read.
    /// </summary>
    public long? ReadThroughSequence { get; private set; }

    /// <summary>
    /// The last reader page was full, so more durable events may follow <see cref="ReadThroughSequence"/>.
    /// </summary>
    public bool HasMoreToRead { get; private set; }

    public void Clear()
    {
        this._events.Clear();
        this.ReadThroughSequence = null;
        this.HasMoreToRead = false;
    }

    /// <summary>
    /// Merges one live event. Returns <see langword="false"/> when the event was already present and unchanged.
    /// </summary>
    public bool Append(JobEvent jobEvent)
    {
        ArgumentNullException.ThrowIfNull(jobEvent);

        var changed = this.Merge(jobEvent);
        this.AdvanceReadThrough();
        return changed;
    }

    /// <summary>
    /// Merges a page read from the durable log after <see cref="ReadThroughSequence"/>.
    /// </summary>
    /// <returns>The number of events that were added or replaced.</returns>
    public int AppendPage(IReadOnlyList<JobEvent> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var changedCount = 0;
        long? lastSequence = null;
        foreach (var jobEvent in page)
        {
            if (this.Merge(jobEvent))
            {
                changedCount++;
            }

            lastSequence = lastSequence is null ? jobEvent.EventSequence : Math.Max(lastSequence.Value, jobEvent.EventSequence);
        }

        if (lastSequence is { } sequence && (this.ReadThroughSequence is null || sequence > this.ReadThroughSequence))
        {
            this.ReadThroughSequence = sequence;
        }

        this.HasMoreToRead = page.Count >= this.PageSize;
        this.AdvanceReadThrough();
        return changedCount;
    }

    private bool Merge(JobEvent jobEvent)
    {
        var index = this._events.FindIndex(candidate =>
          candidate.EventId == jobEvent.EventId || candidate.EventSequence == jobEvent.EventSequence);

        if (index >= 0)
        {
            if (Equals(this._events[index], jobEvent))
            {
                return false;
            }

            this._events.RemoveAt(index);
        }

        var insertAt = this._events.FindLastIndex(candidate => candidate.EventSequence < jobEvent.EventSequence) + 1;
        this._events.Insert(insertAt, jobEvent);
        return true;
    }

    // Live events arrive without a reader cursor. When they continue the loaded run exactly (sequence n + 1) nothing
    // can be missing in between, so the cursor can move past them and later catch-up reads skip them.
    private void AdvanceReadThrough()
    {
        if (this.ReadThroughSequence is not { } readThrough)
        {
            return;
        }

        var index = this._events.FindLastIndex(candidate => candidate.EventSequence <= readThrough) + 1;
        while (index < this._events.Count && this._events[index].EventSequence == readThrough + 1)
        {
            readThrough++;
            index++;
        }

        this.ReadThroughSequence = readThrough;
    }
}
