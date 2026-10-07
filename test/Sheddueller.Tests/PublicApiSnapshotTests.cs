namespace Sheddueller.Tests;

using System.Reflection;

using Shouldly;

public sealed class PublicApiSnapshotTests
{
    public static IEnumerable<object[]> Assemblies
      => new[]
      {
          typeof(ShedduellerOptions).Assembly,
          typeof(Dashboard.ShedduellerDashboardOptions).Assembly,
          typeof(Postgres.ShedduellerPostgresOptions).Assembly,
          typeof(Testing.FakeJobEnqueuer).Assembly,
          typeof(Microsoft.Extensions.DependencyInjection.ShedduellerWorkerServiceCollectionExtensions).Assembly,
      }.Select(assembly => new object[] { assembly.GetName().Name! });

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void PublicApi_CurrentAssembly_MatchesBaseline(string assemblyName)
    {
        var actual = PublicApiSnapshot.Create(Assembly.Load(assemblyName));
        var path = Path.Combine(RepositoryPaths.Root, "test", "Sheddueller.Tests", "PublicApi", assemblyName + ".txt");
        if (Environment.GetEnvironmentVariable("SHEDDUELLER_UPDATE_API_BASELINE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        actual.ShouldBe(File.ReadAllText(path).ReplaceLineEndings("\n"), "Public API changes require an intentional baseline review.");
    }
}
