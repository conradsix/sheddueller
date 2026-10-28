namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobListMergeTests
{
    private static readonly Guid FirstJobId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondJobId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ThirdJobId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid NewJobId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    [Fact]
    public void LiveMerge_ReorderedResult_UpdatesRowsInPlaceAndKeepsDisplayedOrder()
    {
        IReadOnlyList<JobInspectionSummary> displayed =
        [
            CreateJob(FirstJobId, JobState.Queued),
            CreateJob(SecondJobId, JobState.Queued),
            CreateJob(ThirdJobId, JobState.Queued),
        ];
        IReadOnlyList<JobInspectionSummary> fresh =
        [
            CreateJob(ThirdJobId, JobState.Claimed),
            CreateJob(FirstJobId, JobState.Claimed),
            CreateJob(SecondJobId, JobState.Queued),
        ];

        var result = DashboardJobListMerge.Merge(displayed, fresh);

        result.Rows.Select(job => job.JobId).ShouldBe([FirstJobId, SecondJobId, ThirdJobId]);
        result.Rows.Select(job => job.State).ShouldBe([JobState.Claimed, JobState.Queued, JobState.Claimed]);
        result.NewJobCount.ShouldBe(0);
    }

    [Fact]
    public void LiveMerge_NewRowsInResult_AreHeldBackAndCounted()
    {
        IReadOnlyList<JobInspectionSummary> displayed =
        [
            CreateJob(FirstJobId, JobState.Queued),
            CreateJob(SecondJobId, JobState.Queued),
        ];
        IReadOnlyList<JobInspectionSummary> fresh =
        [
            CreateJob(NewJobId, JobState.Queued),
            CreateJob(FirstJobId, JobState.Queued),
            CreateJob(SecondJobId, JobState.Queued),
        ];

        var result = DashboardJobListMerge.Merge(displayed, fresh);

        result.Rows.Select(job => job.JobId).ShouldBe([FirstJobId, SecondJobId]);
        result.NewJobIds.ShouldBe([NewJobId]);
        result.NewJobCount.ShouldBe(1);
    }

    [Fact]
    public void LiveMerge_RowNoLongerInResult_StaysUnchanged()
    {
        var staleRow = CreateJob(SecondJobId, JobState.Queued);
        IReadOnlyList<JobInspectionSummary> displayed =
        [
            CreateJob(FirstJobId, JobState.Queued),
            staleRow,
        ];
        IReadOnlyList<JobInspectionSummary> fresh =
        [
            CreateJob(FirstJobId, JobState.Claimed),
        ];

        var result = DashboardJobListMerge.Merge(displayed, fresh);

        result.Rows.Select(job => job.JobId).ShouldBe([FirstJobId, SecondJobId]);
        result.Rows[1].ShouldBeSameAs(staleRow);
        result.Rows[0].State.ShouldBe(JobState.Claimed);
        result.NewJobCount.ShouldBe(0);
    }

    [Fact]
    public void LiveMerge_NothingDisplayed_ShowsFreshResultDirectly()
    {
        IReadOnlyList<JobInspectionSummary> fresh =
        [
            CreateJob(NewJobId, JobState.Queued),
            CreateJob(FirstJobId, JobState.Queued),
        ];

        var result = DashboardJobListMerge.Merge([], fresh);

        result.Rows.Select(job => job.JobId).ShouldBe([NewJobId, FirstJobId]);
        result.NewJobCount.ShouldBe(0);
    }

    [Fact]
    public void LoadMore_OverlappingPage_SkipsRowsAlreadyDisplayed()
    {
        IReadOnlyList<JobInspectionSummary> displayed =
        [
            CreateJob(FirstJobId, JobState.Queued),
            CreateJob(SecondJobId, JobState.Queued),
        ];
        IReadOnlyList<JobInspectionSummary> page =
        [
            CreateJob(SecondJobId, JobState.Claimed),
            CreateJob(ThirdJobId, JobState.Queued),
        ];

        var rows = DashboardJobListMerge.Append(displayed, page);

        rows.Select(job => job.JobId).ShouldBe([FirstJobId, SecondJobId, ThirdJobId]);
        rows[1].State.ShouldBe(JobState.Queued);
    }

    private static JobInspectionSummary CreateJob(
        Guid jobId,
        JobState state)
      => new(
        jobId,
        state,
        "Sheddueller.Dashboard.Tests.Worker",
        "Run",
        Priority: 0,
        EnqueueSequence: 1,
        DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture),
        NotBeforeUtc: null,
        AttemptCount: 1,
        MaxAttempts: 3,
        Tags: [],
        ConcurrencyGroupKeys: [],
        SourceScheduleKey: null,
        LatestProgress: null,
        QueuePosition: null,
        ClaimedAtUtc: null,
        CompletedAtUtc: null,
        FailedAtUtc: null,
        CanceledAtUtc: null);
}
