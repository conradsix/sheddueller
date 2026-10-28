namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardInvocationViewTests
{
    private const string StringType = "System.String, System.Private.CoreLib";
    private const string IntType = "System.Int32, System.Private.CoreLib";

    [Fact]
    public void HeaderCall_SchedulerSuppliedArguments_AreLeftOut()
    {
        var arguments = DashboardInvocationView.Arguments(GroupHoldInvocation(), ["groupKey", "progress", "cancellationToken"]);

        var tokens = DashboardInvocationView.CallTokens(
          receiver: null,
          "RunGroupHoldAsync",
          arguments,
          valuesAvailable: true,
          dataOnly: true,
          maxValueLength: 60,
          oneArgumentPerLine: false);

        DashboardInvocationView.Text(tokens).ShouldBe("RunGroupHoldAsync(groupKey: \"blocking-2\")");
    }

    [Fact]
    public void FullCall_SchedulerSuppliedArguments_AreShownMutedOnePerLine()
    {
        var arguments = DashboardInvocationView.Arguments(GroupHoldInvocation(), ["groupKey", "progress", "cancellationToken"]);

        var tokens = DashboardInvocationView.CallTokens(
          "DemoJobService",
          "RunGroupHoldAsync",
          arguments,
          valuesAvailable: true,
          dataOnly: false,
          maxValueLength: null,
          oneArgumentPerLine: true);

        DashboardInvocationView.Text(tokens).ShouldBe(
          """
          DemoJobService.RunGroupHoldAsync(
              groupKey: "blocking-2",
              progress: IProgress<Decimal>,
              cancellationToken: CancellationToken
          )
          """.ReplaceLineEndings("\n"));
        tokens.ShouldContain(new CodeToken("groupKey", CodeTokenKind.Parameter));
        tokens.ShouldContain(new CodeToken("\"blocking-2\"", CodeTokenKind.StringLiteral));
        tokens.ShouldContain(new CodeToken("cancellationToken", CodeTokenKind.Muted));
        tokens.ShouldContain(new CodeToken("IProgress<Decimal>", CodeTokenKind.Muted));
    }

    [Fact]
    public void SchedulerSuppliedArguments_ContextAndServices_ShowWhatTheSchedulerPasses()
    {
        var invocation = Invocation(
          new JobInvocationParameterInspection(0, "Sheddueller.JobContext, Sheddueller", new(JobMethodParameterBindingKind.JobContext)),
          new JobInvocationParameterInspection(1, "Acme.IClock, Acme", new(JobMethodParameterBindingKind.Service, "Acme.IClock, Acme")));
        var arguments = DashboardInvocationView.Arguments(invocation, parameterNames: null);

        var text = DashboardInvocationView.CallText(receiver: null, "Run", arguments, valuesAvailable: true, dataOnly: false);

        text.ShouldBe("Run(Job.Context, Job.Resolve<IClock>())");
        arguments.ShouldAllBe(argument => !argument.IsData);
    }

    [Fact]
    public void Call_ParameterNamesUnresolved_ShowsPositionalArguments()
    {
        var arguments = DashboardInvocationView.Arguments(GroupHoldInvocation(), parameterNames: null);

        var text = DashboardInvocationView.CallText(receiver: null, "RunGroupHoldAsync", arguments, valuesAvailable: true, dataOnly: true);

        text.ShouldBe("RunGroupHoldAsync(\"blocking-2\")");
        arguments.ShouldAllBe(argument => argument.Name == null);
    }

    [Theory]
    [InlineData("42", "42", CodeTokenKind.NumberLiteral)]
    [InlineData("-1.5e3", "-1.5e3", CodeTokenKind.NumberLiteral)]
    [InlineData("true", "true", CodeTokenKind.Keyword)]
    [InlineData("null", "null", CodeTokenKind.Keyword)]
    [InlineData("\"caf\\u00e9\\t\\\"x\\\"\"", "\"café\\t\\\"x\\\"\"", CodeTokenKind.StringLiteral)]
    public void ArgumentValue_ScalarJson_RendersAsCSharpLiteral(string json, string expected, CodeTokenKind kind)
    {
        var arguments = DashboardInvocationView.Arguments(Invocation(Data(0, StringType, json)), ["value"]);

        var tokens = DashboardInvocationView.CallTokens(
          receiver: null,
          "Run",
          arguments,
          valuesAvailable: true,
          dataOnly: true,
          maxValueLength: null,
          oneArgumentPerLine: false);

        tokens.ShouldContain(new CodeToken(expected, kind));
    }

    [Fact]
    public void ArgumentValue_Object_RendersAsCompactHighlightedJson()
    {
        var arguments = DashboardInvocationView.Arguments(
          Invocation(Data(0, "Acme.RateRequest, Acme", /*lang=json*/ """{ "propertyId": 5, "tags": ["a", "b"] }""")),
          ["request"]);

        var tokens = DashboardInvocationView.CallTokens(
          receiver: null,
          "LoadRates",
          arguments,
          valuesAvailable: true,
          dataOnly: true,
          maxValueLength: null,
          oneArgumentPerLine: false);

        DashboardInvocationView.Text(tokens).ShouldBe("""LoadRates(request: {"propertyId":5,"tags":["a","b"]})""");
        tokens.ShouldContain(new CodeToken("\"propertyId\"", CodeTokenKind.Property));
        tokens.ShouldContain(new CodeToken("5", CodeTokenKind.NumberLiteral));
    }

    [Fact]
    public void HeaderCall_LongValue_IsCutWithAnEllipsis()
    {
        var value = new string('a', 100);
        var arguments = DashboardInvocationView.Arguments(Invocation(Data(0, StringType, $"\"{value}\"")), ["value"]);

        var tokens = DashboardInvocationView.CallTokens(
          receiver: null,
          "Run",
          arguments,
          valuesAvailable: true,
          dataOnly: true,
          maxValueLength: 20,
          oneArgumentPerLine: false);

        DashboardInvocationView.Text(tokens).ShouldBe($"Run(value: \"{new string('a', 19)}…)");
        tokens[^2].ShouldBe(new CodeToken("…", CodeTokenKind.Muted));
        DashboardInvocationView.CallText(receiver: null, "Run", arguments, valuesAvailable: true, dataOnly: true)
          .ShouldBe($"Run(value: \"{value}\")");
    }

    [Fact]
    public void ArgumentValues_PayloadNotDisplayable_ShowAnEllipsis()
    {
        var arguments = DashboardInvocationView.Arguments(Invocation(Data(0, IntType, null)), ["propertyId"]);

        var text = DashboardInvocationView.CallText(receiver: null, "LoadRates", arguments, valuesAvailable: false, dataOnly: true);

        text.ShouldBe("LoadRates(propertyId: …)");
    }

    [Fact]
    public void CopyText_ManyDataArguments_PutsOneArgumentPerLine()
    {
        var arguments = DashboardInvocationView.Arguments(
          Invocation(
            Data(0, IntType, "5"),
            Data(1, StringType, "\"2026-11-01\""),
            Data(2, StringType, "\"GBP\""),
            new JobInvocationParameterInspection(3, "System.Threading.CancellationToken", new(JobMethodParameterBindingKind.CancellationToken))),
          ["propertyId", "from", "currency", "cancellationToken"]);

        var text = DashboardInvocationView.CallText("RateLoader", "LoadRates", arguments, valuesAvailable: true, dataOnly: true);

        text.ShouldBe(
          """
          RateLoader.LoadRates(
              propertyId: 5,
              from: "2026-11-01",
              currency: "GBP"
          )
          """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void HeaderArguments_EachGroupKeepsItsComma()
    {
        var arguments = DashboardInvocationView.Arguments(
          Invocation(Data(0, IntType, "5"), Data(1, StringType, "\"2026-11-01\"")),
          ["propertyId", "from"]);

        var groups = DashboardInvocationView.ArgumentGroups(arguments, valuesAvailable: true, dataOnly: true, maxValueLength: 60);

        groups.Select(DashboardInvocationView.Text).ShouldBe(["propertyId: 5,", "from: \"2026-11-01\""]);
    }

    [Fact]
    public void JsonValue_Nested_IsIndentedAndHighlighted()
    {
        var tokens = DashboardInvocationView.JsonTokens(/*lang=json*/ """{"a":1,"b":[true,null],"c":{},"d":"x"}""");

        var expected = /*lang=json*/ """
          {
            "a": 1,
            "b": [
              true,
              null
            ],
            "c": {},
            "d": "x"
          }
          """;

        DashboardInvocationView.Text(tokens).ShouldBe(expected.ReplaceLineEndings("\n"));
        tokens.ShouldContain(new CodeToken("\"a\"", CodeTokenKind.Property));
        tokens.ShouldContain(new CodeToken("\"x\"", CodeTokenKind.StringLiteral));
        tokens.ShouldContain(new CodeToken("null", CodeTokenKind.Keyword));
    }

    [Fact]
    public void JsonValue_Invalid_IsShownAsPlainText()
        => DashboardInvocationView.JsonTokens("{not json").ShouldBe([new CodeToken("{not json", CodeTokenKind.Plain)]);

    private static JobInvocationInspection GroupHoldInvocation()
      => Invocation(
        Data(0, StringType, "\"blocking-2\""),
        new JobInvocationParameterInspection(1, "System.IProgress`1[[System.Decimal]]", new(JobMethodParameterBindingKind.ProgressReporter)),
        new JobInvocationParameterInspection(2, "System.Threading.CancellationToken", new(JobMethodParameterBindingKind.CancellationToken)));

    private static JobInvocationParameterInspection Data(int index, string type, string? json)
      => new(index, type, new JobMethodParameterBinding(JobMethodParameterBindingKind.Serialized), json);

    private static JobInvocationInspection Invocation(params JobInvocationParameterInspection[] parameters)
      => new(
        JobInvocationTargetKind.Instance,
        "Acme.Jobs.DemoJobService, Acme.Jobs",
        "Run",
        ReconstructedCall: "Run()",
        parameters,
        SerializedArgumentsContentType: "application/json",
        SerializedArgumentsByteCount: 0,
        JobSerializedArgumentsInspectionStatus.Displayable);
}
