using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using F23.StringSimilarity;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Retrieval;

public sealed class GraphQuerier : IDisposable
{
    private const double PageRankScaleFactor = 1e7;
    private readonly JaroWinkler _jaroWinkler = new JaroWinkler();

    private readonly string _basePath;
    private readonly Dictionary<int, string> _indexToSymbol;
    private readonly Dictionary<string, int> _symbolToIndex;
    
    //行优先的压缩稀疏矩阵图
    private readonly int[] _csrRowPointers;
    private readonly int[] _csrColumnIndices;
    private readonly byte[] _csrKinds;

    //列优先的压缩稀疏矩阵图
    private readonly int[] _cscColPointers;
    private readonly int[] _cscRowIndices;
    private readonly byte[] _cscKinds;
    
    private readonly int _nodeCount;
    private readonly int _edgeCount;

    private readonly Dictionary<string, double> _pageRankScores;
    private readonly Dictionary<EdgeKind, double> _edgeWeights;

    public GraphQuerier(string basePath)
    {
        _basePath = basePath;
        
        (_indexToSymbol, _symbolToIndex) = LoadNodes(basePath + ".nodes.tsv");
        _nodeCount = _indexToSymbol.Count;
        
        (_csrRowPointers, _csrColumnIndices, _csrKinds) = LoadBinary(basePath + ".csr.bin", "CSR1");
        
        (_cscColPointers, _cscRowIndices, _cscKinds) = LoadBinary(basePath + ".csc.bin", "CSC1");
        
        _edgeCount = _csrColumnIndices.Length;

        _pageRankScores = LoadPageRank(basePath + ".pagerank.tsv");
        _edgeWeights = new Dictionary<EdgeKind, double>
        {
            { EdgeKind.Inherits, 2.0 },
            { EdgeKind.XmlInherits, 1.8 },
            { EdgeKind.Implements, 0.9 },
            { EdgeKind.Calls, 0.8 },
            { EdgeKind.XmlBindsClass, 0.7 },
            { EdgeKind.XmlUsesComp, 0.6 },
            { EdgeKind.References, 0.5 },
            { EdgeKind.XmlReferences, 0.4 },
            { EdgeKind.CSharpUsedByDef, 0.7 }
        };
    }

    //执行检索
    public PagedGraphQueryResult Query(GraphQueryConfig config)
    {
        // First try to resolve the symbol reference (handles #nodeId format and fuzzy matching)
        var resolvedSymbol = ResolveSymbolReference(config.SymbolId);
        if (resolvedSymbol == null || !_symbolToIndex.TryGetValue(resolvedSymbol, out var nodeIndex))
        {
            return new PagedGraphQueryResult
            {
                Results = Array.Empty<GraphQueryResult>(),
                TotalCount = 0,
                Page = config.Page,
                PageSize = config.PageSize
            };
        }

        var edges = config.Direction == GraphDirection.Uses
            ? GetOutgoingEdges(nodeIndex)
            : GetIncomingEdges(nodeIndex);

        edges = edges.Where(e => IsEdgeValidForDirection(DecodeKind(e.Kind), config.Direction));
        //类型筛选
        if (!string.IsNullOrWhiteSpace(config.Kind))
        {
            edges = FilterByKind(edges, config.Kind, config.Direction);
        }

        var allResults = edges
            .GroupBy(e => new { 
                SymbolId = config.Direction == GraphDirection.Uses ? _indexToSymbol[e.TargetIndex] : _indexToSymbol[e.SourceIndex], 
                EdgeKind = DecodeKind(e.Kind) 
            })
            .Select(g =>
            {
                var symbolId = g.Key.SymbolId;
                var edgeKind = g.Key.EdgeKind;
                var duplicateCount = g.Count();

                var rawPageRank = _pageRankScores.TryGetValue(symbolId, out var pr) ? pr : 0.0;
                var scaledPageRank = rawPageRank * PageRankScaleFactor;

                var edgeWeight = _edgeWeights.TryGetValue(edgeKind, out var ew) ? ew : 0.1; // Default to low weight
                
                // Lexical bonus as a tie-breaker
                var lexicalBonus = _jaroWinkler.Similarity(config.SymbolId, symbolId);

                var initialScore = scaledPageRank * edgeWeight;
                var finalScore = (initialScore * Math.Sqrt(duplicateCount)) * lexicalBonus;

                return new GraphQueryResult
                {
                    SymbolId = symbolId,
                    EdgeKind = edgeKind,
                    Distance = 1,
                    Score = finalScore,
                    PageRank = scaledPageRank,
                    DuplicateCount = duplicateCount
                };
            })
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.SymbolId, StringComparer.Ordinal) // Stable sort
            .ToList();

        var pagedResults = allResults
            .Skip((config.Page - 1) * config.PageSize)
            .Take(config.PageSize)
            .ToList();
        
        return new PagedGraphQueryResult
        {
            Results = pagedResults,
            TotalCount = allResults.Count,
            Page = config.Page,
            PageSize = config.PageSize
        };
    }

    //出边。不是外向型人格
    private IEnumerable<RawEdge> GetOutgoingEdges(int nodeIndex)
    {
        var start = _csrRowPointers[nodeIndex];
        var end = _csrRowPointers[nodeIndex + 1];
        
        for (var i = start; i < end; i++)
        {
            yield return new RawEdge
            {
                SourceIndex = nodeIndex,
                TargetIndex = _csrColumnIndices[i],
                Kind = _csrKinds[i]
            };
        }
    }

    //入边。不是收入
    private IEnumerable<RawEdge> GetIncomingEdges(int nodeIndex)
    {
        var start = _cscColPointers[nodeIndex];
        var end = _cscColPointers[nodeIndex + 1];
        
        for (var i = start; i < end; i++)
        {
            yield return new RawEdge
            {
                SourceIndex = _cscRowIndices[i],
                TargetIndex = nodeIndex,
                Kind = _cscKinds[i]
            };
        }
    }

// c# 或 xml的筛选
    private IEnumerable<RawEdge> FilterByKind(IEnumerable<RawEdge> edges, string kind, GraphDirection direction)
    {
        var kindLower = kind.ToLowerInvariant();
        var wantCSharp = kindLower == "csharp" || kindLower == "cs";
        var wantXml = kindLower == "xml" || kindLower == "def";

        if (!wantCSharp && !wantXml)
        {
            return edges;
        }

        return edges.Where(e =>
        {
            var resultSymbol = direction == GraphDirection.Uses
                ? _indexToSymbol[e.TargetIndex]
                : _indexToSymbol[e.SourceIndex];
            
            var resultIsCSharp = IsCSharpNode(resultSymbol);
            var resultIsXml = IsXmlNode(resultSymbol);
            
            if (wantCSharp)
            {
                return resultIsCSharp;
            }
            else 
            {
                return resultIsXml;
            }
        });
    }

    private static bool IsCSharpNode(string symbolId)
        => !symbolId.StartsWith("xml:", StringComparison.OrdinalIgnoreCase);

    private static bool IsXmlNode(string symbolId)
        => symbolId.StartsWith("xml:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fuzzy symbol resolution using string similarity.
    /// Handles cases where users provide partial or slightly incorrect symbol names.
    /// </summary>
    private string? ResolveSymbolFuzzy(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var queryLower = query.ToLowerInvariant();
        var queryParts = query.Split(new[] { ':', '.', ' ', '<', '-' }, StringSplitOptions.RemoveEmptyEntries);
        
        string? bestMatch = null;
        double bestScore = 0.0;

        // First pass: try prefix matching for xml symbols
        if (query.StartsWith("xml:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var symbol in _symbolToIndex.Keys)
            {
                if (symbol.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                {
                    return symbol; // Exact prefix match
                }
            }
        }

        // Second pass: fuzzy matching using substring matching and JaroWinkler
        foreach (var symbol in _symbolToIndex.Keys)
        {
            var symbolLower = symbol.ToLowerInvariant();
            
            // Check if all query parts appear in the symbol (case-insensitive)
            var allPartsMatch = queryParts.All(part => 
                symbolLower.Contains(part.ToLowerInvariant()));
            
            if (allPartsMatch)
            {
                // Calculate score based on multiple factors
                double score = 0.0;
                
                // Factor 1: JaroWinkler similarity
                var similarity = _jaroWinkler.Similarity(queryLower, symbolLower);
                score += similarity * 0.3;
                
                // Factor 2: Substring coverage (how much of the symbol is covered by query parts)
                var totalPartLength = queryParts.Sum(p => p.Length);
                var coverageRatio = (double)totalPartLength / symbol.Length;
                score += Math.Min(coverageRatio, 1.0) * 0.3;
                
                // Factor 3: Exact part match bonus (query parts match symbol parts exactly)
                var symbolParts = symbol.Split(new[] { ':', '.', ' ', '<', '-' }, StringSplitOptions.RemoveEmptyEntries);
                var exactPartMatches = queryParts.Count(qp => 
                    symbolParts.Any(sp => sp.Equals(qp, StringComparison.OrdinalIgnoreCase)));
                var exactMatchRatio = (double)exactPartMatches / queryParts.Length;
                score += exactMatchRatio * 0.4;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = symbol;
                }
            }
        }

        // Return if we have any reasonable match (all parts matched)
        return bestMatch;
    }

    private static bool IsCSharpEdge(EdgeKind kind) => kind switch
    {
        EdgeKind.Inherits => true,
        EdgeKind.Calls => true,
        EdgeKind.References => true,
        EdgeKind.XmlBindsClass => true,
        EdgeKind.XmlUsesComp => true,
        _ => false
    };

    private static bool IsXmlEdge(EdgeKind kind) => kind switch
    {
        EdgeKind.XmlInherits => true,
        EdgeKind.XmlReferences => true,
        EdgeKind.CSharpUsedByDef => true,
        _ => false
    };

    //多查一次：CSharpUsedByDef是反向边不能出现在Uses查询中。测试的时候发现这个问题，多做一步
    private static bool IsEdgeValidForDirection(EdgeKind kind, GraphDirection direction)
    {
        if (direction == GraphDirection.Uses)
        {
            return kind != EdgeKind.CSharpUsedByDef;
        }
        return true;
    }

    private static EdgeKind DecodeKind(byte code) => code switch
    {
        1 => EdgeKind.Calls,
        2 => EdgeKind.References,
        3 => EdgeKind.Inherits,
        4 => EdgeKind.XmlReferences,
        10 => EdgeKind.XmlInherits,
        20 => EdgeKind.XmlBindsClass,
        21 => EdgeKind.XmlUsesComp,
        30 => EdgeKind.CSharpUsedByDef,
        _ => EdgeKind.References 
    };

    private static (Dictionary<int, string>, Dictionary<string, int>) LoadNodes(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Graph nodes file not found: {path}");
        }

        var indexToSymbol = new Dictionary<int, string>();
        var symbolToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2)
            {
                continue;
            }

            if (!int.TryParse(parts[0], out var index))
            {
                continue;
            }

            var symbol = parts[1];
            indexToSymbol[index] = symbol;
            symbolToIndex[symbol] = index;
        }

        return (indexToSymbol, symbolToIndex);
    }

    private static Dictionary<string, double> LoadPageRank(string path)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return scores;
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length != 2 || !double.TryParse(parts[1], out var score))
            {
                continue;
            }

            scores[parts[0]] = score;
        }

        return scores;
    }

    public static (int[], int[], byte[]) LoadBinary(string path, string expectedHeader)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        //读标题
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != expectedHeader)
        {
            throw new InvalidDataException($"Invalid magic header in {path}: expected {expectedHeader}, got {magic}");
        }

        var version = reader.ReadInt32();
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported format version: {version}");
        }

        var nodeCount = reader.ReadInt32();
        var edgeCount = reader.ReadInt32();

        //读指针
        var pointers = new int[nodeCount + 1];
        for (var i = 0; i <= nodeCount; i++)
        {
            pointers[i] = reader.ReadInt32();
        }

        //读索引
        var indices = new int[edgeCount];
        for (var i = 0; i < edgeCount; i++)
        {
            indices[i] = reader.ReadInt32();
        }

        //读类
        var kindsLength = reader.ReadInt32();
        if (kindsLength != edgeCount)
        {
            throw new InvalidDataException($"Kinds array length mismatch: expected {edgeCount}, got {kindsLength}");
        }
        var kinds = reader.ReadBytes(edgeCount);

        return (pointers, indices, kinds);
    }

    public void Dispose()
    {
        //好像没啥需要释放的，索性全删了
    }

    /// <summary>
    /// Get the integer node ID for a symbol ID.
    /// Returns null if the symbol is not found in the graph.
    /// </summary>
    public int? GetNodeId(string symbolId)
    {
        if (_symbolToIndex.TryGetValue(symbolId, out var nodeId))
        {
            return nodeId;
        }
        return null;
    }

    /// <summary>
    /// Get the symbol ID for an integer node ID.
    /// Returns null if the node ID is not found in the graph.
    /// </summary>
    public string? GetSymbolId(int nodeId)
    {
        if (_indexToSymbol.TryGetValue(nodeId, out var symbolId))
        {
            return symbolId;
        }
        return null;
    }

    /// <summary>
    /// Resolve a symbol reference that may be either a symbol ID or an integer node ID (prefixed with #).
    /// Returns the resolved symbol ID, or null if not found.
    /// </summary>
    public string? ResolveSymbolReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        // Check if it's an integer ID (prefixed with #)
        if (reference.StartsWith('#') && int.TryParse(reference.AsSpan(1), out var nodeId))
        {
            return GetSymbolId(nodeId);
        }

        // Try exact match
        if (_symbolToIndex.ContainsKey(reference))
        {
            return reference;
        }

        // Try fuzzy resolution
        return ResolveSymbolFuzzy(reference);
    }

    private readonly struct RawEdge
    {
        public int SourceIndex { get; init; }
        public int TargetIndex { get; init; }
        public byte Kind { get; init; }
    }
}
