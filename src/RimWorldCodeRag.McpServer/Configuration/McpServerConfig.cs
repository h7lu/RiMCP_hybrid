namespace RimWorldCodeRag.McpServer.Configuration;


// MCP 服务器配置
public sealed class McpServerConfig
{

    //索引根目录（lucene、vec、graph.db 等子目录）
    public required string IndexRoot { get; set; }

    // 数据根目录（RimWorld 源代码和 Defs 所在目录）
    public string? DataRoot { get; set; }

    // 嵌入服务器 URL
    public string? EmbeddingServerUrl { get; set; }

    // 嵌入模型Api密钥
    public string? ApiKey { get; set; }

    //嵌入模型名
    public string? ModelName { get; set; }

    // 最大并发请求数，默认 5
    public int MaxConcurrentRequests { get; set; } = 5;

    // 默认启用缓存
    public bool EnableCache { get; set; } = true;

    // 验证配置有效
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(IndexRoot))
        {
            throw new InvalidOperationException("IndexRoot is required");
        }

        if (!Directory.Exists(IndexRoot))
        {
            throw new DirectoryNotFoundException($"Index root directory not found: {IndexRoot}");
        }

        var lucenePath = Path.Combine(IndexRoot, "lucene");
        if (!Directory.Exists(lucenePath))
        {
            throw new DirectoryNotFoundException($"Lucene index not found at: {lucenePath}");
        }

        var vecPath = Path.Combine(IndexRoot, "vec");
        if (!Directory.Exists(vecPath))
        {
            throw new DirectoryNotFoundException($"Vector index not found at: {vecPath}");
        }

        var graphCsrPath = Path.Combine(IndexRoot, "graph.csr.bin");
        var graphCscPath = Path.Combine(IndexRoot, "graph.csc.bin");
        var graphNodesPath = Path.Combine(IndexRoot, "graph.nodes.tsv");
        
        if (!File.Exists(graphCsrPath) || !File.Exists(graphCscPath) || !File.Exists(graphNodesPath))
        {
            throw new FileNotFoundException($"Graph files not found in: {IndexRoot}\n" +
                $"Expected: graph.csr.bin, graph.csc.bin, graph.nodes.tsv\n" +
                $"Hint: Make sure all index files (lucene, vec, graph.csr.bin, graph.csc.bin, graph.nodes.tsv) exist.");
        }
    }
}
