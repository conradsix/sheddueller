namespace Sheddueller.Dashboard.Components.Ui;

/// <summary>
/// What a piece of highlighted code is, which decides its colour.
/// </summary>
public enum CodeTokenKind
{
    /// <summary>
    /// Unstyled text, such as whitespace.
    /// </summary>
    Plain,

    /// <summary>
    /// A type name.
    /// </summary>
    Type,

    /// <summary>
    /// A method name.
    /// </summary>
    Method,

    /// <summary>
    /// A named argument's parameter name.
    /// </summary>
    Parameter,

    /// <summary>
    /// Brackets, commas, colons.
    /// </summary>
    Punctuation,

    /// <summary>
    /// A string literal.
    /// </summary>
    StringLiteral,

    /// <summary>
    /// A number literal.
    /// </summary>
    NumberLiteral,

    /// <summary>
    /// <c>true</c>, <c>false</c> or <c>null</c>.
    /// </summary>
    Keyword,

    /// <summary>
    /// A JSON property name.
    /// </summary>
    Property,

    /// <summary>
    /// De-emphasised text, such as a scheduler-supplied argument or an ellipsis.
    /// </summary>
    Muted,
}

/// <summary>
/// A run of code text with its highlighting kind, rendered by <see cref="CodeTokens" />.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="Kind">How it is highlighted.</param>
public readonly record struct CodeToken(string Text, CodeTokenKind Kind);
