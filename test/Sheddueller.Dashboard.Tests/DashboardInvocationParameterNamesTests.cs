namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardInvocationParameterNamesTests
{
    private static readonly string IntType = typeof(int).AssemblyQualifiedName!;
    private static readonly string StringType = typeof(string).AssemblyQualifiedName!;
    private static readonly string TokenType = typeof(CancellationToken).AssemblyQualifiedName!;

    private interface IRateLoader
    {
        Task LoadRates(int propertyId, string from, CancellationToken cancellationToken);

        Task LoadRates(string code, CancellationToken cancellationToken);
    }

    [Fact]
    public void InstanceMethod_Overloads_ResolvesTheOverloadWithTheStoredParameterTypes()
    {
        DashboardInvocationParameterNames
          .TryResolve(JobInvocationTargetKind.Instance, typeof(IRateLoader).AssemblyQualifiedName!, nameof(IRateLoader.LoadRates), [IntType, StringType, TokenType])
          .ShouldBe(["propertyId", "from", "cancellationToken"]);
        DashboardInvocationParameterNames
          .TryResolve(JobInvocationTargetKind.Instance, typeof(IRateLoader).AssemblyQualifiedName!, nameof(IRateLoader.LoadRates), [StringType, TokenType])
          .ShouldBe(["code", "cancellationToken"]);
    }

    [Fact]
    public void StaticMethod_KnownType_ResolvesNames()
        => DashboardInvocationParameterNames
          .TryResolve(JobInvocationTargetKind.Static, typeof(RateMath).AssemblyQualifiedName!, nameof(RateMath.Scale), [typeof(decimal).AssemblyQualifiedName!, IntType])
          .ShouldBe(["rate", "factor"]);

    [Fact]
    public void ParameterType_NotLoadable_FallsBackToTheOnlyOverloadWithThatArity()
        => DashboardInvocationParameterNames
          .TryResolve(JobInvocationTargetKind.Instance, typeof(IRateLoader).AssemblyQualifiedName!, nameof(IRateLoader.LoadRates), [IntType, "Acme.Missing, Acme.Missing", TokenType])
          .ShouldBe(["propertyId", "from", "cancellationToken"]);

    [Fact]
    public void JobMethod_NotLoadable_ReturnsNull()
    {
        DashboardInvocationParameterNames
          .TryResolve(JobInvocationTargetKind.Instance, "Acme.Missing, Acme.Missing", "LoadRates", [IntType])
          .ShouldBeNull();
        DashboardInvocationParameterNames
          .TryResolve(JobInvocationTargetKind.Instance, typeof(IRateLoader).AssemblyQualifiedName!, "Missing", [IntType])
          .ShouldBeNull();
        DashboardInvocationParameterNames
          .TryResolve(JobInvocationTargetKind.Static, typeof(IRateLoader).AssemblyQualifiedName!, nameof(IRateLoader.LoadRates), [StringType, TokenType])
          .ShouldBeNull();
    }

    [Fact]
    public void Resolve_SameJobMethod_ReusesTheCachedNames()
    {
        var invocation = new JobInvocationInspection(
          JobInvocationTargetKind.Static,
          typeof(RateMath).AssemblyQualifiedName!,
          nameof(RateMath.Scale),
          ReconstructedCall: "RateMath.Scale(1.5, 2)",
          [
              new JobInvocationParameterInspection(1, IntType, new(JobMethodParameterBindingKind.Serialized), "2"),
              new JobInvocationParameterInspection(0, typeof(decimal).AssemblyQualifiedName!, new(JobMethodParameterBindingKind.Serialized), "1.5"),
          ],
          SerializedArgumentsContentType: "application/json",
          SerializedArgumentsByteCount: 8,
          JobSerializedArgumentsInspectionStatus.Displayable);

        var first = DashboardInvocationParameterNames.Resolve(invocation);

        first.ShouldBe(["rate", "factor"]);
        DashboardInvocationParameterNames.Resolve(invocation with { ReconstructedCall = "other" }).ShouldBeSameAs(first);
    }

    private static class RateMath
    {
        public static decimal Scale(decimal rate, int factor)
          => rate * factor;
    }
}
