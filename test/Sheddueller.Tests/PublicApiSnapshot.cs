namespace Sheddueller.Tests;

using System.Globalization;
using System.Reflection;

internal static class PublicApiSnapshot
{
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static string Create(Assembly assembly)
    {
        var nullability = new NullabilityInfoContext();
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(TypeName, StringComparer.Ordinal))
        {
            lines.Add($"type {TypeName(type)} [{type.Attributes}] base={TypeName(type.BaseType)} interfaces={string.Join(",", type.GetInterfaces().Select(TypeName).Order(StringComparer.Ordinal))}{GenericConstraints(type.GetGenericArguments())}{Attributes(type.GetCustomAttributesData())}");
            foreach (var member in type.GetMembers(DeclaredMembers))
            {
                var signature = member switch
                {
                    ConstructorInfo constructor when IsVisible(constructor) => $"constructor {MethodModifiers(constructor)} {Parameters(constructor.GetParameters(), nullability)}",
                    MethodInfo method when IsVisible(method) && (!method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal)) => $"method {method.Name} {MethodModifiers(method)} {TypeName(method.ReturnType)} {Nullability(nullability.Create(method.ReturnParameter))} mods={Modifiers(method.ReturnParameter)}{Attributes(method.ReturnParameter.GetCustomAttributesData())} {Parameters(method.GetParameters(), nullability)}{GenericConstraints(method.GetGenericArguments())}",
                    PropertyInfo property when property.GetAccessors(true).Any(IsVisible) => $"property {property.Name} {TypeName(property.PropertyType)} {Nullability(nullability.Create(property))} mods={string.Join(",", property.GetRequiredCustomModifiers().Select(TypeName))} {Parameters(property.GetIndexParameters(), nullability)} get={Accessor(property.GetMethod)} set={Accessor(property.SetMethod)}",
                    FieldInfo field when IsVisible(field) => $"field {field.Name} [{field.Attributes}] {TypeName(field.FieldType)} {Nullability(nullability.Create(field))} mods={string.Join(",", field.GetRequiredCustomModifiers().Select(TypeName))}{(field.IsLiteral ? " value=" + Value(field.GetRawConstantValue()) : string.Empty)}",
                    EventInfo eventInfo when eventInfo.AddMethod is { } add && IsVisible(add) => $"event {eventInfo.Name} {TypeName(eventInfo.EventHandlerType)} add={Accessor(eventInfo.AddMethod)} remove={Accessor(eventInfo.RemoveMethod)}",
                    _ => null,
                };
                if (signature is not null)
                {
                    lines.Add($"  {TypeName(type)} :: {signature}{Attributes(member.GetCustomAttributesData())}");
                }
            }
        }

        return string.Join('\n', lines.Order(StringComparer.Ordinal)) + "\n";
    }

    private static bool IsVisible(MethodBase method)
      => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsVisible(FieldInfo field)
      => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static string TypeName(Type? type)
      => type is null ? "none" : type.ToString();

    private static string MethodModifiers(MethodBase method)
      => $"[{method.Attributes}]";

    private static string Accessor(MethodInfo? method)
      => method is not null && IsVisible(method) ? MethodModifiers(method) + " mods=" + Modifiers(method.ReturnParameter) : "none";

    private static string Modifiers(ParameterInfo parameter)
      => string.Join(",", parameter.GetRequiredCustomModifiers().Select(TypeName));

    private static string Parameters(ParameterInfo[] parameters, NullabilityInfoContext nullability)
      => "(" + string.Join(", ", parameters.Select(parameter =>
          $"{parameter.Name}: {TypeName(parameter.ParameterType)} {Nullability(nullability.Create(parameter))} [{parameter.Attributes}] mods={Modifiers(parameter)}{(parameter.HasDefaultValue ? " default=" + Value(parameter.RawDefaultValue) : string.Empty)}{Attributes(parameter.GetCustomAttributesData())}")) + ")";

    private static string GenericConstraints(Type[] arguments)
      => string.Concat(arguments.Where(argument => argument.IsGenericParameter).Select(argument =>
          $" generic {argument.Name} [{argument.GenericParameterAttributes}] constraints={string.Join(",", argument.GetGenericParameterConstraints().Select(TypeName).Order(StringComparer.Ordinal))}{Attributes(argument.GetCustomAttributesData())}"));

    private static string Nullability(NullabilityInfo info)
      => $"{info.ReadState}/{info.WriteState}" + (info.ElementType is null ? string.Empty : "[" + Nullability(info.ElementType) + "]")
        + (info.GenericTypeArguments.Length == 0 ? string.Empty : "<" + string.Join(",", info.GenericTypeArguments.Select(Nullability)) + ">");

    private static string Attributes(IList<CustomAttributeData> attributes)
      => string.Concat(attributes
          .Where(attribute => (attribute.AttributeType.Namespace != "System.Runtime.CompilerServices"
              || attribute.AttributeType.Name is "ExtensionAttribute" or "RequiredMemberAttribute" or "IsReadOnlyAttribute" or "IsByRefLikeAttribute" or "IsUnmanagedAttribute" or "TupleElementNamesAttribute" or "DynamicAttribute")
            && attribute.AttributeType.FullName != "System.CodeDom.Compiler.GeneratedCodeAttribute"
            && attribute.AttributeType.FullName != "System.Diagnostics.DebuggerHiddenAttribute")
          .Select(attribute => $" attribute {TypeName(attribute.AttributeType)}({string.Join(",", attribute.ConstructorArguments.Select(AttributeValue))})"
            + "{" + string.Join(",", attribute.NamedArguments.Select(argument => argument.MemberName + "=" + AttributeValue(argument.TypedValue)).Order(StringComparer.Ordinal)) + "}")
          .Order(StringComparer.Ordinal));

    private static string AttributeValue(CustomAttributeTypedArgument argument)
      => argument.Value is IList<CustomAttributeTypedArgument> values
        ? "[" + string.Join(",", values.Select(AttributeValue)) + "]"
        : Value(argument.Value);

    private static string Value(object? value)
      => value switch
      {
          null => "null",
          string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\"",
          Type type => TypeName(type),
          IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
          _ => value.ToString() ?? string.Empty,
      };
}
