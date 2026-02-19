using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

internal sealed class GraphBuilder
{
    private readonly string _graphBasePath;
    private readonly int _maxDegreeOfParallelism;
    private readonly bool _useSemanticAnalysis;
    private readonly IReadOnlyList<string>? _referenceDllPaths;

    public GraphBuilder(string databasePath, int maxDegreeOfParallelism, bool useSemanticAnalysis = true, IReadOnlyList<string>? referenceDllPaths = null)
    {
        _graphBasePath = NormalizeBasePath(databasePath);
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        _useSemanticAnalysis = useSemanticAnalysis;
        _referenceDllPaths = referenceDllPaths;
    }

    public void BuildGraph(IReadOnlyList<ChunkRecord> chunks)
    {
        Console.WriteLine("[graph] Building knowledge graph ...");
        Directory.CreateDirectory(Path.GetDirectoryName(_graphBasePath)!);
        
        var csharpChunks = chunks.Where(c => c.Language == LanguageKind.CSharp).ToList();
        var xmlChunks = chunks.Where(c => c.Language == LanguageKind.Xml).ToList();
        
        Console.WriteLine($"[graph] Processing {csharpChunks.Count} C# chunks, {xmlChunks.Count} XML chunks");

        Console.WriteLine("[graph] Creating symbol lookup tables ...");
        var symbolLookup = csharpChunks
            .GroupBy(c => c.SymbolName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        
        // Group XML chunks by SymbolId to handle duplicates (e.g., same def name in multiple files)
        var xmlLookup = xmlChunks
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase); // Use first occurrence
        


        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[graph] Symbol lookup: {symbolLookup.Count} unique symbols, {xmlLookup.Count} unique XML definitions");
        Console.ResetColor();

        // Create a set of all valid chunk IDs for filtering semantic analysis results
        var validChunkIds = new HashSet<string>(chunks.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

        // Phase 1: 构建 C# → C# 边
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("[graph] Phase 1: Analyzing C# code references ...");
        var allEdges = new ConcurrentBag<GraphEdge>();
        
        IReadOnlyCollection<GraphEdge> csharpEdges;
        if (_useSemanticAnalysis)
        {
            Console.WriteLine("[graph] Using Roslyn semantic analysis for accurate dependency resolution");
            csharpEdges = CollectCSharpEdgesWithSemanticAnalysis(csharpChunks, validChunkIds);
        }
        else
        {
            Console.WriteLine("[graph] Using syntactic analysis (less accurate, but faster)");
            csharpEdges = CollectCSharpEdges(csharpChunks, symbolLookup);
        }
        
        foreach (var edge in csharpEdges)
        {
            allEdges.Add(edge);
        }
        
        // Phase 2: 构建 XML → C# 边（优先级最高）
        Console.WriteLine("[graph] Phase 2: Extracting XML → C# edges ...");
        var xmlToCSharpEdges = BuildXmlToCSharpEdges(xmlChunks);
        var xmlToCSharpCount = 0;
        foreach (var edge in xmlToCSharpEdges)
        {
            allEdges.Add(edge);
            xmlToCSharpCount++;
        }
        Console.WriteLine($"[graph] Extracted {xmlToCSharpCount} XML → C# edges");
        
        // Phase 3: 构建 XML → XML 边（优先级中等）
        Console.WriteLine("[graph] Phase 3: Extracting XML → XML edges ...");
        var xmlToXmlEdges = BuildXmlToXmlEdges(xmlChunks);
        var xmlToXmlCount = 0;
        foreach (var edge in xmlToXmlEdges)
        {
            allEdges.Add(edge);
            xmlToXmlCount++;
        }
        Console.WriteLine($"[graph] Extracted {xmlToXmlCount} XML → XML edges");
        
        // Phase 4: 生成 C# → XML 反向边（优先级低）
        Console.WriteLine("[graph] Phase 4: Generating reverse edges (C# → XML) ...");
        var reverseEdges = BuildReverseEdges(allEdges);
        var reverseCount = 0;
        foreach (var edge in reverseEdges)
        {
            allEdges.Add(edge);
            reverseCount++;
        }
        Console.WriteLine($"[graph] Generated {reverseCount} reverse edges");

        var edges = allEdges.ToArray();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[graph] Total edges: {edges.Length}");
        Console.ResetColor();
        Console.WriteLine("[graph] Writing sparse adjacency matrices ...");
        var (nodeToIndex, indexToNode) = SparseGraphWriter.Write(_graphBasePath, chunks, edges);

        Console.WriteLine("[graph] Calculating PageRank for graph nodes ...");
        CalculateAndSavePageRank(_graphBasePath, nodeToIndex.Count, indexToNode);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[graph] Knowledge graph build complete");
        Console.ResetColor();
    }

    private void CalculateAndSavePageRank(string basePath, int nodeCount, IReadOnlyDictionary<int, string> indexToNode)
    {
        var csrPath = basePath + ".csr.bin";
        var cscPath = basePath + ".csc.bin";

        if (!File.Exists(csrPath) || !File.Exists(cscPath))
        {
            Console.Error.WriteLine("[graph] CSR or CSC files not found, skipping PageRank calculation.");
            return;
        }

        var (csrRowPointers, csrColumnIndices, _) = RimWorldCodeRag.Retrieval.GraphQuerier.LoadBinary(csrPath, "CSR1");
        var (cscColPointers, cscRowIndices, _) = RimWorldCodeRag.Retrieval.GraphQuerier.LoadBinary(cscPath, "CSC1");

        var scores = PageRankCalculator.Calculate(
            nodeCount,
            csrRowPointers,
            csrColumnIndices,
            cscColPointers,
            cscRowIndices
        );

        var outputPath = basePath + ".pagerank.tsv";
        using var writer = new StreamWriter(outputPath);
        foreach (var kvp in scores.OrderByDescending(kvp => kvp.Value))
        {
            if (indexToNode.TryGetValue(kvp.Key, out var symbolName))
            {
                writer.WriteLine($"{symbolName}\t{kvp.Value:F6}");
            }
        }
        Console.WriteLine($"[graph] PageRank scores saved to {outputPath}");
    }

    internal static string NormalizeBasePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Path.HasExtension(fullPath))
        {
            var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var fileName = Path.GetFileNameWithoutExtension(fullPath);
            return Path.Combine(directory, fileName);
        }

        return fullPath;
    }

    #region Semantic Analysis (M7 - New approach)

    /// <summary>
    /// Collects C# edges using Roslyn's SemanticModel for accurate symbol resolution.
    /// This method correctly handles:
    /// - Method overload resolution
    /// - Static member resolution
    /// - Inheritance and interface implementations
    /// </summary>
    private IReadOnlyCollection<GraphEdge> CollectCSharpEdgesWithSemanticAnalysis(
        IReadOnlyList<ChunkRecord> csharpChunks, 
        HashSet<string> validChunkIds)
    {
        var edgeBag = new ConcurrentBag<GraphEdge>();

        // Group chunks by file path to process each file once
        var chunksByFile = csharpChunks
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var sourceDirectory = Path.GetDirectoryName(csharpChunks.FirstOrDefault()?.Path ?? ".");
        if (string.IsNullOrEmpty(sourceDirectory))
        {
            sourceDirectory = ".";
        }

        // Find the common root directory for all source files
        var allPaths = csharpChunks.Select(c => c.Path).ToList();
        sourceDirectory = FindCommonRoot(allPaths);

        Console.WriteLine($"[graph] Creating Roslyn compilation from {sourceDirectory}...");
        var compilation = RoslynProject.CreateCompilation(sourceDirectory, _referenceDllPaths);

        // Create file path to chunk mapping for efficient lookup
        var filePathToChunks = chunksByFile;

        var processed = 0;
        var total = Math.Max(1, compilation.SyntaxTrees.Count());
        var lastReportedPercent = -1;

        // Process each syntax tree
        Parallel.ForEach(compilation.SyntaxTrees, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, tree =>
        {
            try
            {
                if (!filePathToChunks.TryGetValue(tree.FilePath, out var chunksInFile))
                {
                    return; // Skip files that don't have any chunks
                }

                SemanticModel semanticModel;
                try
                {
                    semanticModel = compilation.GetSemanticModel(tree);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[graph] Error getting SemanticModel for {tree.FilePath}: {ex.Message}");
                    return;
                }

                var root = tree.GetRoot();

                foreach (var chunk in chunksInFile)
                {
                    // Find the syntax node corresponding to this chunk
                    var chunkSpan = new Microsoft.CodeAnalysis.Text.TextSpan(chunk.SpanStart, chunk.SpanEnd - chunk.SpanStart);
                    var nodeInSpan = root.DescendantNodes()
                        .FirstOrDefault(n => n.Span.Start >= chunk.SpanStart && n.Span.End <= chunk.SpanEnd && n is TypeDeclarationSyntax or MemberDeclarationSyntax);

                    if (nodeInSpan == null)
                    {
                        // Fallback: walk the entire file with chunk ID
                        var walker = new CSharpSemanticWalker(semanticModel, chunk.Id, validChunkIds);
                        walker.Visit(root);
                        foreach (var edge in walker.Edges)
                        {
                            edgeBag.Add(edge);
                        }
                    }
                    else
                    {
                        // Walk only the specific node for this chunk
                        var walker = new CSharpSemanticWalker(semanticModel, chunk.Id, validChunkIds);
                        walker.Visit(nodeInSpan);
                        foreach (var edge in walker.Edges)
                        {
                            edgeBag.Add(edge);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[graph] Error processing {tree.FilePath}: {ex.Message}");
            }

            var current = Interlocked.Increment(ref processed);
            var percent = (current * 100) / total;
            if (percent > lastReportedPercent)
            {
                var oldPercent = Interlocked.Exchange(ref lastReportedPercent, percent);
                if (percent > oldPercent)
                {
                    Console.Write($"\r[graph] Semantic analysis {current}/{total} ({percent}%) - {edgeBag.Count} edges found");
                }
            }
        });

        Console.WriteLine($"\n[graph] Semantic analysis complete: {processed} files analyzed, {edgeBag.Count} edges found");

        return edgeBag;
    }

    private static string FindCommonRoot(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return ".";
        }

        // Get the full paths and normalize them
        var normalizedPaths = paths
            .Select(p => Path.GetFullPath(Path.GetDirectoryName(p) ?? "."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return ".";
        }

        var commonRoot = normalizedPaths[0];

        foreach (var dir in normalizedPaths.Skip(1))
        {
            // Shrink commonRoot until both paths share it as a prefix
            while (!string.IsNullOrEmpty(commonRoot) && 
                   !dir.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase) &&
                   !dir.StartsWith(commonRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(commonRoot);
                if (string.IsNullOrEmpty(parent))
                {
                    // No common root found, return root directory
                    return Path.GetPathRoot(normalizedPaths[0]) ?? ".";
                }
                commonRoot = parent;
            }
        }

        return string.IsNullOrEmpty(commonRoot) ? "." : commonRoot;
    }

    #endregion

    private IReadOnlyCollection<GraphEdge> CollectCSharpEdges(IReadOnlyList<ChunkRecord> csharpChunks, IDictionary<string, string[]> symbolLookup)
    {
        var edgeBag = new ConcurrentBag<GraphEdge>();
        var resolutionCache = new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        var processed = 0;
        var total = Math.Max(1, csharpChunks.Count);
        var lastReportedPercent = -1;

        Parallel.ForEach(csharpChunks, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, chunk =>
        {
            try
            {
                foreach (var edge in CollectEdgesForChunk(chunk, symbolLookup, resolutionCache))
                {
                    edgeBag.Add(edge);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[graph] failed to process {chunk.Id}: {ex.Message}");
            }

            var current = Interlocked.Increment(ref processed);
            var percent = (current * 100) / total;
            if (percent > lastReportedPercent)
            {
                var oldPercent = Interlocked.Exchange(ref lastReportedPercent, percent);
                if (percent > oldPercent)
                {
                    Console.Write($"\r[graph] Analyzing code references {current}/{total} ({percent}%) - {edgeBag.Count} edges found");
                }
            }
        });

        Console.WriteLine($"\n[graph] C# code analysis complete: {processed} chunks analyzed, {edgeBag.Count} edges found");

        return edgeBag;
    }
    
    private IEnumerable<GraphEdge> BuildXmlToCSharpEdges(List<ChunkRecord> xmlChunks)
    {
        var extractor = new XmlGraphExtractor();
        var edgeBag = new ConcurrentBag<GraphEdge>();
        
        Parallel.ForEach(xmlChunks, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, chunk =>
        {
            try
            {
                var element = System.Xml.Linq.XElement.Parse(chunk.Text);
                var edges = extractor.ExtractXmlToCSharpEdges(element, chunk.Id, chunk.DefType);
                
                foreach (var edge in edges)
                {
                    edgeBag.Add(edge);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[graph] Failed to extract XML→C# edges from {chunk.Id}: {ex.Message}");
            }
        });
        
        return edgeBag;
    }
    
    private IEnumerable<GraphEdge> BuildXmlToXmlEdges(List<ChunkRecord> xmlChunks)
    {
        var extractor = new XmlGraphExtractor();
        var edgeBag = new ConcurrentBag<GraphEdge>();
        
        // 构建 defName → ChunkRecord 映射，用于验证引用有效性
        // Include both full SymbolIds and partial names for inheritance matching
        var validDefIds = new HashSet<string>(xmlChunks.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var validDefNames = new HashSet<string>(xmlChunks.Select(c => c.SymbolName), StringComparer.OrdinalIgnoreCase);
        
        Parallel.ForEach(xmlChunks, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, chunk =>
        {
            try
            {
                var element = System.Xml.Linq.XElement.Parse(chunk.Text);
                var edges = extractor.ExtractXmlToXmlEdges(element, chunk.Id, chunk.DefType, validDefIds, validDefNames);
                
                foreach (var edge in edges)
                {
                    edgeBag.Add(edge);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[graph] Failed to extract XML→XML edges from {chunk.Id}: {ex.Message}");
            }
        });
        
        return edgeBag;
    }
    
    private IEnumerable<GraphEdge> BuildReverseEdges(ConcurrentBag<GraphEdge> allEdges)
    {
        var xmlToCSharpEdges = allEdges
            .Where(e => e.Kind == EdgeKind.XmlBindsClass || e.Kind == EdgeKind.XmlUsesComp)
            .ToList();
        
        foreach (var edge in xmlToCSharpEdges)
        {
            // 添加反向边：C# 类 → XML Def
            yield return new GraphEdge
            {
                SourceId = edge.TargetId,  // C# 类
                TargetId = edge.SourceId,  // XML Def
                Kind = EdgeKind.CSharpUsedByDef
            };
        }
    }

    private IEnumerable<GraphEdge> CollectEdgesForChunk(ChunkRecord chunk, IDictionary<string, string[]> symbolLookup, ConcurrentDictionary<string, string[]> resolutionCache)
    {
        var tree = CSharpSyntaxTree.ParseText(chunk.Text);
        var root = tree.GetRoot();
        var chunkSpan = new Microsoft.CodeAnalysis.Text.TextSpan(chunk.SpanStart, chunk.SpanEnd - chunk.SpanStart);

        // Inheritance edge
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classNode?.BaseList != null)
        {
            foreach (var baseType in classNode.BaseList.Types)
            {
                foreach (var resolved in ResolveTargets(baseType.Type.ToString(), symbolLookup, resolutionCache))
                {
                    yield return new GraphEdge
                    {
                        SourceId = chunk.Id,
                        TargetId = resolved,
                        Kind = EdgeKind.Inherits
                    };
                }
            }
        }

        // Only look for nodes within this specific class, not the entire file
        var nodesToSearch = classNode?.DescendantNodes() ?? Enumerable.Empty<SyntaxNode>();

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!chunkSpan.Contains(memberAccess.Span)) continue;

            // If the parent is an invocation, it's already handled by the Invocation loop.
            if (memberAccess.Parent is InvocationExpressionSyntax) continue;

            var targetName = memberAccess.ToString();
            foreach (var resolved in ResolveTargets(targetName, symbolLookup, resolutionCache))
            {
                yield return new GraphEdge
                {
                    SourceId = chunk.Id,
                    TargetId = resolved,
                    Kind = EdgeKind.References
                };
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!chunkSpan.Contains(invocation.Span)) continue;

            var targetName = ExtractInvocationTarget(invocation.Expression);
            if (targetName == null)
            {
                continue;
            }

            foreach (var resolved in ResolveTargets(targetName, symbolLookup, resolutionCache))
            {
                yield return new GraphEdge
                {
                    SourceId = chunk.Id,
                    TargetId = resolved,
                    Kind = EdgeKind.Calls
                };
            }
        }

        foreach (var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (!chunkSpan.Contains(objectCreation.Span)) continue;

            var targetName = objectCreation.Type.ToString();
            foreach (var resolved in ResolveTargets(targetName, symbolLookup, resolutionCache))
            {
                yield return new GraphEdge
                {
                    SourceId = chunk.Id,
                    TargetId = resolved,
                    Kind = EdgeKind.References
                };
            }
        }
    }

    private IEnumerable<string> ResolveTargets(string targetName, IDictionary<string, string[]> symbolLookup, ConcurrentDictionary<string, string[]> resolutionCache)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            yield break;
        }

        if (resolutionCache.TryGetValue(targetName, out var cached))
        {
            foreach (var item in cached)
            {
                yield return item;
            }
            yield break;
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1. Exact match (highest confidence)
        if (symbolLookup.TryGetValue(targetName, out var directHits))
        {
            foreach (var hit in directHits)
            {
                if (seen.Add(hit))
                {
                    results.Add(hit);
                }
            }
        }

        // 2. If it's a qualified name (e.g., "GenAdjFast.AdjacentCellsCardinal"),
        //    try to find symbols that end with it. This helps with cases where
        //    the full namespace isn't available.
        if (results.Count == 0 && targetName.Contains('.'))
        {
            foreach (var candidate in symbolLookup)
            {
                if (candidate.Key.EndsWith("." + targetName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var value in candidate.Value)
                    {
                        if (seen.Add(value))
                        {
                            results.Add(value);
                        }
                    }
                }
            }
        }

        // If it's a simple name (like "Contains") and we didn't get a direct hit,
        // we stop. We no longer guess with a broad "EndsWith" search.

        var array = results.ToArray();
        resolutionCache[targetName] = array;

        foreach (var item in array)
        {
            yield return item;
        }
    }

    private static string? ExtractInvocationTarget(ExpressionSyntax expression)
    {
        // Always return the full expression string to preserve context like "FilthMaker.TryMakeFilth"
        return expression.ToString();
    }
}
