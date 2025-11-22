namespace RimWorldCodeRag.McpServer.Tools;

using System.Text.Json;
using System.Threading.Tasks;


// MCP工具接口定义
public interface ITool
{

    string Name { get; }

    string Description { get; }

    JsonElement GetInputSchema();

    Task<object> ExecuteAsync(JsonElement arguments);
}
