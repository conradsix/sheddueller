namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobActionsTests
{
    private static readonly Guid JobId = Guid.Parse("7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3");

    [Theory]
    [InlineData("7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3")]
    [InlineData("  7C1E9A42-5B3D-4F1E-9A07-2D6C8E41B0F3  ")]
    [InlineData("7c1e9a425b3d4f1e9a072d6c8e41b0f3")]
    [InlineData("{7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3}")]
    public void JobSearch_FullJobId_IsRecognised(string text)
    {
        DashboardJobActions.TryParseJobId(text, out var jobId).ShouldBeTrue();

        jobId.ShouldBe(JobId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TenantBillingSync")]
    [InlineData("7c1e9a42")]
    [InlineData("7c1e9a42-5b3d-4f1e-9a07")]
    public void JobSearch_HandlerTextOrPartialId_IsNotAJobId(string? text)
        => DashboardJobActions.TryParseJobId(text, out _).ShouldBeFalse();

    [Theory]
    [InlineData(JobState.Queued, false, true)]
    [InlineData(JobState.Claimed, false, true)]
    [InlineData(JobState.Claimed, true, false)]
    [InlineData(JobState.Completed, false, false)]
    [InlineData(JobState.Failed, false, false)]
    [InlineData(JobState.Canceled, false, false)]
    public void Cancellation_JobState_DeterminesWhetherCancelIsOffered(JobState state, bool cancellationRequested, bool expected)
    {
        var job = TestData.Job(state: state) with
        {
            CancellationRequestedAtUtc = cancellationRequested ? DateTimeOffset.UnixEpoch : null,
        };

        DashboardJobActions.IsCancellable(job).ShouldBe(expected);
    }

    [Fact]
    public void CancellationSummary_MixedOutcomes_ReportsEachKind()
    {
        var (title, description) = DashboardJobActions.DescribeCancellation(
        [
            JobCancellationResult.Canceled,
            JobCancellationResult.Canceled,
            JobCancellationResult.CancellationRequested,
            JobCancellationResult.AlreadyFinished,
            JobCancellationResult.NotFound,
        ]);

        title.ShouldBe("Canceled 2 jobs; cancellation requested for 1 job");
        description.ShouldBe("The running job stops at its next cancellation check. 1 job had already finished. 1 job no longer exists.");
    }

    [Fact]
    public void CancellationSummary_SingleQueuedJob_HasNoExtraDetail()
    {
        var (title, description) = DashboardJobActions.DescribeCancellation([JobCancellationResult.Canceled]);

        title.ShouldBe("Canceled 1 job");
        description.ShouldBeNull();
    }

    [Fact]
    public void CancellationSummary_NothingCanceled_SaysSo()
    {
        var (title, description) = DashboardJobActions.DescribeCancellation(
          [JobCancellationResult.AlreadyFinished, JobCancellationResult.AlreadyFinished]);

        title.ShouldBe("No jobs were canceled");
        description.ShouldBe("2 jobs had already finished.");
    }
}
