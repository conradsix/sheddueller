namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobViewSessionTests
{
    private const string FirstId = "ab40ef11-b70d-44c9-8a91-3c356f45da73";
    private const string SecondId = "0f6d2b8e-4c1a-4b7e-9d3f-5a2e8c1b7d90";

    private static readonly ShedduellerDashboardJobView Failures = new("Failures")
    {
        States = [JobState.Failed],
        Sort = JobInspectionSort.NewestFirst,
    };

    [Fact]
    public void ConfiguredDefault_NamedSharedView_IsSelectedAndPreferred()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions
        {
            JobViews = [Failures with { Name = " Failures " }],
            DefaultJobViewName = " failures ",
        });

        session.ConfiguredDefaultView.Key.ShouldBe("shared:Failures");
        session.ConfiguredDefaultView.Name.ShouldBe("Failures");
        session.SelectedKey.ShouldBe("shared:Failures");
        session.PreferredKey.ShouldBe("shared:Failures");
        session.ViewParameter.ShouldBeNull();
    }

    [Fact]
    public void ConfiguredDefault_NoneNamed_IsTheBuiltInView()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions { JobViews = [Failures] });

        session.ConfiguredDefaultView.Key.ShouldBe(DashboardJobViews.BuiltInKey);
        session.CurrentView!.Source.ShouldBe(DashboardJobViewSource.BuiltIn);
    }

    [Fact]
    public void StoredViews_Valid_LoadAsPersonalViewsWithTrimmedNames()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions { JobViews = [Failures] });

        var loaded = session.Load(Payload(Stored(FirstId, " Listings "), Stored(SecondId, "Billing")));

        loaded.ShouldBe(DashboardJobViewStorageLoad.Loaded);
        session.ViewGroups.Select(group => group.Label).ShouldBe(["Built-in", "Shared", "My views"]);
        session.ViewGroups[2].Views.Select(view => view.Name).ShouldBe(["Listings", "Billing"]);
        session.FindView(DashboardJobViews.PersonalKey(FirstId))!.Definition.Name.ShouldBe("Listings");
    }

    [Fact]
    public void StoredViews_NothingStored_LoadsNothing()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());

        session.Load(payload: null).ShouldBe(DashboardJobViewStorageLoad.Empty);
        session.ViewGroups.Select(group => group.Label).ShouldBe(["Built-in"]);
    }

    [Fact]
    public void StoredViews_UnsupportedVersion_AreIgnored()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());
        var payload = Payload(Stored(FirstId, "Listings"));
        payload.Version = DashboardJobViewStoragePayload.CurrentVersion + 1;
        payload.PreferredViewKey = DashboardJobViews.PersonalKey(FirstId);

        session.Load(payload).ShouldBe(DashboardJobViewStorageLoad.UnsupportedVersion);

        session.FindView(DashboardJobViews.PersonalKey(FirstId)).ShouldBeNull();
        session.PreferredKey.ShouldBe(DashboardJobViews.BuiltInKey);
    }

    [Theory]
    [InlineData("not-a-guid", "Listings")]
    [InlineData("AB40EF11B70D44C98A913C356F45DA73", "Listings")]
    [InlineData(FirstId, "default")]
    [InlineData(FirstId, " FAILURES ")]
    [InlineData(FirstId, " ")]
    public void StoredViews_BadIdOrTakenName_AreIgnoredAndTheRestLoad(string id, string name)
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions { JobViews = [Failures] });

        var loaded = session.Load(Payload(Stored(id, name), Stored(SecondId, "Billing")));

        loaded.ShouldBe(DashboardJobViewStorageLoad.SomeViewsInvalid);
        session.Views.Where(view => view.Source == DashboardJobViewSource.Personal).Select(view => view.Name).ShouldBe(["Billing"]);
    }

    [Fact]
    public void StoredViews_RepeatedIdOrName_KeepOnlyTheFirst()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());

        var loaded = session.Load(Payload(
          Stored(FirstId, "Listings"),
          Stored(FirstId, "Other"),
          Stored(SecondId, "LISTINGS")));

        loaded.ShouldBe(DashboardJobViewStorageLoad.SomeViewsInvalid);
        session.Views.Where(view => view.Source == DashboardJobViewSource.Personal).Select(view => view.Name).ShouldBe(["Listings"]);
    }

    [Fact]
    public void StoredViews_InvalidDefinitionOrMissingEntry_AreIgnored()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());
        var withoutJobId = Stored(FirstId, "Listings");
        withoutJobId.View = withoutJobId.View with { Columns = [new(ShedduellerDashboardJobColumnKind.Handler)] };
        var payload = Payload(withoutJobId, Stored(SecondId, "Billing"));
        payload.Views.Add(null!);

        session.Load(payload).ShouldBe(DashboardJobViewStorageLoad.SomeViewsInvalid);

        session.Views.Where(view => view.Source == DashboardJobViewSource.Personal).Select(view => view.Name).ShouldBe(["Billing"]);
    }

    [Fact]
    public void StoredViews_PreferredViewExists_OpensByDefault()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());
        var payload = Payload(Stored(FirstId, "Listings"));
        payload.PreferredViewKey = DashboardJobViews.PersonalKey(FirstId);

        session.Load(payload);

        session.PreferredKey.ShouldBe(DashboardJobViews.PersonalKey(FirstId));
        session.PreferredOrDefaultView.Name.ShouldBe("Listings");
        session.ResolveView(key: null).Name.ShouldBe("Listings");
        session.ResolveView("personal:missing").Name.ShouldBe("Listings");
    }

    [Fact]
    public void StoredViews_PreferredViewMissing_KeepsTheConfiguredDefault()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());
        var payload = Payload(Stored(FirstId, "Listings"));
        payload.PreferredViewKey = DashboardJobViews.PersonalKey(SecondId);

        session.Load(payload);

        session.PreferredKey.ShouldBe(DashboardJobViews.BuiltInKey);
    }

    [Fact]
    public void ViewName_TakenIgnoringCase_IsRejectedUnlessItIsTheViewBeingRenamed()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions { JobViews = [Failures] });

        session.ValidateViewName("FAILURES", exceptKey: null).ShouldBe("A view named “FAILURES” already exists.");
        session.ValidateViewName("failures", exceptKey: "shared:Failures").ShouldBeNull();
        session.ValidateViewName("Listings", exceptKey: null).ShouldBeNull();
    }

    [Fact]
    public void Dirty_FiltersOrColumnsChanged_IsReportedUntilUndone()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());
        var columns = DashboardJobViews.BuiltInColumns.ToList();
        var filters = session.CurrentViewFilters();

        session.UpdateDirty(filters, columns);
        session.IsDirty.ShouldBeFalse();

        filters.SetHandlerContains("  ");
        session.UpdateDirty(filters, columns);
        session.IsDirty.ShouldBeFalse();

        filters.SetStateSelected(JobState.Failed, selected: true);
        session.UpdateDirty(filters, columns);
        session.IsDirty.ShouldBeTrue();
        session.CanSetPreferredView.ShouldBeFalse();

        filters.SetStateSelected(JobState.Failed, selected: false);
        columns.RemoveAt(columns.Count - 1);
        session.UpdateDirty(filters, columns);
        session.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void SaveAs_NewPersonalView_IsSelectedAndStored()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());
        var filters = new DashboardJobFilters();
        filters.SetTagContains("tenant:acme");

        var view = session.SaveAs("Acme", filters, DashboardJobViews.BuiltInColumns);

        session.SelectedKey.ShouldBe(view.Key);
        session.IsPersonalView.ShouldBeTrue();
        session.ToPayload().Views.Single().View.TagContains.ShouldBe("tenant:acme");
        session.SuggestedViewName.ShouldBe("Acme copy");
    }

    [Fact]
    public void SaveCurrent_PersonalViewWithChanges_SavesTheNewDefinition()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());
        var filters = new DashboardJobFilters();
        session.SaveAs("Acme", filters, DashboardJobViews.BuiltInColumns);

        session.SaveCurrent(filters, DashboardJobViews.BuiltInColumns).ShouldBeNull();

        filters.SetStateSelected(JobState.Queued, selected: true);
        session.UpdateDirty(filters, DashboardJobViews.BuiltInColumns);
        session.SaveCurrent(filters, DashboardJobViews.BuiltInColumns)!.Definition.States.ShouldBe([JobState.Queued]);
        session.UpdateDirty(filters, DashboardJobViews.BuiltInColumns);
        session.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void PersonalViewActions_BuiltInViewSelected_DoNothing()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());

        session.RenameCurrent("Renamed").ShouldBeNull();
        session.DeleteCurrent().ShouldBeNull();
        session.SetCurrentAsPreferred().ShouldBeNull();
        session.SuggestedViewName.ShouldBe("My view");
    }

    [Fact]
    public void Rename_PersonalView_KeepsItsKey()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions());
        var view = session.SaveAs("Acme", new DashboardJobFilters(), DashboardJobViews.BuiltInColumns);

        session.RenameCurrent("Acme jobs")!.Key.ShouldBe(view.Key);

        session.FindView(view.Key)!.Name.ShouldBe("Acme jobs");
        session.ToPayload().Views.Single().View.Name.ShouldBe("Acme jobs");
    }

    [Fact]
    public void Delete_PreferredPersonalView_ConfiguredDefaultOpensByDefaultAgain()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions { JobViews = [Failures], DefaultJobViewName = "Failures" });
        var view = session.SaveAs("Acme", new DashboardJobFilters(), DashboardJobViews.BuiltInColumns);
        session.SetCurrentAsPreferred()!.Key.ShouldBe(view.Key);

        session.DeleteCurrent()!.Name.ShouldBe("Acme");

        session.FindView(view.Key).ShouldBeNull();
        session.PreferredKey.ShouldBe("shared:Failures");
        session.ToPayload().Views.ShouldBeEmpty();
    }

    [Fact]
    public void ViewParameter_ViewOtherThanPreferredDefault_IsNamed()
    {
        var session = new DashboardJobViewSession(new ShedduellerDashboardOptions { JobViews = [Failures] });

        session.Select(session.FindView("shared:Failures")!);

        session.ViewParameter.ShouldBe("shared:Failures");
        session.CurrentViewFilters().SelectedStates.ShouldBe([JobState.Failed]);
    }

    [Theory]
    [InlineData("http://localhost/", "sheddueller.dashboard.jobViews.v1:")]
    [InlineData("http://localhost/ops/sheddueller/", "sheddueller.dashboard.jobViews.v1:/ops/sheddueller")]
    public void StorageKey_DashboardPath_SeparatesDashboardsOnOneHost(string baseUri, string expected)
      => DashboardJobViewSession.StorageKey(baseUri).ShouldBe(expected);

    private static DashboardJobViewStoragePayload Payload(params DashboardStoredJobView[] views)
      => new() { Views = [.. views] };

    private static DashboardStoredJobView Stored(string id, string name)
      => new()
      {
          Id = id,
          View = new ShedduellerDashboardJobView(name)
          {
              Columns = [new(ShedduellerDashboardJobColumnKind.JobId), new(ShedduellerDashboardJobColumnKind.Handler)],
          },
      };
}
