namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardJobEventLogTests
{
    [Fact]
    public void LiveEvent_DuplicateIdOrSequence_IsNotAppendedTwice()
    {
        var log = new DashboardJobEventLog(pageSize: 10);
        var first = TestData.Event(1, message: "starting");
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

        log.Append(TestData.Event(3, message: "third"));
        log.Append(TestData.Event(1, message: "first"));
        log.Append(TestData.Event(2, message: "second"));

        log.Events.Select(jobEvent => jobEvent.EventSequence).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public void ReaderPage_Full_ReportsMoreToReadFromLastSequence()
    {
        var log = new DashboardJobEventLog(pageSize: 2);

        log.AppendPage([TestData.Event(1, message: "a"), TestData.Event(2, message: "b")]);

        log.HasMoreToRead.ShouldBeTrue();
        log.ReadThroughSequence.ShouldBe(2);

        log.AppendPage([TestData.Event(3, message: "c")]);

        log.HasMoreToRead.ShouldBeFalse();
        log.ReadThroughSequence.ShouldBe(3);
        log.Events.Select(jobEvent => jobEvent.EventSequence).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public void LiveEvent_ContinuingLoadedRun_AdvancesReadCursor()
    {
        var log = new DashboardJobEventLog(pageSize: 10);
        log.AppendPage([TestData.Event(1, message: "a"), TestData.Event(2, message: "b")]);

        log.Append(TestData.Event(3, message: "c"));

        log.ReadThroughSequence.ShouldBe(3);
    }

    [Fact]
    public void LiveEvent_AfterGap_DoesNotAdvanceReadCursorUntilGapIsRead()
    {
        var log = new DashboardJobEventLog(pageSize: 10);
        log.AppendPage([TestData.Event(1, message: "a")]);

        log.Append(TestData.Event(4, message: "missed two while paused"));

        log.ReadThroughSequence.ShouldBe(1);

        log.AppendPage([TestData.Event(2, message: "b"), TestData.Event(3, message: "c"), TestData.Event(4, message: "missed two while paused")]);

        log.ReadThroughSequence.ShouldBe(4);
        log.Events.Select(jobEvent => jobEvent.EventSequence).ShouldBe([1L, 2L, 3L, 4L]);
    }

    [Fact]
    public void ReaderPage_FillsGapBelowLiveEvents_MergesAndClosesRun()
    {
        var log = new DashboardJobEventLog(pageSize: 2);
        log.AppendPage([TestData.Event(1, message: "a"), TestData.Event(2, message: "b")]);
        log.Append(TestData.Event(5, message: "live"));

        log.AppendPage([TestData.Event(3, message: "c"), TestData.Event(4, message: "d")]);

        log.Events.Select(jobEvent => jobEvent.EventSequence).ShouldBe([1L, 2L, 3L, 4L, 5L]);
        log.ReadThroughSequence.ShouldBe(5);
    }

    [Fact]
    public void Clear_LoadedEvents_ResetsCursor()
    {
        var log = new DashboardJobEventLog(pageSize: 1);
        log.AppendPage([TestData.Event(1, message: "a")]);

        log.Clear();

        log.Events.ShouldBeEmpty();
        log.ReadThroughSequence.ShouldBeNull();
        log.HasMoreToRead.ShouldBeFalse();
    }
}
