namespace Sheddueller.Dashboard.Components;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using Sheddueller.Dashboard;
using Sheddueller.Dashboard.Internal;

public partial class JobColumnConfigurator
{
    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public IReadOnlyList<ShedduellerDashboardJobColumn> Columns { get; set; } = [];

    [Parameter]
    public IReadOnlyList<ShedduellerDashboardJobColumnKind> HiddenBuiltInColumns { get; set; } = [];

    [Parameter]
    public IReadOnlyList<string> ObservedTagNames { get; set; } = [];

    [Parameter]
    public bool IsPersonalView { get; set; }

    [Parameter]
    public bool IsDirty { get; set; }

    [Parameter]
    public EventCallback CloseRequested { get; set; }

    [Parameter]
    public EventCallback SaveRequested { get; set; }

    [Parameter]
    public EventCallback SaveAsRequested { get; set; }

    [Parameter]
    public EventCallback ResetRequested { get; set; }

    [Parameter]
    public EventCallback AutoFitRequested { get; set; }

    [Parameter]
    public EventCallback ResetWidthsRequested { get; set; }

    [Parameter]
    public EventCallback<(int FromIndex, int ToIndex)> ColumnMoved { get; set; }

    [Parameter]
    public EventCallback<(ShedduellerDashboardJobColumnKind Kind, bool Visible)> VisibilityChanged { get; set; }

    [Parameter]
    public EventCallback<(int Index, ShedduellerDashboardJobColumn Column)> TagColumnEdited { get; set; }

    [Parameter]
    public EventCallback<int> TagColumnRemoved { get; set; }

    [Parameter]
    public EventCallback<ShedduellerDashboardJobColumn> TagColumnAdded { get; set; }

    [Parameter]
    public EventCallback<int> ColumnWidthResetRequested { get; set; }

    private ElementReference _closeButton;
    private DotNetObjectReference<JobColumnConfigurator>? _selfReference;
    private int? _dragSourceIndex;
    private int? _dragTargetIndex;
    private int? _editingIndex;
    private string _editTagName = string.Empty;
    private string _editHeading = string.Empty;
    private string _newTagName = string.Empty;
    private string _newTagHeading = string.Empty;
    private string? _editError;
    private string? _addError;
    private string? _announcement;
    private bool _isAddingTag;
    private bool _wasOpen;
    private bool _escapeRegistered;

    protected override void OnParametersSet()
    {
        if (this._editingIndex is { } index
            && (index < 0 || index >= this.Columns.Count || this.Columns[index].Kind != ShedduellerDashboardJobColumnKind.Tag))
        {
            this.CancelTagEdit();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _ = firstRender;

        if (this.IsOpen && !this._wasOpen)
        {
            this._wasOpen = true;
            await this._closeButton.FocusAsync();
        }
        else if (!this.IsOpen)
        {
            this._wasOpen = false;
        }

        if (this.IsOpen && !this._escapeRegistered)
        {
            this._selfReference ??= DotNetObjectReference.Create(this);
            await this.JsRuntime.InvokeVoidAsync("shedduellerJobViews.registerDrawerEscape", this._selfReference);
            this._escapeRegistered = true;
        }
        else if (!this.IsOpen && this._escapeRegistered)
        {
            await this.JsRuntime.InvokeVoidAsync("shedduellerJobViews.unregisterDrawerEscape");
            this._escapeRegistered = false;
        }
    }

    [SuppressMessage("Usage", "CA1816:Dispose methods should call SuppressFinalize", Justification = "Preserve existing public component disposal behavior.")]
    public async ValueTask DisposeAsync()
    {
        if (this._escapeRegistered)
        {
            try
            {
                await this.JsRuntime.InvokeVoidAsync("shedduellerJobViews.unregisterDrawerEscape");
            }
            catch (JSDisconnectedException)
            {
            }
        }

        this._selfReference?.Dispose();
    }

    [JSInvokable]
    public Task CloseFromEscapeAsync()
      => this.CloseAsync();

    private Task CloseAsync()
      => this.CloseRequested.InvokeAsync();

    private void BeginDrag(int index)
    {
        this._dragSourceIndex = index;
        this._dragTargetIndex = index;
    }

    private void SetDragTarget(int index)
    {
        if (this._dragSourceIndex is not null)
        {
            this._dragTargetIndex = index;
        }
    }

    private async Task DropColumnAsync(int targetIndex)
    {
        if (this._dragSourceIndex is { } sourceIndex)
        {
            await this.MoveColumnAsync(sourceIndex, targetIndex);
        }

        this.EndDrag();
    }

    private void EndDrag()
    {
        this._dragSourceIndex = null;
        this._dragTargetIndex = null;
    }

    private async Task HandleReorderKeyAsync(
        int index,
        KeyboardEventArgs eventArgs)
    {
        var targetIndex = eventArgs.Key switch
        {
            "ArrowUp" => index - 1,
            "ArrowDown" => index + 1,
            "Home" => 0,
            "End" => this.Columns.Count - 1,
            _ => index,
        };

        if (targetIndex != index && targetIndex >= 0 && targetIndex < this.Columns.Count)
        {
            await this.MoveColumnAsync(index, targetIndex);
        }
    }

    private async Task MoveColumnAsync(
        int fromIndex,
        int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= this.Columns.Count
            || toIndex < 0 || toIndex >= this.Columns.Count
            || fromIndex == toIndex)
        {
            return;
        }

        var heading = DashboardJobViews.Heading(this.Columns[fromIndex]);
        await this.ColumnMoved.InvokeAsync((fromIndex, toIndex));
        this._announcement = string.Create(
          CultureInfo.InvariantCulture,
          $"{heading} moved to position {toIndex + 1} of {this.Columns.Count}.");
    }

    private Task SetColumnVisibleAsync(
        ShedduellerDashboardJobColumnKind kind,
        bool visible)
      => this.VisibilityChanged.InvokeAsync((kind, visible));

    private void BeginTagEdit(int index)
    {
        var column = this.Columns[index];
        this._editingIndex = index;
        this._editTagName = column.TagName ?? string.Empty;
        this._editHeading = column.Heading ?? string.Empty;
        this._editError = null;
    }

    private async Task ApplyTagEditAsync()
    {
        if (this._editingIndex is not { } index)
        {
            return;
        }

        var preview = this.Columns.ToList();
        if (!DashboardJobColumnEditor.TryUpdateTag(
            preview,
            index,
            this._editTagName,
            this._editHeading,
            out this._editError))
        {
            return;
        }

        await this.TagColumnEdited.InvokeAsync((index, preview[index]));
        this.CancelTagEdit();
    }

    private void CancelTagEdit()
    {
        this._editingIndex = null;
        this._editTagName = string.Empty;
        this._editHeading = string.Empty;
        this._editError = null;
    }

    private Task RemoveTagColumnAsync(int index)
      => this.TagColumnRemoved.InvokeAsync(index);

    private void ToggleAddTag()
    {
        this._isAddingTag = !this._isAddingTag;
        this._addError = null;
    }

    private async Task AddTagColumnAsync()
    {
        var preview = this.Columns.ToList();
        if (!DashboardJobColumnEditor.TryAddTag(
            preview,
            this._newTagName,
            this._newTagHeading,
            out this._addError))
        {
            return;
        }

        await this.TagColumnAdded.InvokeAsync(preview[^1]);
        this.CancelAddTag();
    }

    private void CancelAddTag()
    {
        this._isAddingTag = false;
        this._newTagName = string.Empty;
        this._newTagHeading = string.Empty;
        this._addError = null;
    }

    private string ColumnRowClass(int index)
      => string.Concat(
        "jobs-column-drawer__row",
        this._dragSourceIndex == index ? " jobs-column-drawer__row--dragging" : string.Empty,
        this._dragTargetIndex == index && this._dragSourceIndex != index
          ? " jobs-column-drawer__row--drop-target"
          : string.Empty);

    private static string ColumnKey(ShedduellerDashboardJobColumn column)
      => column.Kind == ShedduellerDashboardJobColumnKind.Tag
        ? string.Concat("tag:", column.TagName)
        : column.Kind.ToString();

    private static string ColumnWidthText(ShedduellerDashboardJobColumn column)
      => string.Create(
        CultureInfo.InvariantCulture,
        $"{DashboardJobColumnWidths.Get(column)} px · {(column.Width is null ? "default" : "custom")}");
}
