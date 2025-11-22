using System.Text;
using System.Xml.Linq;

namespace RimWorldCodeRag.Common;

//一个简单的算法，通过xml的label,description,statBases,comps等字段，生成一个语义化的标题，方便嵌入模型理解。直接掺杂xml格式的内容对向量精准性的伤害很大
public sealed class XmlTitleBuilder
{
    private readonly XElement _element;
    private readonly string? _defName;
    private readonly string _defType;
    private readonly StringBuilder _sb = new();
    private readonly Dictionary<string, string?> _fieldCache = new();

    public XmlTitleBuilder(XElement element, string? defName, string defType)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _defName = defName;
        _defType = defType ?? throw new ArgumentNullException(nameof(defType));
    }

    public string Build()
    {
        BuildCommonFields();

        var result = _defType switch
        {
            "ThingDef" => BuildThingDefSpecific(),
            "RecipeDef" => BuildRecipeDefSpecific(),
            "HediffDef" => BuildHediffDefSpecific(),
            "PawnKindDef" => BuildPawnKindDefSpecific(),
            "GeneDef" => BuildGeneDefSpecific(),
            _ => _sb.ToString() // 除此以外的其他def，只生成通用字段
        };

        return result;
    }

    #region Common Fields

    private void BuildCommonFields()
    {
        _sb.Append($"{_defType}: {_defName ?? "Unknown"}");

        var parent = GetElement("parentName", "ParentName");
        if (parent != null)
        {
            _sb.Append($" (inherits {parent})");
        }
        _sb.AppendLine();

        AppendField("Label", GetElement("label"));

        var desc = GetElement("description");
        if (desc != null)
        {
            if (desc.Length > 150)
            {
                desc = desc.Substring(0, 147) + "...";
            }
            AppendField("Description", desc);
        }

        // c#类型绑定
        var thingClass = GetElement("thingClass", "class", "workerClass", "hediffClass");
        AppendField("Class", thingClass);
    }

    #endregion

    #region ThingDef Specific

    private string BuildThingDefSpecific()
    {
        AppendField("Category", GetElement("designationCategory"));

        var statBases = _element.Element("statBases");
        if (statBases != null)
        {
            var importantStats = new[]
            {
                "MaxHitPoints", "WorkToBuild", "Beauty", "Mass",
                "MarketValue", "WorkTableWorkSpeedFactor", "DoorOpenSpeed",
                "Flammability", "MoveSpeed", "ArmorRating_Blunt", "ArmorRating_Sharp"
            };

            var stats = statBases.Elements()
                .Where(e => importantStats.Contains(e.Name.LocalName))
                .Select(e => $"{e.Name.LocalName}={e.Value}")
                .Take(4);

            if (stats.Any())
            {
                _sb.AppendLine($"Stats: {string.Join(", ", stats)}");
            }
        }

        var comps = _element.Element("comps")?.Elements()
            .Select(c => SimplifyCompClass(c.Attribute("Class")?.Value))
            .Where(c => c != null)
            .Take(4);

        if (comps?.Any() == true)
        {
            _sb.AppendLine($"Comps: {string.Join(", ", comps)}");
        }

        var costs = _element.Element("costList")?.Elements()
            .Select(e => $"{e.Name.LocalName}×{e.Value}")
            .Take(3);

        if (costs?.Any() == true)
        {
            _sb.AppendLine($"Cost: {string.Join(", ", costs)}");
        }

        return _sb.ToString();
    }

    private string? SimplifyCompClass(string? fullClass)
    {
        if (fullClass == null) return null;
        return fullClass.Replace("CompProperties_", "").Replace("Comp_", "");
    }

    #endregion

    #region RecipeDef Specific

    private string BuildRecipeDefSpecific()
    {
        var users = _element.Element("recipeUsers")?.Elements()
            .Select(e => e.Value)
            .Take(3);
        if (users?.Any() == true)
        {
            _sb.AppendLine($"Workbenches: {string.Join(", ", users)}");
        }

        var products = _element.Element("products")?.Elements()
            .Select(e => $"{e.Name.LocalName}×{e.Value}")
            .Take(3);
        if (products?.Any() == true)
        {
            _sb.AppendLine($"Products: {string.Join(", ", products)}");
        }

        var ingredients = _element.Element("ingredients")?.Elements()
            .SelectMany(ing => ExtractIngredientInfo(ing))
            .Take(3);
        if (ingredients?.Any() == true)
        {
            _sb.AppendLine($"Ingredients: {string.Join(", ", ingredients)}");
        }

        AppendField("WorkAmount", GetElement("workAmount"));

        return _sb.ToString();
    }

    private IEnumerable<string> ExtractIngredientInfo(XElement ingredient)
    {
        var filter = ingredient.Element("filter");
        var count = ingredient.Element("count")?.Value ?? "?";

        var thingDefs = filter?.Element("thingDefs")?.Elements()
            .Select(e => $"{e.Value}×{count}");
        if (thingDefs?.Any() == true)
        {
            return thingDefs;
        }

        var categories = filter?.Element("categories")?.Elements()
            .Select(e => $"{e.Value}×{count}");
        return categories ?? Enumerable.Empty<string>();
    }

    #endregion

    #region HediffDef Specific

    private string BuildHediffDefSpecific()
    {
        if (GetElement("tendable") == "true" || GetElement("tendable") == "True")
        {
            _sb.AppendLine("Tendable: Yes");
        }

        AppendField("MaxSeverity", GetElement("maxSeverity"));

        var stages = _element.Element("stages")?.Elements().ToList();
        if (stages?.Count > 0)
        {
            _sb.AppendLine($"Stages: {stages.Count}");

            var firstEffects = ExtractStageEffects(stages[0]);
            if (firstEffects != null)
            {
                _sb.AppendLine($"Stage 1: {firstEffects}");
            }

            if (stages.Count > 1)
            {
                var lastEffects = ExtractStageEffects(stages[^1]);
                if (lastEffects != null)
                {
                    _sb.AppendLine($"Stage {stages.Count}: {lastEffects}");
                }
            }
        }

        AppendField("Lethal at severity", GetElement("lethalSeverity"));

        return _sb.ToString();
    }

    private string? ExtractStageEffects(XElement stage)
    {
        var effects = new List<string>();

        var pain = stage.Element("painFactor")?.Value ?? stage.Element("painOffset")?.Value;
        if (pain != null)
        {
            effects.Add($"pain {pain}");
        }

        var consciousness = stage.Element("consciousnessFactor")?.Value;
        if (consciousness != null)
        {
            effects.Add($"consciousness ×{consciousness}");
        }

        var capMods = stage.Element("capMods")?.Elements();
        if (capMods != null)
        {
            var modList = capMods
                .Select(c =>
                {
                    var capacity = c.Element("capacity")?.Value;
                    var offset = c.Element("offset")?.Value;
                    if (!string.IsNullOrEmpty(capacity) && !string.IsNullOrEmpty(offset))
                    {
                        return $"{capacity} {offset}";
                    }
                    return null;
                })
                .Where(s => s != null)
                .Take(2);
            
            effects.AddRange(modList!);
        }

        return effects.Any() ? string.Join(", ", effects) : null;
    }

    #endregion

    #region PawnKindDef Specific

    private string BuildPawnKindDefSpecific()
    {
        AppendField("Race", GetElement("race"));
        AppendField("CombatPower", GetElement("combatPower"));

        var weapons = _element.Descendants("weaponTags")
            .SelectMany(e => e.Elements())
            .Select(e => e.Value)
            .Distinct()
            .Take(3);
        
        if (weapons.Any())
        {
            _sb.AppendLine($"Weapons: {string.Join(", ", weapons)}");
        }

        return _sb.ToString();
    }

    #endregion

    #region GeneDef Specific

    private string BuildGeneDefSpecific()
    {
        AppendField("Metabolic", GetElement("biostatMet"));

        var statOffsets = _element.Element("statOffsets")?.Elements()
            .Select(e => $"{e.Name.LocalName} {e.Value}")
            .Take(3);
        if (statOffsets?.Any() == true)
        {
            _sb.AppendLine($"Effects: {string.Join(", ", statOffsets)}");
        }

        AppendField("Prerequisite", GetElement("prerequisite"));

        return _sb.ToString();
    }

    #endregion

    #region Helper Methods

    private void AppendField(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _sb.AppendLine($"{name}: {value}");
        }
    }

    //召回
    private string? GetElement(params string[] candidateNames)
    {
        foreach (var name in candidateNames)
        {
            if (_fieldCache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var value = _element.Element(name)?.Value;
            
            if (value == null)
            {
                value = _element.Attribute(name)?.Value;
            }

            if (value == null)
            {
                value = _element.Descendants(name).FirstOrDefault()?.Value;
            }

            _fieldCache[name] = value;
            if (value != null)
            {
                return value;
            }
        }
        return null;
    }

    #endregion
}
