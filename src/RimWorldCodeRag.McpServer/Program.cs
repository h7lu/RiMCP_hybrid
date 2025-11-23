using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RimWorldCodeRag.McpServer;
using RimWorldCodeRag.McpServer.Configuration;
using RimWorldCodeRag.McpServer.Tools;

// 读取配置（优先使用环境变量，其次使用 appsettings.json）
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// 解析配置
var indexRootFromConfig = configuration["RIMWORLD_INDEX_ROOT"] ?? configuration["McpServer:IndexRoot"];

string indexRoot;
if (!string.IsNullOrWhiteSpace(indexRootFromConfig))
{
    indexRoot = indexRootFromConfig;
}
else
{
    // Try to discover the repository root by searching upward for a solution file or .git folder.
    // If found, prefer src/RimWorldCodeRag/index under the repo root. Otherwise fall back to the
    // original relative path from the current working directory.
    var cwd = Directory.GetCurrentDirectory();
    var dirInfo = new DirectoryInfo(cwd);
    DirectoryInfo? repoRoot = null;
    while (dirInfo != null)
    {
        try
        {
            if (dirInfo.GetFiles("*.sln").Any() || Directory.Exists(Path.Combine(dirInfo.FullName, ".git")))
            {
                repoRoot = dirInfo;
                break;
            }
        }
        catch
        {
            // ignore IO errors and continue upwards
        }

        dirInfo = dirInfo.Parent;
    }

    if (repoRoot != null)
    {
        var candidate = Path.Combine(repoRoot.FullName, "src", "RimWorldCodeRag", "index");
        if (Directory.Exists(candidate))
        {
            indexRoot = candidate;
        }
        else
        {
            indexRoot = Path.Combine(cwd, "..", "..", "index");
        }
    }
    else
    {
        indexRoot = Path.Combine(cwd, "..", "..", "index");
    }
}

var config = new McpServerConfig
{
    IndexRoot = Path.GetFullPath(indexRoot),
    DataRoot = configuration["RIMWORLD_DATA_ROOT"]
               ?? configuration["McpServer:DataRoot"],
    EmbeddingServerUrl = configuration["EMBEDDING_SERVER_URL"]
                         ?? configuration["McpServer:EmbeddingServerUrl"],
    ApiKey = configuration["APIKEY"] ?? configuration["McpServer:ApiKey"],
    ModelName = configuration["EMBEDDING_MODELNAME"] ?? configuration["McpServer:ModelName"]
};

// 记录配置信息
Console.Error.WriteLine("=== RimWorld Code RAG MCP Server ===");
Console.Error.WriteLine($"Index Root: {config.IndexRoot}");
Console.Error.WriteLine($"Data Root: {config.DataRoot ?? "(not configured)"}");
Console.Error.WriteLine($"Embedding Server: {config.EmbeddingServerUrl ?? "(not configured)"}");
Console.Error.WriteLine("=====================================");

// 创建取消令牌（支持 Ctrl+C 优雅关闭）
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.Error.WriteLine("Shutdown signal received...");
};

// 创建并运行服务器
try
{
    using var server = new McpServer(config);

    // 注册所有 MCP 工具
    Console.Error.WriteLine("Registering tools...");
    
    server.RegisterTool(new RoughSearchTool(
        config.IndexRoot,
        config.EmbeddingServerUrl,
        config.ApiKey,
        config.ModelName
    ));
    
    server.RegisterTool(new GetUsesTool(
        config.IndexRoot
    ));
    
    server.RegisterTool(new GetUsedByTool(
        config.IndexRoot
    ));
    
    server.RegisterTool(new GetItemTool(
        config.IndexRoot
    ));

    Console.Error.WriteLine("All tools registered successfully.");
    Console.Error.WriteLine("=====================================");

    await server.RunAsync(cts.Token);
}
catch (DirectoryNotFoundException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine("Hint: Make sure the index has been built. Run 'dotnet run --project ../RimWorldCodeRag -- index --root <path>' first.");
    return 1;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine("Hint: Make sure all index files (lucene, vec, graph.db) exist.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
    return 1;
}

return 0;
