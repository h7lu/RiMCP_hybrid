using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace RimWorldCodeRag.Indexer;

/// <summary>
/// Analyzes Def class fields using Roslyn's Semantic Model to dynamically discover 
/// which XML tag names can link to C# classes.
/// 
/// This implements M8: Dynamic XML-to-C# Link Extraction via Reflection Analysis.
/// Instead of maintaining a hardcoded list of XML tags, we analyze the Def classes
/// themselves to find fields whose types inherit from known "linkable" base types.
/// </summary>
public sealed class DefFieldAnalyzer
{
    private readonly Compilation _compilation;
    private readonly IReadOnlyCollection<string> _linkableBaseTypes;
    private readonly INamedTypeSymbol?[] _linkableSymbols;

    /// <summary>
    /// Predefined set of base types that represent "linkable" C# classes from XML.
    /// When a Def class has a public field of a type that inherits from one of these,
    /// the XML tag name for that field can reference a C# class.
    /// </summary>
    public static readonly HashSet<string> DefaultLinkableBaseTypes = new(StringComparer.Ordinal)
    {
        // Core game object types
        "Verse.Thing",
        "Verse.ThingComp",
        "RimWorld.CompProperties",
        
        // Verb and ability types
        "Verse.Verb",
        "RimWorld.AbilityComp",
        "RimWorld.CompProperties_AbilityEffect",
        
        // Health and hediff types
        "Verse.HediffComp",
        "Verse.HediffCompProperties",
        "Verse.Hediff",
        
        // AI and behavior types
        "Verse.AI.ThinkNode",
        "Verse.AI.JobDriver",
        "RimWorld.WorkGiver",
        "RimWorld.IncidentWorker",
        
        // Building and room types
        "RimWorld.RoomContentsWorker",
        "RimWorld.PlaceWorker",
        "RimWorld.Building",
        
        // Graphics types
        "Verse.Graphic",
        
        // Designation types
        "Verse.Designator",
        
        // Ideology and religion types
        "RimWorld.RitualOutcomeEffect",
        "RimWorld.RitualBehaviorWorker",
        "RimWorld.PreceptComp",
        
        // Quest types
        "RimWorld.QuestPart",
        "RimWorld.QuestNode",
        
        // Royalty types
        "RimWorld.RoyalTitlePermitWorker",
        
        // Anomaly types
        "RimWorld.ThingComp",
        
        // Generic worker types
        "Verse.GenStep",
        "RimWorld.StockGenerator",
        "RimWorld.StatPart",
        
        // Pawn-related workers
        "RimWorld.PawnRelationWorker",
        "RimWorld.InteractionWorker",
        "RimWorld.ThoughtWorker",
        
        // System types that can be instantiated
        "System.Type"  // For fields that store Type objects
    };

    /// <summary>
    /// Creates a new DefFieldAnalyzer.
    /// </summary>
    /// <param name="compilation">The Roslyn compilation containing the RimWorld source code.</param>
    /// <param name="linkableBaseTypes">Set of fully qualified base type names that are considered "linkable". 
    /// If null, uses DefaultLinkableBaseTypes.</param>
    public DefFieldAnalyzer(Compilation compilation, IReadOnlyCollection<string>? linkableBaseTypes = null)
    {
        _compilation = compilation;
        _linkableBaseTypes = linkableBaseTypes ?? DefaultLinkableBaseTypes;
        
        // Pre-resolve the string names into Roslyn symbol objects for efficient checking
        _linkableSymbols = _linkableBaseTypes
            .Select(typeName => _compilation.GetTypeByMetadataName(typeName))
            .Where(s => s != null)
            .ToArray();
        
        Console.WriteLine($"[DefFieldAnalyzer] Resolved {_linkableSymbols.Length} of {_linkableBaseTypes.Count} linkable base types");
    }

    /// <summary>
    /// Finds all public field names in Def classes that can link to C# classes.
    /// These are fields whose types inherit from one of the linkable base types.
    /// </summary>
    /// <returns>A set of field names that can be XML tags linking to C# classes.</returns>
    public HashSet<string> FindAllLinkableFieldNames()
    {
        var linkableFieldNames = new HashSet<string>(StringComparer.Ordinal);
        
        // Find the base Def type
        var defSymbol = _compilation.GetTypeByMetadataName("Verse.Def");
        if (defSymbol == null)
        {
            Console.Error.WriteLine("[DefFieldAnalyzer] Warning: Could not find Verse.Def type. XML field analysis will be limited.");
            // Return a fallback set of known field names
            return GetFallbackLinkableFieldNames();
        }

        Console.WriteLine("[DefFieldAnalyzer] Finding all types that inherit from Verse.Def...");
        
        // Find all types in the compilation that inherit from Verse.Def
        var allDefTypes = FindAllTypesInheritingFrom(defSymbol);
        Console.WriteLine($"[DefFieldAnalyzer] Found {allDefTypes.Count} Def types to analyze");

        foreach (var defType in allDefTypes)
        {
            AnalyzeDefType(defType, linkableFieldNames);
        }

        // Add known field names that might be missed due to missing DLL references
        AddWellKnownFieldNames(linkableFieldNames);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[DefFieldAnalyzer] Found {linkableFieldNames.Count} linkable XML field names");
        Console.ResetColor();
        
        return linkableFieldNames;
    }

    private void AnalyzeDefType(INamedTypeSymbol defType, HashSet<string> linkableFieldNames)
    {
        // Get all public fields (including inherited ones)
        var fields = GetAllPublicFields(defType);

        foreach (var field in fields)
        {
            if (IsLinkableType(field.Type))
            {
                linkableFieldNames.Add(field.Name);
            }
            
            // Also check for Type fields that store class references
            if (IsTypeField(field.Type))
            {
                linkableFieldNames.Add(field.Name);
            }
            
            // Check for List<T> where T is linkable
            if (IsListOfLinkableType(field.Type, out var elementTypeName))
            {
                linkableFieldNames.Add(field.Name);
            }
        }
    }

    private IEnumerable<IFieldSymbol> GetAllPublicFields(INamedTypeSymbol type)
    {
        var current = type;
        while (current != null)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IFieldSymbol field && field.DeclaredAccessibility == Accessibility.Public)
                {
                    yield return field;
                }
            }
            current = current.BaseType;
        }
    }

    private bool IsLinkableType(ITypeSymbol typeSymbol)
    {
        // Direct check against linkable symbols
        foreach (var linkableSymbol in _linkableSymbols)
        {
            if (linkableSymbol != null && InheritsFrom(typeSymbol, linkableSymbol))
            {
                return true;
            }
        }
        
        // Check by name for types we couldn't resolve
        var fullName = GetFullTypeName(typeSymbol);
        if (_linkableBaseTypes.Any(baseName => fullName.EndsWith(baseName, StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private bool IsTypeField(ITypeSymbol typeSymbol)
    {
        var fullName = GetFullTypeName(typeSymbol);
        return fullName == "System.Type" || fullName.EndsWith(".Type", StringComparison.Ordinal);
    }

    private bool IsListOfLinkableType(ITypeSymbol typeSymbol, out string? elementTypeName)
    {
        elementTypeName = null;
        
        if (typeSymbol is INamedTypeSymbol namedType)
        {
            // Check if it's List<T> or IList<T>
            if (namedType.IsGenericType && 
                (namedType.Name == "List" || namedType.Name == "IList") &&
                namedType.TypeArguments.Length == 1)
            {
                var elementType = namedType.TypeArguments[0];
                if (IsLinkableType(elementType) || IsTypeField(elementType))
                {
                    elementTypeName = GetFullTypeName(elementType);
                    return true;
                }
            }
        }
        
        return false;
    }

    private static string GetFullTypeName(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        if (ns == null || ns.IsGlobalNamespace)
        {
            return type.Name;
        }
        return $"{ns.ToDisplayString()}.{type.Name}";
    }

    private static bool InheritsFrom(ITypeSymbol? type, INamedTypeSymbol baseType)
    {
        var current = type;
        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    private List<INamedTypeSymbol> FindAllTypesInheritingFrom(INamedTypeSymbol baseType)
    {
        var result = new List<INamedTypeSymbol>();
        
        // Search through all syntax trees in the compilation
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var semanticModel = _compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            
            foreach (var typeDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                if (typeSymbol != null && InheritsFrom(typeSymbol, baseType))
                {
                    result.Add(typeSymbol);
                }
            }
        }
        
        return result;
    }

    /// <summary>
    /// Returns a fallback set of well-known field names when Roslyn analysis is limited.
    /// </summary>
    private static HashSet<string> GetFallbackLinkableFieldNames()
    {
        return new HashSet<string>(StringComparer.Ordinal)
        {
            // Core class binding fields
            "thingClass",
            "workerClass", 
            "driverClass",
            "hediffClass",
            "class",
            "aiController",
            "roomContentsWorkerType",
            
            // Graphics
            "graphicClass",
            
            // Verbs
            "verbClass",
            
            // Comps
            "compClass",
            
            // Workers
            "placeWorkers",
            "thinkRoot",
            "inspectorTabsResolved",
            
            // AI
            "jobDriver",
            "thinkTreeMainConstant",
            "thinkTreeConstant",
            
            // Other known patterns
            "outcomeEffect",
            "behaviorWorker"
        };
    }

    /// <summary>
    /// Adds well-known field names that might be missed due to incomplete type resolution.
    /// </summary>
    private static void AddWellKnownFieldNames(HashSet<string> fieldNames)
    {
        var wellKnown = new[]
        {
            // Core binding fields
            "thingClass",
            "workerClass",
            "driverClass", 
            "hediffClass",
            "class",
            "aiController",
            "roomContentsWorkerType",
            
            // Graphics
            "graphicClass",
            
            // Verbs and abilities
            "verbClass",
            "abilityClass",
            
            // Comps
            "compClass",
            
            // Workers
            "placeWorkers",
            
            // AI
            "jobDriver",
            
            // Outcome effects
            "outcomeEffect",
            "behaviorWorker",
            
            // Other patterns ending in "Class" or "Worker"
            "thoughtWorker",
            "interactionWorker",
            "relationWorker"
        };

        foreach (var name in wellKnown)
        {
            fieldNames.Add(name);
        }
    }
}
