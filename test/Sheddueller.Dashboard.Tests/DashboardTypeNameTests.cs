namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardTypeNameTests
{
    [Fact]
    public void ShortTypeName_AssemblyQualifiedNames_DropsNamespacesAndAssemblies()
    {
        DashboardTypeName.Short(typeof(CancellationToken).AssemblyQualifiedName!).ShouldBe("CancellationToken");
        DashboardTypeName.Short(typeof(IProgress<decimal>).AssemblyQualifiedName!).ShouldBe("IProgress<Decimal>");
        DashboardTypeName.Short(typeof(Dictionary<string, List<int>>).AssemblyQualifiedName!).ShouldBe("Dictionary<String, List<Int32>>");
        DashboardTypeName.Short(typeof(int?[]).AssemblyQualifiedName!).ShouldBe("Nullable<Int32>[]");
        DashboardTypeName.Short(typeof(string[,]).AssemblyQualifiedName!).ShouldBe("String[,]");
        DashboardTypeName.Short("System.Collections.Generic.List`1[System.Int32]").ShouldBe("List<Int32>");
        DashboardTypeName.Short("Acme.Jobs.RateLoader, Acme.Jobs").ShouldBe("RateLoader");
    }
}
