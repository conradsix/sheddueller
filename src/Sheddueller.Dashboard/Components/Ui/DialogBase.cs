namespace Sheddueller.Dashboard.Components.Ui;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

/// <summary>
/// Shared behaviour for components built on a native modal <c>&lt;dialog&gt;</c>: opening and closing
/// follow <see cref="Open" />, and closing from the browser (Esc, click outside) flows back through
/// <see cref="OpenChanged" />.
/// </summary>
public abstract class DialogBase : ComponentBase, IAsyncDisposable
{
    private DotNetObjectReference<DialogBase>? _reference;
    private bool _shown;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Parameter]
    public bool Open { get; set; }

    [Parameter]
    public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>
    /// Raised after the dialog closes, however it was closed.
    /// </summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    protected ElementReference Element { get; set; }

    protected string TitleId { get; } = string.Concat("sd-dialog-", Guid.NewGuid().ToString("N").AsSpan(0, 8));

    [JSInvokable("OnDialogClosed")]
    public async Task OnDialogClosedAsync()
    {
        this._shown = false;
        if (this.Open)
        {
            this.Open = false;
            await this.OpenChanged.InvokeAsync(false);
        }

        await this.OnClose.InvokeAsync();
    }

    public ValueTask DisposeAsync()
    {
        this._reference?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Closes the dialog from component code, for example after a confirm button runs.
    /// </summary>
    protected async Task CloseAsync()
    {
        if (!this.Open)
        {
            return;
        }

        this.Open = false;
        await this.OpenChanged.InvokeAsync(false);
        this.StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            this._reference = DotNetObjectReference.Create(this);
            await this.JS.InvokeVoidAsync("sheddueller.dialog.attach", this.Element, this._reference);
        }

        if (this.Open != this._shown)
        {
            this._shown = this.Open;
            await this.JS.InvokeVoidAsync(this.Open ? "sheddueller.dialog.show" : "sheddueller.dialog.close", this.Element);
        }
    }
}
