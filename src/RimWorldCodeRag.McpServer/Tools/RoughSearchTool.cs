namespace RimWorldCodeRag.McpServer.Tools;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldCodeRag.Retrieval;


//rough_search MCP工具暴露接口
public sealed class RoughSearchTool : ITool, IDisposable
{
    private readonly Lazy<RoughSearcher> _searcher;
    private readonly string _indexRoot;
    private readonly string? _embeddingServerUrl;
    private readonly RoughSearchConfig _defaultConfig;
    private bool _disposed;

    public string Name => "rough_search";

    public string Description =>
        "Search for RimWorld code symbols and XML definitions using natural language queries. Returns a list of matching item names with metadata. Use get_item tool afterwards to retrieve full source code for any interesting results. If search doesn't return relevant results, try simplifying your query to focus on essential keywords only.";

    public RoughSearchTool(string indexRoot, string? embeddingServerUrl = null)
    {
        _indexRoot = indexRoot;
        _embeddingServerUrl = embeddingServerUrl;
        
        _defaultConfig = new RoughSearchConfig
        {
            LuceneIndexPath = Path.Combine(_indexRoot, "lucene"),
            VectorIndexPath = Path.Combine(_indexRoot, "vec"),
            EmbeddingServerUrl = _embeddingServerUrl,
            MaxResults = 20,
            UseSemanticScoringOnly = true
        };

        _searcher = new Lazy<RoughSearcher>(() =>
        {
            Console.Error.WriteLine("[RoughSearchTool] Initializing searcher (this may take a few seconds)...");
            var searcher = new RoughSearcher(_defaultConfig);
            Console.Error.WriteLine("[RoughSearchTool] Searcher initialized successfully.");
            return searcher;
        });
    }

    public JsonElement GetInputSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "Natural language search query. Examples: 'pawn hunger system', 'crafting buildings', 'weapon damage calculation', 'colonist needs'",
                    minLength = 1,
                    maxLength = 500
                },
                kind = new
                {
                    type = "string",
                    @enum = new[] { "csharp", "cs", "def", "xml" },
                    description = "Optional filter: 'csharp' or 'cs' for C# code only, 'def' or 'xml' for XML definitions only. Omit to search everything."
                },
                max_results = new
                {
                    type = "integer",
                    @default = 20,
                    minimum = 1,
                    maximum = 100,
                    description = "Maximum number of results to return. Recommended range: 10-30."
                }
            },
            required = new[] { "query" }
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    public async Task<object> ExecuteAsync(JsonElement arguments)
    {
        // 解析参数
        if (!arguments.TryGetProperty("query", out var queryElement))
        {
            throw new ArgumentException("参数 'query' 是必需的");
        }

        // Debug: 打印接收到的原始参数，便于排查客户端传参问题
        try
        {
            Console.Error.WriteLine($"[RoughSearchTool] Raw arguments: {arguments.GetRawText()}");
        }
        catch
        {
            // 忽略任何日志写入错误
        }

        var query = queryElement.GetString();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("参数 'query' 不能为空");
        }

        var kind = arguments.TryGetProperty("kind", out var kindElem)
            ? kindElem.GetString()
            : null;

        var maxResults = arguments.TryGetProperty("max_results", out var maxElem)
            ? maxElem.GetInt32()
            : _defaultConfig.MaxResults;

        // 验证参数
        if (maxResults < 1 || maxResults > 100)
        {
            throw new ArgumentException("max_results 必须在 1-100 之间");
        }

        var startTime = DateTime.Now;
        IReadOnlyList<RoughSearchResult> results;
        if (string.Equals(kind, _defaultConfig.Kind, StringComparison.OrdinalIgnoreCase) && maxResults == _defaultConfig.MaxResults)
        {
            results = await _searcher.Value.SearchAsync(query);
        }
        else
        {
            var perReq = new RoughSearchConfig
            {
                LuceneIndexPath = _defaultConfig.LuceneIndexPath,
                VectorIndexPath = _defaultConfig.VectorIndexPath,
                EmbeddingServerUrl = _defaultConfig.EmbeddingServerUrl,
                UseSemanticScoringOnly = _defaultConfig.UseSemanticScoringOnly,
                LexicalCandidates = _defaultConfig.LexicalCandidates,
                SemanticCandidates = _defaultConfig.SemanticCandidates,
                Kind = kind,
                MaxResults = maxResults
            };

            using var tmpSearcher = new RoughSearcher(perReq);
                results = await tmpSearcher.SearchAsync(query);//当时写这个rough search的时候没考虑那么多，把很多东西写死了，导致现在得用这种粗暴的方式实现参数调整。真是狗操
        }

        var elapsed = DateTime.Now - startTime;

        // 转换为 MCP响应格式
        var response = new
        {
                results = results.Select(r => new
            {
                symbolId = r.SymbolId,
                kind = r.Language.ToString().ToLowerInvariant(),
                symbolKind = r.SymbolKind.ToString(),
                path = r.Path,
                @namespace = r.Namespace,
                containingType = r.ContainingType,
                signature = r.Signature,
                title = ((r.Signature ?? r.Preview ?? r.SymbolId).Split('\n')[0] ?? string.Empty).Trim(),
                score = Math.Round(r.Score, 4),
                spanStart = r.SpanStart,
                spanEnd = r.SpanEnd,
                preview = r.Language == RimWorldCodeRag.Common.LanguageKind.CSharp ? string.Empty : r.Preview
            }).ToArray(),
            totalFound = results.Count,
            queryTime = $"{elapsed.TotalSeconds:F2}s"
        };

        return response;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_searcher.IsValueCreated)
        {
            (_searcher.Value as IDisposable)?.Dispose();
        }
        _disposed = true;
    }
}
