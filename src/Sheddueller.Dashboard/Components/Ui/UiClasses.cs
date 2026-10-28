namespace Sheddueller.Dashboard.Components.Ui;

internal static class UiClasses
{
    public static string Join(params string?[] classes)
      => string.Join(' ', classes.Where(value => !string.IsNullOrWhiteSpace(value)));

    public static string Button(ButtonVariant variant, ButtonSize size, bool iconOnly, string? extra = null)
      => Join(
        "sd-btn",
        variant switch
        {
            ButtonVariant.Primary => "sd-btn--primary",
            ButtonVariant.Secondary => "sd-btn--secondary",
            ButtonVariant.Outline => "sd-btn--outline",
            ButtonVariant.Ghost => "sd-btn--ghost",
            ButtonVariant.Destructive => "sd-btn--destructive",
            ButtonVariant.DestructiveOutline => "sd-btn--destructive-outline",
            ButtonVariant.Link => "sd-btn--link",
            _ => null,
        },
        size switch
        {
            ButtonSize.Small => "sd-btn--sm",
            ButtonSize.ExtraSmall => "sd-btn--xs",
            ButtonSize.Large => "sd-btn--lg",
            _ => null,
        },
        iconOnly ? "sd-btn--icon" : null,
        extra);
}
