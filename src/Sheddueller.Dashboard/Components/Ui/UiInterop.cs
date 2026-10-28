namespace Sheddueller.Dashboard.Components.Ui;

using Microsoft.JSInterop;

/// <summary>
/// Calls into the dashboard scripts (<c>wwwroot/js/</c>), whose members hang off the <c>sheddueller</c> namespace.
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

    /// <summary>
    /// Makes a best-effort call: one the user doesn't need to hear about when it fails, such as scrolling, drawing
    /// a chart, wiring up a dialog, or cleaning up on dispose. A disconnected circuit and a script error are both
    /// ignored, so neither escapes a render or dispose and ends the circuit. Calls whose failure the user should
    /// see (downloads, browser storage) call <see cref="JSRuntimeExtensions.InvokeVoidAsync(IJSRuntime, string, object?[])" />
    /// and handle <see cref="JSException" /> themselves.
    /// </summary>
    /// <returns>Whether the call completed.</returns>
    public static async ValueTask<bool> TryInvokeVoidAsync(this IJSRuntime js, string identifier, params object?[]? args)
    {
        try
        {
            await js.InvokeVoidAsync(identifier, args);
            return true;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
        catch (JSException)
        {
            return false;
        }
    }
}
