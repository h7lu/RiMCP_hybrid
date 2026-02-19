using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

// M8: Dynamic XML-to-C# Link Extraction
// Instead of hardcoding XML tag names, we now support dynamic discovery of linkable fields.
// The DefFieldAnalyzer uses Roslyn to find all Def class fields that can link to C# classes.

//从 XML Def 中提取图谱边
public sealed class XmlGraphExtractor
{
    /// <summary>
    /// Dynamic set of field names that can link to C# classes.
    /// If not set, falls back to the hardcoded list for backward compatibility.
    /// </summary>
    public IReadOnlySet<string>? DynamicLinkableFieldNames { get; set; }

    /// <summary>
    /// Default hardcoded field names for backward compatibility when dynamic analysis is not available.
    /// </summary>
    private static readonly HashSet<string> DefaultClassFieldCandidates = new(StringComparer.Ordinal)
    {
        "thingClass",
        "workerClass",
        "driverClass",
        "hediffClass",
        "class",
        "aiController",
        "roomContentsWorkerType",
        "graphicClass",
        "verbClass",
        "abilityClass",
        "compClass",
        "placeWorkers",
        "jobDriver",
        "outcomeEffect",
        "behaviorWorker",
        "thoughtWorker",
        "interactionWorker",
        "relationWorker"
    };

    //提取 XML → C# 边（XmlBindsClass, XmlUsesComp）
    public IEnumerable<GraphEdge> ExtractXmlToCSharpEdges(XElement defElement, string defId, string? defType)
    {
        foreach (var edge in ExtractBindClassEdges(defElement, defId))
        {
            yield return edge;
        }

        foreach (var edge in ExtractUsesCompEdges(defElement, defId))
        {
            yield return edge;
        }
        
        // M8: Extract edges using dynamic field names
        foreach (var edge in ExtractDynamicBindClassEdges(defElement, defId))
        {
            yield return edge;
        }
    }

    /// <summary>
    /// Extracts XML → C# edges using the dynamically discovered linkable field names.
    /// This implements M8: Dynamic XML-to-C# Link Extraction.
    /// </summary>
    public IEnumerable<GraphEdge> ExtractXmlToCSharpEdges(
        XElement defElement, 
        string defId, 
        string? defType,
        IReadOnlySet<string> linkableFieldNames)
    {
        // Use the provided dynamic field names
        foreach (var fieldName in linkableFieldNames)
        {
            // Check direct child elements
            var className = defElement.Element(fieldName)?.Value;
            if (!string.IsNullOrWhiteSpace(className) && LooksLikeClassName(className))
            {
                yield return new GraphEdge
                {
                    SourceId = defId,
                    TargetId = NormalizeClassName(className),
                    Kind = EdgeKind.XmlBindsClass
                };
            }
            
            // Check nested elements (e.g., graphicData/graphicClass)
            foreach (var nestedElement in defElement.Descendants(fieldName))
            {
                var nestedClassName = nestedElement.Value;
                if (!string.IsNullOrWhiteSpace(nestedClassName) && LooksLikeClassName(nestedClassName))
                {
                    yield return new GraphEdge
                    {
                        SourceId = defId,
                        TargetId = NormalizeClassName(nestedClassName),
                        Kind = EdgeKind.XmlBindsClass
                    };
                }
            }
        }
        
        // Also extract comps edges (these have a special structure)
        foreach (var edge in ExtractUsesCompEdges(defElement, defId))
        {
            yield return edge;
        }
    }
    

    //XML -> XML 边（XmlInherits, XmlReferences）
    public IEnumerable<GraphEdge> ExtractXmlToXmlEdges(
        XElement defElement, 
        string defId, 
        string? defType,
        ICollection<string> validDefIds,
        ICollection<string> validDefNames)
    {
        var inheritsEdge = ExtractInheritsEdge(defElement, defId);
        if (inheritsEdge != null && (validDefIds.Contains(inheritsEdge.TargetId) || validDefNames.Contains(inheritsEdge.TargetId.Replace("xml:", ""))))
        {
            yield return inheritsEdge;
        }

        foreach (var edge in ExtractReferencesEdges(defElement, defId, defType))
        {
            if (validDefIds.Contains(edge.TargetId) || validDefNames.Contains(edge.TargetId.Replace("xml:", "")))
            {
                yield return edge;
            }
        }
    }
    
    #region XML → C# Edge Extraction

    //类绑定边（thingClass, workerClass, verbClass, graphicClass等）- legacy hardcoded approach
    private IEnumerable<GraphEdge> ExtractBindClassEdges(XElement defElement, string defId)
    {
        var classFieldCandidates = new[]
        {
            "thingClass",
            "workerClass",
            "driverClass",
            "hediffClass",
            "class",
            "aiController",
            "roomContentsWorkerType"
        };
        
        foreach (var fieldName in classFieldCandidates)
        {
            var className = defElement.Element(fieldName)?.Value;
            if (!string.IsNullOrWhiteSpace(className))
            {
                yield return new GraphEdge
                {
                    SourceId = defId,
                    TargetId = NormalizeClassName(className),
                    Kind = EdgeKind.XmlBindsClass
                };
            }
        }
        
        //嵌套类的title, 如graphicData/graphicClass, verbs/li/verbClass
        var graphicClass = defElement.Descendants("graphicData")
            .Elements("graphicClass")
            .FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(graphicClass))
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = NormalizeClassName(graphicClass),
                Kind = EdgeKind.XmlBindsClass
            };
        }
        
        var verbClasses = defElement.Descendants("verbs")
            .Elements("li")
            .Elements("verbClass")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v));
        
        foreach (var verbClass in verbClasses)
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = NormalizeClassName(verbClass),
                Kind = EdgeKind.XmlBindsClass
            };
        }
    }

    /// <summary>
    /// Extract class binding edges using dynamic or default field names.
    /// </summary>
    private IEnumerable<GraphEdge> ExtractDynamicBindClassEdges(XElement defElement, string defId)
    {
        var fieldNames = DynamicLinkableFieldNames ?? DefaultClassFieldCandidates;
        var emittedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var fieldName in fieldNames)
        {
            // Check direct child elements
            var className = defElement.Element(fieldName)?.Value;
            if (!string.IsNullOrWhiteSpace(className) && LooksLikeClassName(className))
            {
                var targetId = NormalizeClassName(className);
                if (emittedTargets.Add(targetId))
                {
                    yield return new GraphEdge
                    {
                        SourceId = defId,
                        TargetId = targetId,
                        Kind = EdgeKind.XmlBindsClass
                    };
                }
            }
            
            // Check nested elements anywhere in the tree
            foreach (var nestedElement in defElement.Descendants(fieldName))
            {
                var nestedClassName = nestedElement.Value;
                if (!string.IsNullOrWhiteSpace(nestedClassName) && LooksLikeClassName(nestedClassName))
                {
                    var targetId = NormalizeClassName(nestedClassName);
                    if (emittedTargets.Add(targetId))
                    {
                        yield return new GraphEdge
                        {
                            SourceId = defId,
                            TargetId = targetId,
                            Kind = EdgeKind.XmlBindsClass
                        };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks if a string value looks like a C# class name.
    /// </summary>
    private static bool LooksLikeClassName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        
        // Skip if it looks like a number or boolean
        if (int.TryParse(value, out _) || 
            float.TryParse(value, out _) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        
        // Skip if it contains spaces or special characters (not a valid class name)
        if (value.Contains(' ') || value.Contains('\n') || value.Contains('<') || value.Contains('>'))
        {
            return false;
        }
        
        // Should start with a letter or underscore
        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            return false;
        }
        
        // Common class name patterns
        if (value.Contains('.'))
        {
            // Looks like a fully qualified name
            return true;
        }
        
        // Common RimWorld class prefixes
        var commonPrefixes = new[] 
        { 
            "Comp", "Thing", "Building", "Verb", "Graphic", "Hediff", 
            "Job", "Work", "Pawn", "Inc", "Ritual", "Quest", "Stat",
            "Room", "Place", "Think", "Gen", "AI"
        };
        
        foreach (var prefix in commonPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        
        // If it ends with common suffixes
        var commonSuffixes = new[] { "Worker", "Driver", "Class", "Handler", "Comp", "Effect" };
        foreach (var suffix in commonSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        
        // Default: assume it could be a class name if it's PascalCase
        return char.IsUpper(value[0]) && value.Length > 2;
    }

    //comps 列表中的 CompProperties_*）
    private IEnumerable<GraphEdge> ExtractUsesCompEdges(XElement defElement, string defId)
    {
        var compElements = defElement.Descendants("comps")
            .Elements("li")
            .Select(li => li.Attribute("Class")?.Value)
            .Where(c => !string.IsNullOrWhiteSpace(c));
        
        foreach (var compClass in compElements)
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = NormalizeClassName(compClass!),
                Kind = EdgeKind.XmlUsesComp
            };
        }
    }
    
    //规范化类名确保包含命名空间
    private string NormalizeClassName(string className)
    {
        // Already has namespace
        if (className.Contains('.'))
            return className;
        
        // Common RimWorld prefixes → RimWorld namespace
        if (className.StartsWith("CompProperties_") || className.StartsWith("Comp_"))
            return $"RimWorld.{className}";
        
        // Verse namespace patterns
        if (className.StartsWith("Verb_") || className.StartsWith("Graphic_") || 
            className.StartsWith("Hediff") || className.StartsWith("ThinkNode"))
            return $"Verse.{className}";
        
        // RimWorld namespace patterns
        if (className.StartsWith("Building_") || className.StartsWith("Thing_") ||
            className.StartsWith("Job") || className.StartsWith("Work") ||
            className.StartsWith("Incident") || className.StartsWith("Quest") ||
            className.StartsWith("Ritual") || className.StartsWith("Room"))
            return $"RimWorld.{className}";
      
        // Default to RimWorld namespace
        return $"RimWorld.{className}";
    }
    
    #endregion
    
    #region XML → XML Edge Extraction
    
    //提取继承边（ParentName）
    private GraphEdge? ExtractInheritsEdge(XElement defElement, string defId)
    {
        var parentName = defElement.Attribute("ParentName")?.Value;

        if (string.IsNullOrWhiteSpace(parentName))
        {
            parentName = defElement.Element("ParentName")?.Value;
        }
        
        if (string.IsNullOrWhiteSpace(parentName))
            return null;
        
        return new GraphEdge
        {
            SourceId = defId,
            TargetId = $"xml:{parentName}", // Note: This creates partial match, will be resolved in graph builder
            Kind = EdgeKind.XmlInherits
        };
    }
    
    //提取引用边（其实不完整，但是我想不出其他来了）
    private IEnumerable<GraphEdge> ExtractReferencesEdges(XElement defElement, string defId, string? defType)
    {
        return defType switch
        {
            "RecipeDef" => ExtractRecipeDefReferences(defElement, defId),
            "PawnKindDef" => ExtractPawnKindDefReferences(defElement, defId),
            "ResearchProjectDef" => ExtractResearchProjectReferences(defElement, defId),
            "ThingDef" => ExtractThingDefReferences(defElement, defId),
            _ => Enumerable.Empty<GraphEdge>()
        };
    }
    
    private IEnumerable<GraphEdge> ExtractRecipeDefReferences(XElement element, string defId)
    {
        var products = element.Descendants("products")
            .Elements()
            .Select(e => $"xml:{e.Name.LocalName}");
        
        foreach (var productId in products)
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = productId,
                Kind = EdgeKind.XmlReferences
            };
        }

        var ingredients = element.Descendants("ingredients")
            .SelectMany(ing => ing.Descendants("thingDefs").Elements())
            .Select(e => $"xml:{e.Value}");
        
        foreach (var ingredientId in ingredients)
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = ingredientId,
                Kind = EdgeKind.XmlReferences
            };
        }
    }
    
    private IEnumerable<GraphEdge> ExtractPawnKindDefReferences(XElement element, string defId)
    {
        var race = element.Element("race")?.Value;
        if (!string.IsNullOrWhiteSpace(race))
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = $"xml:{race}",
                Kind = EdgeKind.XmlReferences
            };
        }
    }
    
    private IEnumerable<GraphEdge> ExtractResearchProjectReferences(XElement element, string defId)
    {
        var prerequisites = element.Descendants("prerequisites")
            .Elements("li")
            .Select(e => $"xml:{e.Value}");
        
        foreach (var prereqId in prerequisites)
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = prereqId,
                Kind = EdgeKind.XmlReferences
            };
        }
    }
    
    private IEnumerable<GraphEdge> ExtractThingDefReferences(XElement element, string defId)
    {
        var costs = element.Descendants("costList")
            .Elements()
            .Select(e => $"xml:{e.Name.LocalName}");
        
        foreach (var costId in costs)
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = costId,
                Kind = EdgeKind.XmlReferences
            };
        }
    }
    
    #endregion
}
