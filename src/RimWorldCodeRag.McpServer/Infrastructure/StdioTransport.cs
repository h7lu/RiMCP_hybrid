namespace RimWorldCodeRag.McpServer.Infrastructure;

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;


// 基于 stdin/stdout 的 jsonrpc传输层
public sealed class StdioTransport
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly JsonSerializerOptions _options;

    public StdioTransport()
        : this(Console.In, Console.Out)
    {
    }

    public StdioTransport(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }


    //从 stdin 读取一个json_rpc请求
    public async Task<JsonRpcRequest?> ReadRequestAsync()
    {
        var line = await _input.ReadLineAsync();
        if (line == null)
        {
            return null; // EOF
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonRpcRequest>(line, _options);
        }
        catch (JsonException ex)
        {
            LogToStderr($"Failed to parse JSON-RPC request: {ex.Message}");
            LogToStderr($"Raw input: {line}");
            throw new InvalidOperationException($"Invalid JSON-RPC request: {ex.Message}", ex);
        }
    }

    // 向 stdout 写入一个jsonrpc响应
    public async Task WriteResponseAsync(JsonRpcResponse response)
    {
        try
        {
            var json = JsonSerializer.Serialize(response, _options);
            await _output.WriteLineAsync(json);
            await _output.FlushAsync();
        }
        catch (Exception ex)
        {
            LogToStderr($"Failed to write JSON-RPC response: {ex.Message}");
            throw;
        }
    }

    //向 stderr 写入日志，不然会毁掉运行在stdio上的请求
    private static void LogToStderr(string message)
    {
        Console.Error.WriteLine($"[StdioTransport] {DateTime.Now:HH:mm:ss} {message}");
    }
}
