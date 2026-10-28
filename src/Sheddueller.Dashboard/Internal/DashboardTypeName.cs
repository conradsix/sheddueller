namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

/// <summary>
/// Reads the stored (assembly-qualified) type names of job handlers and parameters for display.
/// </summary>
internal static class DashboardTypeName
{
    /// <summary>
    /// A stored (assembly-qualified) type name without its namespace and assembly, with generic arguments written
    /// the C# way: <c>IProgress&lt;Decimal&gt;</c>, <c>Dictionary&lt;String, Int32&gt;</c>, <c>String[]</c>.
    /// </summary>
    public static string Short(string typeName)
    {
        var index = 0;
        return ReadShortTypeName(typeName, ref index);
    }

    // Reads one type name from index, leaving index after its generic arguments and array suffixes (at the
    // assembly part, if any). Generic arguments are either assembly-qualified ("[[A, Asm],[B, Asm]]") or not ("[A,B]").
    private static string ReadShortTypeName(string text, ref int index)
    {
        var start = index;
        while (index < text.Length && text[index] is not (',' or '[' or ']'))
        {
            index++;
        }

        var name = text[start..index].Trim();
        var arityIndex = name.IndexOf('`', StringComparison.Ordinal);
        if (arityIndex >= 0)
        {
            name = name[..arityIndex];
        }

        var separatorIndex = name.LastIndexOf('.');
        if (separatorIndex >= 0 && separatorIndex < name.Length - 1)
        {
            name = name[(separatorIndex + 1)..];
        }

        var arguments = new List<string>();
        if (arityIndex >= 0 && index < text.Length && text[index] == '[' && !IsArraySuffix(text, index))
        {
            index++;
            while (index < text.Length && text[index] != ']')
            {
                if (text[index] == '[')
                {
                    index++;
                    arguments.Add(ReadShortTypeName(text, ref index));
                    var close = text.IndexOf(']', index);
                    index = close < 0 ? text.Length : close + 1;
                }
                else
                {
                    arguments.Add(ReadShortTypeName(text, ref index));
                }

                if (index < text.Length && text[index] == ',')
                {
                    index++;
                }
            }

            index++;
        }

        var arraySuffix = string.Empty;
        while (IsArraySuffix(text, index))
        {
            var close = text.IndexOf(']', index);
            arraySuffix += text[index..(close + 1)];
            index = close + 1;
        }

        return arguments.Count == 0
          ? name + arraySuffix
          : string.Create(CultureInfo.InvariantCulture, $"{name}<{string.Join(", ", arguments)}>{arraySuffix}");
    }

    private static bool IsArraySuffix(string text, int index)
      => index + 1 < text.Length
        && text[index] == '['
        && text[index + 1] is ']' or ',' or '*'
        && text.IndexOf(']', index) > 0;
}
