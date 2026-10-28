namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobEventLogTests
{
    private static readonly Guid JobId = Guid.Parse("8c32d457-9e7a-42bb-8947-0c8fa54743be");

    [Fact]
    public void LiveEvent_DuplicateIdOrSequence_IsNotAppendedTwice()
    {
        var log = new DashboardJobEventLog(pageSize: 10);
        var first = CreateEvent(1, "starting");
        log.AppendPage([first]);

        log.Append(first).ShouldBeFalse();
        log.Append(first with { EventId = Guid.NewGuid() }).ShouldBeTrue();

        log.Events.Count.ShouldBe(1);
        log.Events[0].EventSequence.ShouldBe(1);
    }

    [Fact]
    public void LiveEvent_OutOfOrder_IsKeptInSequenceOrder()
    {
        var log = new DashboardJobEventLog(pageSize: 10);

        log.Append(CreateEvent(3, "third"));
        log.Append(CreateEvent(1, "first"));
        log.Append(CreateEvent(2, "second"));

        log.Events.Select(jobEvent => jobEvent.EventSequence).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public void ReaderPage_Full_ReportsMoreToReadFromLastSequence()
    {
        var log = new DashboardJobEventLog(pageSize: 2);

        log.AppendPage([CreateEvent(1, "a"), CreateEvent(2, "b")]);

        log.HasMoreToRead.ShouldBeTrue();
        log.ReadThroughSequence.ShouldBe(2);

        log.AppendPage([CreateEvent(3, "c")]);

        log.HasMoreToRead.ShouldBeFalse();
        log.ReadThroughSequence.ShouldBe(3);
        log.Events.Select(jobEvent => jobEvent.EventSequence).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public void LiveEvent_ContinuingLoadedRun_AdvancesReadCursor()
    {
        var log = new DashboardJobEventLog(pageSize: 10);
        log.AppendPage([CreateEvent(1, "a"), CreateEvent(2, "b")]);

        log.Append(CreateEvent(3, "c"));

        log.ReadThroughSequence.ShouldBe(3);
    }

    [Fact]
    public void LiveEvent_AfterGap_DoesNotAdvanceReadCursorUntilGapIsRead()
    {
        var log = new DashboardJobEventLog(pageSize: 10);
        log.AppendPage([CreateEvent(1, "a")]);

        log.Append(CreateEvent(4, "missed two while paused"));

        log.ReadThroughSequence.ShouldBe(1);

        log.AppendPage([CreateEvent(2, "b"), CreateEvent(3, "c"), CreateEvent(4, "missed two while paused")]);

        log.ReadThroughSequence.ShouldBe(4);
        log.Events.Select(jobEvent => jobEvent.EventSequence).ShouldBe([1L, 2L, 3L, 4L]);
    }

    [Fact]
    public void ReaderPage_FillsGapBelowLiveEvents_MergesAndClosesRun()
    {
        var log = new DashboardJobEventLog(pageSize: 2);
        log.AppendPage([CreateEvent(1, "a"), CreateEvent(2, "b")]);
        log.Append(CreateEvent(5, "live"));

        log.AppendPage([CreateEvent(3, "c"), CreateEvent(4, "d")]);

        log.Events.Select(jobEvent => jobEvent.EventSequence).ShouldBe([1L, 2L, 3L, 4L, 5L]);
        log.ReadThroughSequence.ShouldBe(5);
    }

    [Fact]
    public void Clear_LoadedEvents_ResetsCursor()
    {
        var log = new DashboardJobEventLog(pageSize: 1);
        log.AppendPage([CreateEvent(1, "a")]);

        log.Clear();

        log.Events.ShouldBeEmpty();
        log.ReadThroughSequence.ShouldBeNull();
        log.HasMoreToRead.ShouldBeFalse();
    }

    private static JobEvent CreateEvent(
        long sequence,
        string message)
      => new(
        Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{sequence:D12}")),
        JobId,
        sequence,
        JobEventKind.Log,
        DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture).AddSeconds(sequence),
        AttemptNumber: 1,
        LogLevel: JobLogLevel.Information,
        Message: message);
}
