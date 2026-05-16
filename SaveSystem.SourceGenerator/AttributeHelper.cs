using Microsoft.CodeAnalysis;
using System;
using System.Collections.Immutable;
using System.Linq;
using SaveDataGenerator;

public static class AttributeHelper
{
    public static void PrintAttributes(INamedTypeSymbol symbol)
    {
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            string attrName = attr.AttributeClass?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "Unknown";
            Logger.Log($"🔹 Attribute: {attrName}");

            // 1. Positional constructor arguments
            for (int i = 0; i < attr.ConstructorArguments.Length; i++)
            {
                var arg = attr.ConstructorArguments[i];
                Logger.Log($"   Constructor arg[{i}]: {FormatTypedConstant(arg)}");
            }

            // 2. Named arguments (properties or fields)
            foreach (var namedArg in attr.NamedArguments)
            {
                Logger.Log($"   Named arg '{namedArg.Key}': {FormatTypedConstant(namedArg.Value)}");
            }
        }
    }

    public static string? GetSaveDataSelector(INamedTypeSymbol symbol)
    {
        var attr = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name is "SaveDataAttribute" or "SaveData");

        if (attr == null) return null;

        // NamedArguments is ImmutableArray<KeyValuePair<string, TypedConstant>>
        var filterArg = attr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Select");

        // Check if the argument was actually provided
        if (filterArg.Value.IsNull) return null;

        return filterArg.Value.Value as string;
    }
    
    public static string? GetSaveDataFilter(IPropertySymbol propertySymbol)
    {
        var attr = propertySymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name is "SaveDataAttribute" or "SaveData");

        if (attr == null) return null;

        // NamedArguments is ImmutableArray<KeyValuePair<string, TypedConstant>>
        var filterArg = attr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Filter");
    
        // Check if the argument was actually provided
        if (filterArg.Value.IsNull) return null;
    
        return filterArg.Value.Value as string;
    }

    public static string? GetSaveDataSelector(IPropertySymbol propertySymbol)
    {
        var attr = propertySymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name is "SaveDataAttribute" or "SaveData");

        if (attr == null) return null;

        // NamedArguments is ImmutableArray<KeyValuePair<string, TypedConstant>>
        var filterArg = attr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Select");

        // Check if the argument was actually provided
        if (filterArg.Value.IsNull) return null;

        return filterArg.Value.Value as string;
    }

    private static string FormatTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull) return "null";

        return constant.Kind switch
        {
            TypedConstantKind.Primitive or TypedConstantKind.Enum or TypedConstantKind.Type => constant.Value?.ToString() ?? "null",
            TypedConstantKind.Array => $"[{string.Join(", ", constant.Values.Select(FormatTypedConstant))}]",
            TypedConstantKind.Error => "❌ Invalid constant",
            _ => $"Unknown ({constant.Kind})"
        };
    }
}
