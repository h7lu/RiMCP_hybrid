namespace RimWorldCodeRag.McpServer.Tools;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldCodeRag.Retrieval;


// get_used_by MCP工具暴露接口
public sealed class GetUsedByTool : ITool, IDisposable
{
    private readonly Lazy<GraphQuerier> _querier;
    private readonly string _indexRoot;
    private bool _disposed;

    public string Name => "get_used_by";

    public string Description =>
        "Find what uses a symbol - shows reverse dependencies and calling relationships. Excellent for understanding impact and usage patterns by tracing who calls or references the symbol. Use get_item tool afterwards to examine the full source code of any interesting callers found.";

    public GetUsedByTool(string indexRoot)
    {
        _indexRoot = indexRoot;
        _querier = new Lazy<GraphQuerier>(() =>
        {
            Console.Error.WriteLine("[GetUsedByTool] Loading graph data...");
            var graphBasePath = Path.Combine(_indexRoot, "graph");
            var querier = new GraphQuerier(graphBasePath);
            Console.Error.WriteLine("[GetUsedByTool] Graph loaded successfully.");
            return querier;
        });
    }

    public JsonElement GetInputSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                symbol = new
                {
                    type = "string",
                    description = "Symbol ID to analyze. Examples: 'RimWorld.Pawn', 'Verse.Thing.Tick', 'RimWorld.JobDriver_Mine', 'xml:Steel'",
                    pattern = "^([A-Za-z0-9_\\.]+|xml:[A-Za-z0-9_]+)$"
                },
                kind = new
                {
                    type = "string",
                    @enum = new[] { "csharp", "xml", "all" },
                    @default = "all",
                    description = "Filter by source type: 'csharp' for C# symbols only, 'xml' for XML definitions only, 'all' for everything"
                },
                depth = new
                {
                    type = "integer",
                    @default = 1,
                    minimum = 1,
                    maximum = 2,
                    description = "Traversal depth. 1=recommended (direct dependents only), 2=includes indirect dependents"
                },
                max_results = new
                {
                    type = "integer",
                    @default = 50,
                    minimum = 1,
                    maximum = 500,
                    description = "Maximum results per page. Default 50. Lower values reduce token usage."
                },
                page = new
                {
                    type = "integer",
                    @default = 1,
                    minimum = 1,
                    description = "Page number (starts from 1). Results are sorted by distance, edge type, then name for consistent pagination."
                }
            },
            required = new[] { "symbol" }
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    public async Task<object> ExecuteAsync(JsonElement arguments)
    {
        // 解析参数
        if (!arguments.TryGetProperty("symbol", out var symbolElement))
        {
            throw new ArgumentException("参数 'symbol' 是必需的");
        }

        var symbol = symbolElement.GetString();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("参数 'symbol' 不能为空");
        }

        var kind = arguments.TryGetProperty("kind", out var kindElem)
            ? kindElem.GetString()
            : "all";

        var depth = arguments.TryGetProperty("depth", out var depthElem)
            ? depthElem.GetInt32()
            : 1;

        var maxResults = arguments.TryGetProperty("max_results", out var maxElem)
            ? maxElem.GetInt32()
            : 50;

        var page = arguments.TryGetProperty("page", out var pageElem)
            ? pageElem.GetInt32()
            : 1;

        // 验证参数
        if (depth < 1 || depth > 2)
        {
            throw new ArgumentException("depth 必须为 1 或 2");
        }

        if (maxResults < 1 || maxResults > 500)
        {
            throw new ArgumentException("max_results 必须在 1 到 500 之间");
        }

        if (page < 1)
        {
            throw new ArgumentException("page 必须大于等于 1");
        }

        //初始化一下图检索
        var config = new Common.GraphQueryConfig
        {
            SymbolId = symbol,
            Direction = Common.GraphDirection.UsedBy,
            Kind = kind == "all" ? null : kind,
            MaxDepth = depth
        };

        var edges = await Task.Run(() => _querier.Value.Query(config));

       //这种排序可以保证每次同样查询返回的结果都是一致的，不然分页没法做
        var sortedEdges = edges
            .OrderBy(e => e.Distance)
            .ThenBy(e => e.EdgeKind)
            .ThenBy(e => e.SymbolId, StringComparer.Ordinal)
            .ToList();

        var totalCount = sortedEdges.Count;
        var totalPages = (int)Math.Ceiling(totalCount / (double)maxResults);
        
        // 分页计算
        var skip = (page - 1) * maxResults;
        var pagedEdges = sortedEdges
            .Skip(skip)
            .Take(maxResults)
            .ToList();

        // 转换为MCP响应格式
        var response = new
        {
            targetSymbol = symbol,
            edges = pagedEdges.Select(e => new
            {
                sourceSymbol = e.SymbolId,
                edgeKind = e.EdgeKind.ToString(),
                edgeLabel = GetEdgeLabel(e.EdgeKind),
                distance = e.Distance
            }).ToArray(),
            
            //分页信息
            pagination = new
            {
                page = page,
                pageSize = maxResults,
                totalResults = totalCount,
                totalPages = totalPages,
                hasNextPage = page < totalPages,
                hasPreviousPage = page > 1,
                resultRange = pagedEdges.Count > 0 
                    ? $"{skip + 1}-{skip + pagedEdges.Count}" 
                    : "0-0"
            },
            
            //简单给个prompt
            message = pagedEdges.Count == 0 && page > totalPages
                ? $"页码超出范围。总共 {totalPages} 页（{totalCount} 条结果）。"
                : totalPages > 1
                    ? $"显示第 {page}/{totalPages} 页。使用 'page' 参数浏览其他结果。"
                    : null
        };

        return response;
    }

    private static string GetEdgeLabel(Common.EdgeKind kind)
    {
        return kind switch
        {
            Common.EdgeKind.Inherits => "被继承",
            Common.EdgeKind.Calls => "被调用",
            Common.EdgeKind.References => "被引用",
            Common.EdgeKind.XmlInherits => "被 XML 继承",
            Common.EdgeKind.XmlReferences => "被 XML 引用",
            Common.EdgeKind.XmlBindsClass => "被 Def 绑定",
            Common.EdgeKind.XmlUsesComp => "被 Def 使用",
            Common.EdgeKind.CSharpUsedByDef => "被 Def 使用",
            _ => kind.ToString()
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_querier.IsValueCreated)
        {
            (_querier.Value as IDisposable)?.Dispose();
        }
        _disposed = true;
    }
}
