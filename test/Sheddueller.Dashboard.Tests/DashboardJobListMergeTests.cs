namespace Sheddueller.Dashboard.Tests;

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
            TestData.Job(FirstJobId, JobState.Queued),
            TestData.Job(SecondJobId, JobState.Queued),
            TestData.Job(ThirdJobId, JobState.Queued),
        ];
        IReadOnlyList<JobInspectionSummary> fresh =
        [
            TestData.Job(ThirdJobId, JobState.Claimed),
            TestData.Job(FirstJobId, JobState.Claimed),
            TestData.Job(SecondJobId, JobState.Queued),
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
            TestData.Job(FirstJobId, JobState.Queued),
            TestData.Job(SecondJobId, JobState.Queued),
        ];
        IReadOnlyList<JobInspectionSummary> fresh =
        [
            TestData.Job(NewJobId, JobState.Queued),
            TestData.Job(FirstJobId, JobState.Queued),
            TestData.Job(SecondJobId, JobState.Queued),
        ];

        var result = DashboardJobListMerge.Merge(displayed, fresh);

        result.Rows.Select(job => job.JobId).ShouldBe([FirstJobId, SecondJobId]);
        result.NewJobIds.ShouldBe([NewJobId]);
        result.NewJobCount.ShouldBe(1);
    }

    [Fact]
    public void LiveMerge_RowNoLongerInResult_StaysUnchanged()
    {
        var staleRow = TestData.Job(SecondJobId, JobState.Queued);
        IReadOnlyList<JobInspectionSummary> displayed =
        [
            TestData.Job(FirstJobId, JobState.Queued),
            staleRow,
        ];
        IReadOnlyList<JobInspectionSummary> fresh =
        [
            TestData.Job(FirstJobId, JobState.Claimed),
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
            TestData.Job(NewJobId, JobState.Queued),
            TestData.Job(FirstJobId, JobState.Queued),
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
            TestData.Job(FirstJobId, JobState.Queued),
            TestData.Job(SecondJobId, JobState.Queued),
        ];
        IReadOnlyList<JobInspectionSummary> page =
        [
            TestData.Job(SecondJobId, JobState.Claimed),
            TestData.Job(ThirdJobId, JobState.Queued),
        ];

        var rows = DashboardJobListMerge.Append(displayed, page);

        rows.Select(job => job.JobId).ShouldBe([FirstJobId, SecondJobId, ThirdJobId]);
        rows[1].State.ShouldBe(JobState.Queued);
    }
}
