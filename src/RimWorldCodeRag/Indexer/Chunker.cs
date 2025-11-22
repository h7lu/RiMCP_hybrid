using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RimWorldCodeRag.Common;
using CommonSymbolKind = RimWorldCodeRag.Common.SymbolKind;

namespace RimWorldCodeRag.Indexer;

internal sealed class Chunker
{
    private readonly IndexingConfig _config;
    private readonly MetadataStore _metadataStore;

    public Chunker(IndexingConfig config, MetadataStore metadataStore)
    {
        _config = config;
        _metadataStore = metadataStore;
    }

    public IReadOnlyList<ChunkRecord> BuildChunks()
    {
        var sourceRoot = _config.SourceRoot;
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Source root '{sourceRoot}' does not exist.");
        }

        var files = Directory
            .EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var changedFiles = FilterChangedFiles(files);
        var chunkBag = new ConcurrentBag<ChunkRecord>();

        Parallel.ForEach(changedFiles, new ParallelOptions { MaxDegreeOfParallelism = _config.MaxDegreeOfParallelism }, file =>
        {
            try
            {
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var chunk in ChunkCSharpFile(file))
                    {
                        chunkBag.Add(chunk);
                    }
                }
                else if (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var chunk in ChunkXmlFile(file))
                    {
                        chunkBag.Add(chunk);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[chunker] failed to process {file}: {ex.Message}");
            }
        });

        var results = chunkBag.ToList();
        foreach (var file in changedFiles)
        {
            _metadataStore.SetTimestamp(file, File.GetLastWriteTimeUtc(file));
        }

        return results;
    }

    public IReadOnlyList<ChunkRecord> BuildFullSnapshot()
    {
        var sourceRoot = _config.SourceRoot;
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Source root '{sourceRoot}' does not exist.");
        }

        var files = Directory
            .EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var chunkBag = new ConcurrentBag<ChunkRecord>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = _config.MaxDegreeOfParallelism }, file =>
        {
            try
            {
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var chunk in ChunkCSharpFile(file))
                    {
                        chunkBag.Add(chunk);
                    }
                }
                else if (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var chunk in ChunkXmlFile(file))
                    {
                        chunkBag.Add(chunk);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[chunker] failed to process {file}: {ex.Message}");
            }
        });

        return chunkBag.ToList();
    }

    private IReadOnlyList<string> FilterChangedFiles(string[] files)
    {
        var list = new List<string>(files.Length);
        foreach (var file in files)
        {
            var lastWrite = File.GetLastWriteTimeUtc(file);
            var previous = _metadataStore.GetTimestamp(file);
            if (!_config.Incremental || previous == null || Math.Abs((lastWrite - previous.Value.UtcDateTime).TotalSeconds) > 0.5)
            {
                list.Add(file);
            }
        }

        return list;
    }

    private IEnumerable<ChunkRecord> ChunkCSharpFile(string file)
    {
        var text = File.ReadAllText(file);
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
        var root = tree.GetCompilationUnitRoot();
        var sourceText = tree.GetText();

        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var chunk = CreateChunkForMember(member, file, sourceText);
            if (chunk is { } c)
            {
                yield return c;
            }
        }
    }

    private ChunkRecord? CreateChunkForMember(MemberDeclarationSyntax member, string file, SourceText sourceText)
    {
        switch (member)
        {
            case TypeDeclarationSyntax type:
                return BuildTypeChunk(type, file, sourceText);
            case MethodDeclarationSyntax method:
                return BuildMethodChunk(method, file, sourceText);
            case ConstructorDeclarationSyntax ctor:
                return BuildConstructorChunk(ctor, file, sourceText);
            case PropertyDeclarationSyntax prop:
                return BuildPropertyChunk(prop, file, sourceText);
            case FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.ConstKeyword):
                return BuildConstFieldChunk(field, file, sourceText);
            default:
                return null;
        }
    }

    private ChunkRecord? BuildTypeChunk(TypeDeclarationSyntax type, string file, SourceText sourceText)
    {
        // Build type declaration with base class: "RimWorld.SomeClass : SuperClass"
        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty;
        var typeName = type.Identifier.Text;
        
        var baseType = type.BaseList?.Types.FirstOrDefault()?.Type.ToString();
        var declaration = !string.IsNullOrEmpty(baseType) 
            ? $"{typeName} : {baseType}"
            : typeName;
        
        // Build full symbol ID
        var symbolId = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
        
        // For type declarations, include interfaces/base classes in identifiers
        var identifiers = new List<string> { typeName };
        if (type.BaseList != null)
        {
            identifiers.AddRange(type.BaseList.Types.Select(t => t.Type.ToString()));
        }
        
        var symbolInfo = (symbolId, ns, string.Empty, typeName);
        return BuildChunkRecord(type, file, sourceText, LanguageKind.CSharp, CommonSymbolKind.Type, symbolInfo, declaration, identifiers);
    }

    private ChunkRecord? BuildMethodChunk(MethodDeclarationSyntax method, string file, SourceText sourceText)
    {
        var signature = BuildMethodSignature(method);
        var symbolId = BuildSymbolId(method, signature);
        if (symbolId == null)
        {
            return null;
        }

        var bodyNode = (SyntaxNode?)method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression ?? method;
    var identifiers = CollectIdentifiers(bodyNode);
        return BuildChunkRecord(method, file, sourceText, LanguageKind.CSharp, CommonSymbolKind.Method, symbolId.Value, signature, identifiers);
    }

    private ChunkRecord? BuildConstructorChunk(ConstructorDeclarationSyntax ctor, string file, SourceText sourceText)
    {
        var signature = BuildConstructorSignature(ctor);
        var symbolId = BuildSymbolId(ctor, signature);
        if (symbolId == null)
        {
            return null;
        }

        var bodyNode = (SyntaxNode?)ctor.Body ?? (SyntaxNode?)ctor.ExpressionBody?.Expression ?? ctor;
    var identifiers = CollectIdentifiers(bodyNode);
        return BuildChunkRecord(ctor, file, sourceText, LanguageKind.CSharp, CommonSymbolKind.Constructor, symbolId.Value, signature, identifiers);
    }    private ChunkRecord? BuildPropertyChunk(PropertyDeclarationSyntax prop, string file, SourceText sourceText)
    {
        // Build declaration format: "public float SomeProperty { get; set; }"
        var declaration = BuildPropertyDeclaration(prop);
        var symbolId = BuildSymbolId(prop, declaration);
        if (symbolId == null)
        {
            return null;
        }

    var bodyNode = (SyntaxNode?)prop.ExpressionBody?.Expression ?? (SyntaxNode?)prop.AccessorList ?? prop;
    var identifiers = CollectIdentifiers(bodyNode);
        return BuildChunkRecord(prop, file, sourceText, LanguageKind.CSharp, CommonSymbolKind.Property, symbolId.Value, declaration, identifiers);
    }

    private ChunkRecord? BuildConstFieldChunk(FieldDeclarationSyntax field, string file, SourceText sourceText)
    {
        if (field.Declaration == null)
        {
            return null;
        }

        var variable = field.Declaration.Variables.FirstOrDefault();
        if (variable == null)
        {
            return null;
        }

        // Build declaration format: "public const float SomeField = 0.9f"
        var declaration = BuildFieldDeclaration(field, variable);
        var symbolId = BuildSymbolId(field, declaration);
        if (symbolId == null)
        {
            return null;
        }

        var identifiers = CollectIdentifiers(field);
        return BuildChunkRecord(field, file, sourceText, LanguageKind.CSharp, CommonSymbolKind.Field, symbolId.Value, declaration, identifiers);
    }

    private static ChunkRecord BuildChunkRecord(SyntaxNode node, string file, SourceText sourceText, LanguageKind lang, CommonSymbolKind kind, (string SymbolId, string Namespace, string ContainingType, string Name) symbolInfo, string signature, IReadOnlyCollection<string> identifiers)
    {
        var span = node.Span;
        var preview = TextUtilities.BuildPreview(node.ToFullString());
        var lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        var keywordIdentifiers = identifiers.Distinct(StringComparer.OrdinalIgnoreCase).Select(x => x.ToLowerInvariant()).ToArray();
        var tokens = TextUtilities.SplitIdentifiers(keywordIdentifiers).Distinct().ToArray();

        // Enrich preview with semantic context (parent type + signature) for better embedding quality
        var contextPrefix = BuildContextPrefix(symbolInfo.ContainingType, symbolInfo.Name, signature, kind);
        var enrichedPreview = string.IsNullOrEmpty(contextPrefix) ? preview : $"{contextPrefix}\n{preview}";

        return new ChunkRecord
        {
            Id = symbolInfo.SymbolId,
            Path = file,
            Language = lang,
            Text = node.ToFullString(),
            Preview = enrichedPreview,
            Identifiers = tokens,
            KeywordIdentifiers = keywordIdentifiers,
            SymbolKind = kind,
            SymbolName = symbolInfo.Name,
            Namespace = symbolInfo.Namespace,
            ContainingType = symbolInfo.ContainingType,
            SpanStart = span.Start,
            SpanEnd = span.End,
            StartLine = lineSpan.Start.Line + 1,
            EndLine = lineSpan.End.Line + 1,
            Signature = signature,
            XmlLinks = Array.Empty<string>()
        };
    }

    private static string BuildContextPrefix(string containingType, string name, string signature, CommonSymbolKind kind)
    {
        if (string.IsNullOrEmpty(containingType))
        {
            return string.Empty;
        }

        return kind switch
        {
            CommonSymbolKind.Method => $"{containingType}.{signature}",
            CommonSymbolKind.Constructor => $"{containingType}.{signature}",
            CommonSymbolKind.Property => $"{containingType} -> {signature}",
            CommonSymbolKind.Field => $"{containingType} -> {signature}",
            CommonSymbolKind.Type => signature, // For types, signature already includes base class
            _ => string.Empty
        };
    }

    private static string BuildMethodSignature(MethodDeclarationSyntax method)
    {
        var modifiers = string.Join(" ", method.Modifiers.Select(m => m.Text));
        var returnType = method.ReturnType.ToString();
        var methodName = method.Identifier.Text;
        
        if (method.ParameterList.Parameters.Count == 0)
        {
            return $"{modifiers} {returnType} {methodName}()";
        }

        var paramSignatures = method.ParameterList.Parameters
            .Select(p => $"{p.Type} {p.Identifier.Text}")
            .Where(sig => !string.IsNullOrWhiteSpace(sig));

        return $"{modifiers} {returnType} {methodName}({string.Join(", ", paramSignatures)})";
    }

    private static string BuildConstructorSignature(ConstructorDeclarationSyntax ctor)
    {
        var modifiers = string.Join(" ", ctor.Modifiers.Select(m => m.Text));
        var ctorName = ctor.Identifier.Text;
        
        if (ctor.ParameterList.Parameters.Count == 0)
        {
            return $"{modifiers} {ctorName}()";
        }

        var paramSignatures = ctor.ParameterList.Parameters
            .Select(p => $"{p.Type} {p.Identifier.Text}")
            .Where(sig => !string.IsNullOrWhiteSpace(sig));

        return $"{modifiers} {ctorName}({string.Join(", ", paramSignatures)})";
    }

    private static string BuildPropertyDeclaration(PropertyDeclarationSyntax prop)
    {
        var modifiers = string.Join(" ", prop.Modifiers.Select(m => m.Text));
        var type = prop.Type.ToString();
        var name = prop.Identifier.Text;
        
        if (modifiers.Length > 0)
        {
            return $"{modifiers} {type} {name}";
        }
        return $"{type} {name}";
    }

    private static string BuildFieldDeclaration(FieldDeclarationSyntax field, VariableDeclaratorSyntax variable)
    {
        var modifiers = string.Join(" ", field.Modifiers.Select(m => m.Text));
        var type = field.Declaration.Type.ToString();
        var name = variable.Identifier.Text;
        
        // Include initializer if present (e.g., "= 0.9f")
        var initializer = variable.Initializer?.ToFullString() ?? "";
        
        if (modifiers.Length > 0)
        {
            return $"{modifiers} {type} {name}{initializer}";
        }
        return $"{type} {name}{initializer}";
    }

    private static (string SymbolId, string Namespace, string ContainingType, string Name)? BuildSymbolId(SyntaxNode node, string? methodSignature)
    {
        var typeDecl = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl == null)
        {
            return null;
        }

        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty;
        var typeName = typeDecl.Identifier.Text;
        
        // Extract base name without signature
        string name = node switch
        {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
            PropertyDeclarationSyntax prop => prop.Identifier.Text,
            FieldDeclarationSyntax field => field.Declaration?.Variables.FirstOrDefault()?.Identifier.Text ?? "field",
            _ => "member"
        };

        // For methods/constructors, use full signature in symbol ID to distinguish overloads
        // For properties/fields, just use name
        var symbolName = node switch
        {
            MethodDeclarationSyntax method => !string.IsNullOrEmpty(BuildMethodSignature(method)) ? BuildMethodSignature(method) : name,
            ConstructorDeclarationSyntax ctor => !string.IsNullOrEmpty(BuildConstructorSignature(ctor)) ? BuildConstructorSignature(ctor) : name,
            _ => name
        };

        var symbolId = string.IsNullOrEmpty(ns)
            ? $"{typeName}.{symbolName}"
            : $"{ns}.{typeName}.{symbolName}";

        return (symbolId, ns, typeName, name);
    }

    private static IReadOnlyCollection<string> CollectIdentifiers(SyntaxNode node)
    {
        var list = new List<string>();
        foreach (var token in node.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
        {
            list.Add(token.ValueText);
        }

        return list;
    }

    private IEnumerable<ChunkRecord> ChunkXmlFile(string file)
    {
        XDocument doc;
        using (var stream = File.OpenRead(file))
        {
            doc = XDocument.Load(stream, LoadOptions.SetLineInfo);
        }

        var defElements = doc.Root?.Elements().Where(e => e.Name.LocalName.EndsWith("Def", StringComparison.OrdinalIgnoreCase));
        if (defElements == null)
        {
            yield break;
        }

        foreach (var element in defElements)
        {
            var defName = element.Element("defName")?.Value ?? element.Element("DefName")?.Value;
            if (string.IsNullOrWhiteSpace(defName))
            {
                continue;
            }

            var defType = element.Name.LocalName; // ThingDef, RecipeDef, etc.
            var classValue = element.Element("class")?.Value ?? element.Element("Class")?.Value;
            var parentValue = element.Attribute("ParentName")?.Value ?? element.Element("parentName")?.Value ?? element.Element("parent")?.Value ?? element.Element("ParentName")?.Value;
            var text = element.ToString(SaveOptions.DisableFormatting);
            
            // ✨ Generate semantic title using XmlTitleBuilder
            var titleBuilder = new XmlTitleBuilder(element, defName, defType);
            var semanticTitle = titleBuilder.Build();
            
            // Preview = semantic title + truncated raw XML
            var rawPreview = TextUtilities.BuildPreview(text, maxLength: 200);
            var preview = $"{semanticTitle}\n\n{rawPreview}";
            
            var identifiers = new List<string> { defName };
            if (!string.IsNullOrWhiteSpace(classValue))
            {
                identifiers.Add(classValue);
            }
            if (!string.IsNullOrWhiteSpace(parentValue))
            {
                identifiers.Add(parentValue);
            }

            foreach (var node in element.Descendants())
            {
                if (!string.IsNullOrWhiteSpace(node.Value) && node.Value.Length < 200)
                {
                    identifiers.Add(node.Value.Trim());
                }
            }

            var xmlLinks = element
                .Descendants()
                .SelectMany(n => ExtractXmlLinks(n.Value))
                .Concat(ExtractXmlLinks(classValue))
                .Concat(ExtractXmlLinks(parentValue))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Include defType and parent in SymbolId to distinguish same defs with different inheritance
            var parentSuffix = !string.IsNullOrWhiteSpace(parentValue) ? $" <- {parentValue}" : "";
            var info = (SymbolId: $"xml:{defType}:{defName}{parentSuffix}", Namespace: string.Empty, ContainingType: string.Empty, Name: defName);
            var lineInfo = (IXmlLineInfo)element;
            var startLine = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;
            var endLine = startLine + element.ToString().Count(c => c == '\n');

            var keywordIdentifiers = identifiers.Distinct(StringComparer.OrdinalIgnoreCase).Select(x => x.ToLowerInvariant()).ToArray();
            var tokens = TextUtilities.SplitIdentifiers(keywordIdentifiers).Distinct().ToArray();

            yield return new ChunkRecord
            {
                Id = info.SymbolId,
                Path = file,
                Language = LanguageKind.Xml,
                Text = text,
                Preview = preview, // ← Now includes semantic title
                Identifiers = tokens,
                KeywordIdentifiers = keywordIdentifiers,
                SymbolKind = CommonSymbolKind.XmlDef,
                SymbolName = info.Name,
                Namespace = info.Namespace,
                ContainingType = info.ContainingType,
                SpanStart = 0,
                SpanEnd = text.Length,
                StartLine = startLine,
                EndLine = endLine,
                Signature = $"{defType}:{defName}{parentSuffix}",
                XmlLinks = xmlLinks,
                DefType = defType // ← New field
            };
        }
    }

    private static IEnumerable<string> ExtractXmlLinks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var tokens = value.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (token.EndsWith("Def", StringComparison.OrdinalIgnoreCase))
            {
                yield return token;
            }
        }
    }
}
