namespace RimWorldCodeRag.McpServer;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldCodeRag.McpServer.Configuration;
using RimWorldCodeRag.McpServer.Infrastructure;
using RimWorldCodeRag.McpServer.Tools;


//MCP 服务器主类
public sealed class McpServer : IDisposable
{
    private readonly StdioTransport _transport;
    private readonly Dictionary<string, ITool> _tools;
    private readonly McpServerConfig _config;
    private bool _initialized;
    private bool _disposed;

    public McpServer(McpServerConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();

        _transport = new StdioTransport();
        _tools = new Dictionary<string, ITool>();

        LogToStderr($"MCP Server initialized with index root: {config.IndexRoot}");
        LogToStderr($"Embedding server URL: {config.EmbeddingServerUrl ?? "(not configured)"}");
    }

    //注册工具
    public void RegisterTool(ITool tool)
    {
        if (tool == null)
        {
            throw new ArgumentNullException(nameof(tool));
        }

        _tools[tool.Name] = tool;
        LogToStderr($"Registered tool: {tool.Name}");
    }

    //运行服务器主循环
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        LogToStderr("MCP Server starting...");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var request = await _transport.ReadRequestAsync();
                if (request == null)
                {
                    LogToStderr("Received EOF, shutting down...");
                    break;
                }

                var isNotification = !request.Id.HasValue;
                LogToStderr($"Received {(isNotification ? "notification" : "request")}: {request.Method}");

                var response = await HandleRequestAsync(request, cancellationToken);
                
                if (!isNotification && response != null)
                {
                    await _transport.WriteResponseAsync(response);
                }
            }
            catch (OperationCanceledException)
            {
                LogToStderr("Operation cancelled, shutting down...");
                break;
            }
            catch (Exception ex)
            {
                LogToStderr($"Error in main loop: {ex.Message}");
                LogToStderr($"Stack trace: {ex.StackTrace}");
            }
        }

        LogToStderr("MCP Server stopped.");
    }

    private async Task<JsonRpcResponse?> HandleRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "initialized" or "notifications/initialized" => HandleInitialized(request),
                "tools/list" => HandleToolsList(request),
                "tools/call" => await HandleToolsCallAsync(request, cancellationToken),
                "shutdown" => HandleShutdown(request),
                _ => JsonRpcResponse.CreateError(request.Id, -32601, $"Method not found: {request.Method}")
            };
        }
        catch (Exception ex)
        {
            LogToStderr($"Exception in {request.Method}: {ex}");
            return JsonRpcResponse.CreateError(request.Id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        LogToStderr("Handling initialize request");

        var result = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = "rimworld-code-rag",
                version = "1.0.0"
            }
        };

        return JsonRpcResponse.Success(request.Id, result);
    }

    private JsonRpcResponse? HandleInitialized(JsonRpcRequest request)
    {
        _initialized = true;
        LogToStderr("Server initialized (client ready)");
        return null; 
    }

    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        LogToStderr($"Listing {_tools.Count} tools");

        var tools = _tools.Values.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.GetInputSchema()
        }).ToArray();

        return JsonRpcResponse.Success(request.Id, new { tools });
    }

    private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            return JsonRpcResponse.CreateError(request.Id, -32002, "Server not initialized");
        }

        if (request.Params == null)
        {
            return JsonRpcResponse.CreateError(request.Id, -32602, "Missing params");
        }

        ToolCallParams? toolCall;
        try
        {
            toolCall = JsonSerializer.Deserialize<ToolCallParams>(
                request.Params.Value.GetRawText()
            );
        }
        catch (JsonException ex)
        {
            return JsonRpcResponse.CreateError(request.Id, -32602, $"Invalid params: {ex.Message}");
        }

        if (toolCall?.Name == null)
        {
            return JsonRpcResponse.CreateError(request.Id, -32602, "Missing tool name");
        }

        if (!_tools.TryGetValue(toolCall.Name, out var tool))
        {
            return JsonRpcResponse.CreateError(request.Id, -32602, $"Unknown tool: {toolCall.Name}");
        }

        try
        {
            LogToStderr($"Executing tool: {toolCall.Name}");

            var result = await tool.ExecuteAsync(toolCall.Arguments);

            var content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(result, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    })
                }
            };

            LogToStderr($"Tool {toolCall.Name} executed successfully");

            return JsonRpcResponse.Success(request.Id, new { content });
        }
        catch (ArgumentException ex)
        {
            LogToStderr($"Argument error in {toolCall.Name}: {ex.Message}");
            return JsonRpcResponse.CreateError(request.Id, -32602, $"Invalid arguments: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogToStderr($"Error executing tool {toolCall.Name}: {ex.Message}");
            return JsonRpcResponse.CreateError(request.Id, -32603, $"Tool execution failed: {ex.Message}");
        }
    }

    private JsonRpcResponse HandleShutdown(JsonRpcRequest request)
    {
        LogToStderr("Shutdown requested");
        return JsonRpcResponse.Success(request.Id, new { });
    }

    private static void LogToStderr(string message)
    {
        Console.Error.WriteLine($"[MCP] {DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        LogToStderr("Disposing MCP Server...");

        foreach (var tool in _tools.Values)
        {
            if (tool is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    LogToStderr($"Error disposing tool {tool.Name}: {ex.Message}");
                }
            }
        }

        _disposed = true;
        LogToStderr("MCP Server disposed");
    }
}
