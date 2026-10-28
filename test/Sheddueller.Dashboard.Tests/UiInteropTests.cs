namespace Sheddueller.Dashboard.Tests;

using System.Diagnostics.CodeAnalysis;

using Microsoft.JSInterop;

using Sheddueller.Dashboard.Components.Ui;

using Shouldly;

public sealed class UiInteropTests
{
    [Fact]
    public async Task BestEffortCall_ScriptRuns_ReportsCompletionAndPassesArguments()
    {
        var js = new FakeJSRuntime();

        var completed = await js.TryInvokeVoidAsync("sheddueller.scrollIntoView", "element", 3);

        completed.ShouldBeTrue();
        js.Identifier.ShouldBe("sheddueller.scrollIntoView");
        js.Arguments.ShouldBe(["element", 3]);
    }

    [Fact]
    public async Task BestEffortCall_CircuitDisconnected_IsIgnored()
    {
        var js = new FakeJSRuntime(new JSDisconnectedException("The circuit is gone."));

        var completed = await js.TryInvokeVoidAsync("sheddueller.throughputChart.destroy");

        completed.ShouldBeFalse();
    }

    [Fact]
    public async Task BestEffortCall_ScriptThrows_IsIgnored()
    {
        var js = new FakeJSRuntime(new JSException("showModal failed."));

        var completed = await js.TryInvokeVoidAsync("sheddueller.dialog.show");

        completed.ShouldBeFalse();
    }

    [Fact]
    public async Task BestEffortCall_OtherFailure_Propagates()
    {
        var js = new FakeJSRuntime(new InvalidOperationException("JavaScript interop calls cannot be issued at this time."));

        await Should.ThrowAsync<InvalidOperationException>(async () => await js.TryInvokeVoidAsync("sheddueller.dialog.attach"));
    }

    private sealed class FakeJSRuntime(Exception? failure = null) : IJSRuntime
    {
        public string? Identifier { get; private set; }

        public object?[]? Arguments { get; private set; }

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(
            string identifier,
            object?[]? args)
          => this.InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            this.Identifier = identifier;
            this.Arguments = args;
            return failure is null
              ? ValueTask.FromResult(default(TValue)!)
              : ValueTask.FromException<TValue>(failure);
        }
    }
}
