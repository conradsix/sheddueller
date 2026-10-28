namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Dashboard.Internal;

/// <content>
/// The jobs table's columns: which are shown and in what order (the column editor), their widths (dragged, typed
/// with the keyboard, or fitted to the loaded values), and the browser side of resizing.
/// </content>
[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Razor names the page class after the page; the generated part has the same name.")]
public partial class Jobs
{
    private const int SelectColumnWidth = 40;
    private const int ActionsColumnWidth = 48;

    private readonly List<ShedduellerDashboardJobColumn> _columns = [];
    private DotNetObjectReference<Jobs>? _columnResizeReceiver;
    private bool _columnResizingRegistered;
    private bool _isColumnEditorOpen;

    private IReadOnlyList<ShedduellerDashboardJobColumnKind> HiddenBuiltInColumns
      =>
      [
          .. Enum.GetValues<ShedduellerDashboardJobColumnKind>()
            .Where(kind => kind != ShedduellerDashboardJobColumnKind.Tag)
            .Where(kind => this._columns.All(column => column.Kind != kind)),
      ];

    private IReadOnlyList<string> ObservedTagNames
      =>
      [
          .. this._jobs.Rows.SelectMany(job => job.Tags)
            .Select(tag => tag.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
      ];

    private string TableStyle
    {
        get
        {
            // At least the configured column widths; the trailing filler column takes any extra space.
            var width = DashboardJobColumnWidths.Total(this._columns) + SelectColumnWidth + ActionsColumnWidth;
            return string.Create(CultureInfo.InvariantCulture, $"width: max(100%, {width}px); min-width: {width}px");
        }
    }

    [JSInvokable]
    public Task SetColumnWidthFromResizeAsync(
        int columnIndex,
        int width)
      => this.InvokeAsync(() =>
      {
          this.SetColumnWidth(columnIndex, width);
          this.StateHasChanged();
      });

    private static string ColumnKey(ShedduellerDashboardJobColumn column)
      => column.Kind == ShedduellerDashboardJobColumnKind.Tag
        ? string.Concat("tag:", column.TagName)
        : column.Kind.ToString();

    private static int ColumnWidth(ShedduellerDashboardJobColumn column)
      => DashboardJobColumnWidths.Get(column);

    private static string ColumnWidthStyle(ShedduellerDashboardJobColumn column)
      => string.Create(CultureInfo.InvariantCulture, $"width: {ColumnWidth(column)}px");

    private static string? ColumnHeaderClass(ShedduellerDashboardJobColumn column)
      => column.Kind is ShedduellerDashboardJobColumnKind.Priority or ShedduellerDashboardJobColumnKind.Attempts
        ? "jobs-table__number"
        : null;

    // The browser reports drag-resizes through SetColumnWidthFromResizeAsync once this page is registered.
    private async Task RegisterColumnResizingAsync()
    {
        if (this._columnResizingRegistered)
        {
            return;
        }

        // Best-effort: without it, drag-resizing does nothing, and the next render tries again.
        this._columnResizeReceiver ??= DotNetObjectReference.Create(this);
        this._columnResizingRegistered = await this.JsRuntime.TryInvokeVoidAsync(
          "sheddueller.jobViews.registerTableColumnResizing",
          this._columnResizeReceiver);
    }

    private async Task UnregisterColumnResizingAsync()
    {
        if (this._columnResizingRegistered)
        {
            await this.JsRuntime.TryInvokeVoidAsync("sheddueller.jobViews.unregisterTableColumnResizing");
        }

        this._columnResizeReceiver?.Dispose();
    }

    private void OpenColumnEditor()
      => this._isColumnEditorOpen = true;

    private void CloseColumnEditor()
      => this._isColumnEditorOpen = false;

    private void SetBuiltInColumnVisible((ShedduellerDashboardJobColumnKind Kind, bool Visible) change)
    {
        if (DashboardJobColumnEditor.SetBuiltInVisibility(this._columns, change.Kind, change.Visible))
        {
            this.UpdateViewDirty();
        }
    }

    private void MoveColumn((int FromIndex, int ToIndex) move)
    {
        if (DashboardJobColumnEditor.Move(this._columns, move.FromIndex, move.ToIndex))
        {
            this.UpdateViewDirty();
        }
    }

    private void RemoveColumn(int index)
    {
        if (!DashboardJobColumnEditor.RemoveTag(this._columns, index))
        {
            return;
        }

        this.UpdateViewDirty();
    }

    private void AddTagColumn(ShedduellerDashboardJobColumn column)
    {
        if (!DashboardJobColumnEditor.TryAddTag(
            this._columns,
            column.TagName ?? string.Empty,
            column.Heading,
            out var error))
        {
            this.ShowToast("Couldn't add the tag column", error, AlertTone.Destructive);
            return;
        }

        this.UpdateViewDirty();
    }

    private void EditTagColumn((int Index, ShedduellerDashboardJobColumn Column) change)
    {
        if (!DashboardJobColumnEditor.TryUpdateTag(
            this._columns,
            change.Index,
            change.Column.TagName ?? string.Empty,
            change.Column.Heading,
            out var error))
        {
            this.ShowToast("Couldn't update the tag column", error, AlertTone.Destructive);
            return;
        }

        this.UpdateViewDirty();
    }

    private Task HandleColumnWidthKeyAsync(
        int columnIndex,
        KeyboardEventArgs eventArgs)
    {
        if (columnIndex < 0 || columnIndex >= this._columns.Count)
        {
            return Task.CompletedTask;
        }

        var currentWidth = ColumnWidth(this._columns[columnIndex]);
        var step = eventArgs.ShiftKey ? 32 : 8;
        var nextWidth = eventArgs.Key switch
        {
            "ArrowLeft" => currentWidth - step,
            "ArrowRight" => currentWidth + step,
            "Home" => DashboardJobColumnWidths.MinimumOverride,
            "End" => DashboardJobColumnWidths.MaximumOverride,
            _ => currentWidth,
        };

        if (nextWidth != currentWidth)
        {
            this.SetColumnWidth(
              columnIndex,
              Math.Clamp(
                nextWidth,
                DashboardJobColumnWidths.MinimumOverride,
                DashboardJobColumnWidths.MaximumOverride));
        }

        return Task.CompletedTask;
    }

    private async Task AutoFitColumnAsync(int columnIndex)
    {
        var widths = await this.MeasureAutoFitWidthsAsync();
        if (columnIndex >= 0 && columnIndex < widths.Count)
        {
            this.SetColumnWidth(columnIndex, widths[columnIndex]);
        }
    }

    private async Task AutoFitAllColumnsAsync()
    {
        var widths = await this.MeasureAutoFitWidthsAsync();
        var changed = false;
        for (var index = 0; index < Math.Min(this._columns.Count, widths.Count); index++)
        {
            changed |= DashboardJobColumnWidths.Set(this._columns, index, widths[index]);
        }

        if (changed)
        {
            this.UpdateViewDirty();
        }
    }

    // Measuring runs in the browser; a failure there must not escape the event handler and end the circuit.
    private async Task<IReadOnlyList<int>> MeasureAutoFitWidthsAsync()
    {
        var minimums = this._columns
          .Select(column => DashboardJobColumnWidths.AutoFitMinimum(column.Kind))
          .ToArray();
        var maximums = this._columns
          .Select(column => DashboardJobColumnWidths.AutoFitMaximum(column.Kind))
          .ToArray();

        try
        {
            var widths = await this.JsRuntime.InvokeAsync<int[]?>(
              "sheddueller.jobViews.measureJobColumnWidths",
              minimums,
              maximums);
            return widths is { Length: > 0 } && widths.Length == this._columns.Count && widths.All(width => DashboardJobColumnWidths.IsValid(width))
              ? widths
              : this.ColumnsNotMeasured();
        }
        catch (Exception exception) when (exception is JSException or System.Text.Json.JsonException)
        {
            return this.ColumnsNotMeasured();
        }
    }

    private IReadOnlyList<int> ColumnsNotMeasured()
    {
        this.ShowToast("Couldn't fit the columns", "Column widths are unchanged.", AlertTone.Destructive);
        return [];
    }

    private void ResetColumnWidth(int columnIndex)
    {
        if (DashboardJobColumnWidths.Set(this._columns, columnIndex, width: null))
        {
            this.UpdateViewDirty();
        }
    }

    private void ResetColumnWidths()
    {
        if (DashboardJobColumnWidths.Reset(this._columns))
        {
            this.UpdateViewDirty();
        }
    }

    private void SetColumnWidth(
        int columnIndex,
        int width)
    {
        if (DashboardJobColumnWidths.Set(this._columns, columnIndex, width))
        {
            this.UpdateViewDirty();
        }
    }

    private void ResetColumns()
    {
        this.ReplaceColumns(DashboardJobViews.BuiltInColumns);
        this.UpdateViewDirty();
    }

    private void ReplaceColumns(IReadOnlyList<ShedduellerDashboardJobColumn> columns)
    {
        this._columns.Clear();
        this._columns.AddRange(columns);
    }
}
