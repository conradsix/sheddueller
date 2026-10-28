namespace Sheddueller.Dashboard.Components.Ui;

using Microsoft.JSInterop;

/// <summary>
/// Calls into <c>wwwroot/js/sheddueller-ui.js</c>.
/// </summary>
internal static class UiInterop
{
    public static ValueTask ToastAsync(
        this IJSRuntime js,
        string title,
        string? description = null,
        AlertTone tone = AlertTone.Neutral)
      => js.InvokeVoidAsync(
        "sheddueller.toast",
        new
        {
            title,
            description,
            tone = tone switch
            {
                AlertTone.Success => "success",
                AlertTone.Warning => "warning",
                AlertTone.Destructive => "error",
                _ => "info",
            },
        });

    public static ValueTask DownloadAsync(this IJSRuntime js, string fileName, string content, string contentType = "text/plain;charset=utf-8")
      => js.InvokeVoidAsync("sheddueller.download", fileName, content, contentType);
}
