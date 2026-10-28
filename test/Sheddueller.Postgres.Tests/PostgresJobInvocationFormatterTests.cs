namespace Sheddueller.Postgres.Tests;

using System.Text;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Postgres.Internal.Operations;
using Sheddueller.Serialization;
using Sheddueller.Storage;

using Shouldly;

public sealed class PostgresJobInvocationFormatterTests
{
    private const string ServiceType = "Acme.Billing.InvoiceJobs, Acme";
    private static readonly string IntType = typeof(int).AssemblyQualifiedName!;
    private static readonly string StringType = typeof(string).AssemblyQualifiedName!;
    private static readonly string CancellationTokenType = typeof(CancellationToken).AssemblyQualifiedName!;

    [Fact]
    public void Invocation_JsonArguments_ShowsEachSerializedValue()
    {
        var invocation = Format([IntType, StringType], storedBindings: null, Json("""[42,"hello"]"""));

        invocation.SerializedArgumentsStatus.ShouldBe(JobSerializedArgumentsInspectionStatus.Displayable);
        invocation.SerializedArgumentsStatusMessage.ShouldBeNull();
        invocation.Parameters.Select(static parameter => parameter.SerializedValueJson).ShouldBe(["42", "\"hello\""]);
        invocation.Parameters.Select(static parameter => parameter.ParameterIndex).ShouldBe([0, 1]);
        invocation.ReconstructedCall.ShouldBe("InvoiceJobs.Send(42, \"hello\")");
        invocation.TargetKind.ShouldBe(JobInvocationTargetKind.Static);
        invocation.ServiceType.ShouldBe(ServiceType);
        invocation.MethodName.ShouldBe("Send");
        invocation.SerializedArgumentsContentType.ShouldBe(SystemTextJsonJobPayloadSerializer.JsonContentType);
        invocation.SerializedArgumentsByteCount.ShouldBe(Encoding.UTF8.GetByteCount("""[42,"hello"]"""));
    }

    [Fact]
    public void Invocation_StoredServiceAndCancellationBindings_ShowsValuesOnlyForSerializedParameters()
    {
        var invocation = Format(
          [IntType, "Acme.IClock, Acme", CancellationTokenType],
          [
              new JobMethodParameterBinding(JobMethodParameterBindingKind.Serialized),
              new JobMethodParameterBinding(JobMethodParameterBindingKind.Service, "Acme.IClock, Acme"),
              new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
          ],
          Json("[7]"));

        invocation.SerializedArgumentsStatus.ShouldBe(JobSerializedArgumentsInspectionStatus.Displayable);
        invocation.Parameters.Select(static parameter => parameter.SerializedValueJson).ShouldBe(["7", null, null]);
        invocation.Parameters[1].Binding.Kind.ShouldBe(JobMethodParameterBindingKind.Service);
        invocation.ReconstructedCall.ShouldContain("Job.Resolve<IClock>()");
        invocation.ReconstructedCall.ShouldContain("CancellationToken");
    }

    [Fact]
    public void Invocation_NoStoredBindings_InfersFrameworkParametersFromTheirTypes()
    {
        var invocation = Format([StringType, CancellationTokenType], storedBindings: null, Json("""["x"]"""));

        invocation.SerializedArgumentsStatus.ShouldBe(JobSerializedArgumentsInspectionStatus.Displayable);
        invocation.Parameters.Select(static parameter => parameter.Binding.Kind)
          .ShouldBe([JobMethodParameterBindingKind.Serialized, JobMethodParameterBindingKind.CancellationToken]);
        invocation.Parameters.Select(static parameter => parameter.SerializedValueJson).ShouldBe(["\"x\"", null]);
    }

    [Fact]
    public void Invocation_UnsupportedContentType_ReportsStatusWithoutValues()
    {
        var invocation = Format([IntType], storedBindings: null, new SerializedJobPayload("application/x-custom", [1, 2, 3]));

        invocation.SerializedArgumentsStatus.ShouldBe(JobSerializedArgumentsInspectionStatus.UnsupportedContentType);
        invocation.SerializedArgumentsStatusMessage.ShouldBe("Serialized arguments use unsupported content type 'application/x-custom'.");
        invocation.SerializedArgumentsByteCount.ShouldBe(3);
        invocation.Parameters.Single().SerializedValueJson.ShouldBeNull();
    }

    [Fact]
    public void Invocation_PayloadOverDisplayLimit_ReportsTooLargeWithoutParsing()
    {
        var data = new byte[PostgresJobInvocationFormatter.SerializedArgumentDisplayByteLimit + 1];
        var invocation = Format([IntType], storedBindings: null, new SerializedJobPayload(SystemTextJsonJobPayloadSerializer.JsonContentType, data));

        invocation.SerializedArgumentsStatus.ShouldBe(JobSerializedArgumentsInspectionStatus.TooLarge);
        invocation.SerializedArgumentsStatusMessage.ShouldBe("Serialized arguments are 65,537 bytes, exceeding the 65,536 byte display limit.");
        invocation.Parameters.Single().SerializedValueJson.ShouldBeNull();
    }

    [Theory]
    [InlineData(/*lang=json*/ """{"value":1}""", "Serialized arguments payload is not a JSON array.")]
    [InlineData("[1,", "Serialized arguments payload is not valid JSON.")]
    public void Invocation_PayloadNotAJsonArray_ReportsInvalidPayload(string json, string expectedMessage)
    {
        var invocation = Format([IntType], storedBindings: null, Json(json));

        invocation.SerializedArgumentsStatus.ShouldBe(JobSerializedArgumentsInspectionStatus.InvalidPayload);
        invocation.SerializedArgumentsStatusMessage.ShouldBe(expectedMessage);
        invocation.Parameters.Single().SerializedValueJson.ShouldBeNull();
    }

    [Fact]
    public void Invocation_ArgumentCountDiffersFromSerializedParameters_ReportsMismatch()
    {
        var invocation = Format([IntType, CancellationTokenType], storedBindings: null, Json("[1,2]"));

        invocation.SerializedArgumentsStatus.ShouldBe(JobSerializedArgumentsInspectionStatus.ArgumentCountMismatch);
        invocation.SerializedArgumentsStatusMessage.ShouldBe("Serialized arguments contain 2 values for 1 serialized parameters.");
        invocation.Parameters.ShouldAllBe(parameter => parameter.SerializedValueJson == null);
    }

    private static JobInvocationInspection Format(
        IReadOnlyList<string> parameterTypes,
        IReadOnlyList<JobMethodParameterBinding>? storedBindings,
        SerializedJobPayload payload)
      => PostgresJobInvocationFormatter.Format(
        JobInvocationTargetKind.Static,
        ServiceType,
        "Send",
        parameterTypes,
        storedBindings,
        payload);

    private static SerializedJobPayload Json(string json)
      => new(SystemTextJsonJobPayloadSerializer.JsonContentType, Encoding.UTF8.GetBytes(json));
}
