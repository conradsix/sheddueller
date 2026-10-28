namespace Sheddueller.Dashboard.Components.Ui;

/// <summary>
/// Visual style of a dashboard button.
/// </summary>
public enum ButtonVariant
{
    /// <summary>
    /// Filled with the accent colour; the main action on a surface.
    /// </summary>
    Primary,

    /// <summary>
    /// Filled with a neutral tint.
    /// </summary>
    Secondary,

    /// <summary>
    /// Bordered, for secondary actions.
    /// </summary>
    Outline,

    /// <summary>
    /// No border or fill until hovered.
    /// </summary>
    Ghost,

    /// <summary>
    /// Filled destructive action, used to confirm.
    /// </summary>
    Destructive,

    /// <summary>
    /// Bordered with destructive text, used to start a destructive flow.
    /// </summary>
    DestructiveOutline,

    /// <summary>
    /// Text link styling.
    /// </summary>
    Link,
}

/// <summary>
/// Height and padding of a dashboard button.
/// </summary>
public enum ButtonSize
{
    /// <summary>
    /// 32px controls.
    /// </summary>
    Default,

    /// <summary>
    /// 28px controls for toolbars and tables.
    /// </summary>
    Small,

    /// <summary>
    /// 24px icon buttons inline with text.
    /// </summary>
    ExtraSmall,

    /// <summary>
    /// 36px controls.
    /// </summary>
    Large,
}

/// <summary>
/// Visual style of a badge.
/// </summary>
public enum BadgeVariant
{
    /// <summary>
    /// Bordered, no fill.
    /// </summary>
    Outline,

    /// <summary>
    /// Filled with the accent colour.
    /// </summary>
    Primary,

    /// <summary>
    /// Filled with a neutral tint.
    /// </summary>
    Secondary,

    /// <summary>
    /// Filled destructive colour.
    /// </summary>
    Destructive,
}

/// <summary>
/// Semantic tone of an alert or callout.
/// </summary>
public enum AlertTone
{
    /// <summary>
    /// Neutral information.
    /// </summary>
    Neutral,

    /// <summary>
    /// Informational, in the info colour.
    /// </summary>
    Info,

    /// <summary>
    /// Something succeeded.
    /// </summary>
    Success,

    /// <summary>
    /// Needs attention but is not an error.
    /// </summary>
    Warning,

    /// <summary>
    /// Something failed.
    /// </summary>
    Destructive,
}

/// <summary>
/// Visual style of a tab list.
/// </summary>
public enum TabsVariant
{
    /// <summary>
    /// Pill-shaped segmented control, used for filters.
    /// </summary>
    Segmented,

    /// <summary>
    /// Underlined tabs, used for content sections.
    /// </summary>
    Line,
}
