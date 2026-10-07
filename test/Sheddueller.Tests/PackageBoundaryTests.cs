namespace Sheddueller.Tests;

using Shouldly;

public sealed class PackageBoundaryTests
{
    [Fact]
    public void DashboardAndPostgres_DoNotReferenceEachOther()
    {
        var root = RepositoryPaths.Root;
        var dashboardProject = File.ReadAllText(Path.Combine(root, "src", "Sheddueller.Dashboard", "Sheddueller.Dashboard.csproj"));
        var postgresProject = File.ReadAllText(Path.Combine(root, "src", "Sheddueller.Postgres", "Sheddueller.Postgres.csproj"));

        dashboardProject.ShouldNotContain("Sheddueller.Postgres");
        postgresProject.ShouldNotContain("Sheddueller.Dashboard");
    }
}
