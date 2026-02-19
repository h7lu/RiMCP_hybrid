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
        "Find what uses a symbol - shows reverse dependencies and calling relationships. " +
        "Excellent for understanding impact and usage patterns by tracing who calls or references the symbol. " +
        "Accepts symbol ID (e.g., 'Verse.Pawn') or node ID from search results (e.g., '#42998'). " +
        "IMPORTANT: Use the 'kind' parameter to filter results - set kind='xml' to find which XML Defs use this symbol, " +
        "set kind='csharp' to find which C# code uses this symbol. This reduces noise significantly. " +
        "Use 'page' parameter to navigate through large result sets. " +
        "Use get_item tool afterwards to examine the full source code of any interesting callers found.";

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
                    description = "Symbol ID or node ID to analyze. Examples: 'Verse.Pawn', 'Verse.Thing.Tick', 'xml:Steel', or '#42998' (node ID from search results)",
                    pattern = "^(#[0-9]+|[A-Za-z0-9_\\.]+|xml:[A-Za-z0-9_]+.*)$"
                },
                kind = new
                {
                    type = "string",
                    @enum = new[] { "csharp", "xml", "all" },
                    @default = "all",
                    description = "RECOMMENDED: Filter source type. Use 'csharp' to see only C# code that uses this symbol, 'xml' to see only XML Defs that use this symbol. Use 'all' only when you need both."
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

        // Resolve symbol reference (handles #nodeId format)
        var resolvedSymbol = _querier.Value.ResolveSymbolReference(symbol);
        if (resolvedSymbol == null)
        {
            throw new ArgumentException($"无法解析符号引用: '{symbol}'。提示：使用 rough_search 工具查找可用的符号。");
        }

        //初始化一下图检索
        var config = new Common.GraphQueryConfig
        {
            SymbolId = resolvedSymbol,
            Direction = Common.GraphDirection.UsedBy,
            Kind = kind == "all" ? null : kind,
            MaxDepth = depth,
            Page = page,
            PageSize = maxResults
        };

        var queryResult = await Task.Run(() => _querier.Value.Query(config));

        var totalCount = queryResult.TotalCount;
        var totalPages = (int)Math.Ceiling(totalCount / (double)maxResults);
        
        // 分页计算
        var pagedEdges = queryResult.Results;
        var skip = (page - 1) * maxResults;

        // 转换为MCP响应格式
        var response = new
        {
            targetSymbol = resolvedSymbol,
            targetNodeId = _querier.Value.GetNodeId(resolvedSymbol),
            edges = pagedEdges.Select(e => new
            {
                sourceSymbol = e.SymbolId,
                sourceNodeId = _querier.Value.GetNodeId(e.SymbolId),
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
