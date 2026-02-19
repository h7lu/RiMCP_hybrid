using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace RimWorldCodeRag.Indexer;

/// <summary>
/// Helper class to format Roslyn ISymbol objects into consistent string IDs
/// that match the project's existing format (Namespace.Type.Member).
/// </summary>
public static class SymbolFormatter
{
    /// <summary>
    /// Gets a unique identifier for a symbol that matches the project's ID format.
    /// This handles methods with overloads by including parameter types in the ID.
    /// </summary>
    /// <param name="symbol">The Roslyn symbol to format.</param>
    /// <returns>A string ID, or null if the symbol cannot be identified.</returns>
    public static string? GetUniqueId(ISymbol? symbol)
    {
        if (symbol == null)
        {
            return null;
        }

        // Skip symbols that don't have source locations (external/BCL symbols)
        // unless they're from the RimWorld/Verse namespaces
        var containingNamespace = GetContainingNamespace(symbol);
        if (!IsRimWorldSymbol(containingNamespace) && symbol.Locations.All(loc => !loc.IsInSource))
        {
            return null;
        }

        return symbol switch
        {
            INamedTypeSymbol typeSymbol => FormatTypeSymbol(typeSymbol),
            IMethodSymbol methodSymbol => FormatMethodSymbol(methodSymbol),
            IPropertySymbol propertySymbol => FormatPropertySymbol(propertySymbol),
            IFieldSymbol fieldSymbol => FormatFieldSymbol(fieldSymbol),
            IEventSymbol eventSymbol => FormatEventSymbol(eventSymbol),
            ILocalSymbol => null, // Local variables are not tracked
            IParameterSymbol => null, // Parameters are not tracked
            _ => FormatGenericSymbol(symbol)
        };
    }

    /// <summary>
    /// Determines if a symbol is from the RimWorld/Verse codebase.
    /// </summary>
    public static bool IsRimWorldSymbol(string? namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
        {
            return false;
        }

        return namespaceName.StartsWith("RimWorld", StringComparison.Ordinal) ||
               namespaceName.StartsWith("Verse", StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets the fully qualified namespace for a symbol.
    /// </summary>
    public static string GetContainingNamespace(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace;
        if (ns == null || ns.IsGlobalNamespace)
        {
            return string.Empty;
        }

        return ns.ToDisplayString();
    }

    private static string? FormatTypeSymbol(INamedTypeSymbol typeSymbol)
    {
        var ns = GetContainingNamespace(typeSymbol);
        var typeName = typeSymbol.Name;

        // Handle nested types
        if (typeSymbol.ContainingType != null)
        {
            var containingTypeName = typeSymbol.ContainingType.Name;
            typeName = $"{containingTypeName}.{typeName}";
        }

        // Handle generic types
        if (typeSymbol.TypeParameters.Length > 0)
        {
            typeName = $"{typeName}`{typeSymbol.TypeParameters.Length}";
        }

        return string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
    }

    private static string? FormatMethodSymbol(IMethodSymbol methodSymbol)
    {
        var containingType = methodSymbol.ContainingType;
        if (containingType == null)
        {
            return null;
        }

        var typeId = FormatTypeSymbol(containingType);
        if (typeId == null)
        {
            return null;
        }

        // Build method signature with modifiers, return type, and parameters
        var sb = new StringBuilder();
        
        // Add modifiers
        var modifiers = GetModifiers(methodSymbol);
        if (!string.IsNullOrEmpty(modifiers))
        {
            sb.Append(modifiers);
            sb.Append(' ');
        }

        // Add return type
        sb.Append(FormatTypeName(methodSymbol.ReturnType));
        sb.Append(' ');

        // Add method name
        sb.Append(methodSymbol.Name);

        // Add parameters
        sb.Append('(');
        var parameters = methodSymbol.Parameters
            .Select(p => $"{FormatTypeName(p.Type)} {p.Name}");
        sb.Append(string.Join(", ", parameters));
        sb.Append(')');

        return $"{typeId}.{sb}";
    }

    private static string? FormatPropertySymbol(IPropertySymbol propertySymbol)
    {
        var containingType = propertySymbol.ContainingType;
        if (containingType == null)
        {
            return null;
        }

        var typeId = FormatTypeSymbol(containingType);
        if (typeId == null)
        {
            return null;
        }

        return $"{typeId}.{propertySymbol.Name}";
    }

    private static string? FormatFieldSymbol(IFieldSymbol fieldSymbol)
    {
        var containingType = fieldSymbol.ContainingType;
        if (containingType == null)
        {
            return null;
        }

        var typeId = FormatTypeSymbol(containingType);
        if (typeId == null)
        {
            return null;
        }

        return $"{typeId}.{fieldSymbol.Name}";
    }

    private static string? FormatEventSymbol(IEventSymbol eventSymbol)
    {
        var containingType = eventSymbol.ContainingType;
        if (containingType == null)
        {
            return null;
        }

        var typeId = FormatTypeSymbol(containingType);
        if (typeId == null)
        {
            return null;
        }

        return $"{typeId}.{eventSymbol.Name}";
    }

    private static string? FormatGenericSymbol(ISymbol symbol)
    {
        var containingType = symbol.ContainingType;
        if (containingType == null)
        {
            return null;
        }

        var typeId = FormatTypeSymbol(containingType);
        if (typeId == null)
        {
            return null;
        }

        return $"{typeId}.{symbol.Name}";
    }

    /// <summary>
    /// Formats a type name for use in signatures, handling generics and arrays.
    /// </summary>
    private static string FormatTypeName(ITypeSymbol type)
    {
        return type switch
        {
            IArrayTypeSymbol arrayType => $"{FormatTypeName(arrayType.ElementType)}[]",
            INamedTypeSymbol { IsGenericType: true } namedType => FormatGenericTypeName(namedType),
            ITypeParameterSymbol typeParam => typeParam.Name,
            _ => type.Name
        };
    }

    private static string FormatGenericTypeName(INamedTypeSymbol namedType)
    {
        var baseName = namedType.Name;
        var typeArgs = namedType.TypeArguments.Select(FormatTypeName);
        return $"{baseName}<{string.Join(", ", typeArgs)}>";
    }

    /// <summary>
    /// Gets the access modifiers for a symbol.
    /// </summary>
    private static string GetModifiers(ISymbol symbol)
    {
        var parts = new System.Collections.Generic.List<string>();

        // Access modifier
        switch (symbol.DeclaredAccessibility)
        {
            case Accessibility.Public:
                parts.Add("public");
                break;
            case Accessibility.Private:
                parts.Add("private");
                break;
            case Accessibility.Protected:
                parts.Add("protected");
                break;
            case Accessibility.Internal:
                parts.Add("internal");
                break;
            case Accessibility.ProtectedOrInternal:
                parts.Add("protected internal");
                break;
            case Accessibility.ProtectedAndInternal:
                parts.Add("private protected");
                break;
        }

        // Other modifiers
        if (symbol.IsStatic)
        {
            parts.Add("static");
        }
        if (symbol.IsAbstract)
        {
            parts.Add("abstract");
        }
        if (symbol.IsVirtual)
        {
            parts.Add("virtual");
        }
        if (symbol.IsOverride)
        {
            parts.Add("override");
        }
        if (symbol.IsSealed)
        {
            parts.Add("sealed");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Gets the symbol ID in a simpler format (just Namespace.Type or Namespace.Type.Member)
    /// without method signatures. Useful for looking up types.
    /// </summary>
    public static string? GetSimpleId(ISymbol? symbol)
    {
        if (symbol == null)
        {
            return null;
        }

        return symbol switch
        {
            INamedTypeSymbol typeSymbol => FormatTypeSymbol(typeSymbol),
            IMethodSymbol methodSymbol => GetSimpleMethodId(methodSymbol),
            IPropertySymbol propertySymbol => FormatPropertySymbol(propertySymbol),
            IFieldSymbol fieldSymbol => FormatFieldSymbol(fieldSymbol),
            _ => null
        };
    }

    private static string? GetSimpleMethodId(IMethodSymbol methodSymbol)
    {
        var containingType = methodSymbol.ContainingType;
        if (containingType == null)
        {
            return null;
        }

        var typeId = FormatTypeSymbol(containingType);
        if (typeId == null)
        {
            return null;
        }

        return $"{typeId}.{methodSymbol.Name}";
    }
}
