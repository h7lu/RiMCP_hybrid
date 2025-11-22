# Using RimWorld Code RAG MCP Server with GitHub Copilot in VS Code

This guide explains how to use this MCP (Model Context Protocol) server with GitHub Copilot in Visual Studio Code.

## Prerequisites

1. **VS Code 1.102 or later** - MCP support is available starting from this version
2. **GitHub Copilot subscription** - You need an active Copilot subscription
3. **.NET 8.0 SDK** - Required to run the C# MCP server
4. **Built index files** - The server requires pre-built index files in the `index/` directory

## Setup Instructions

### 1. Enable MCP Support in VS Code

MCP is enabled by default in VS Code 1.102+. To verify or adjust settings:

1. Open VS Code Settings (`Ctrl+,` or `Cmd+,`)
2. Search for `chat.mcp.access`
3. Ensure it's set to `all` (default) or `registry`

### 2. MCP Server Configuration

The `mcp.json` file in the workspace root configures your MCP server:

```json
{
  "servers": {
    "rimWorldCodeRag": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "${workspaceFolder}/src/RimWorldCodeRag.McpServer/RimWorldCodeRag.McpServer.csproj"
      ],
      "env": {
        "RIMWORLD_INDEX_ROOT": "${workspaceFolder}/index",
        "RIMWORLD_DATA_ROOT": "${workspaceFolder}/RimWorldData"
      }
    }
  }
}
```

### 3. Start the MCP Server

**Option A: Automatic Start (Recommended)**

1. Enable auto-start in VS Code settings:
   - Search for `chat.mcp.autostart`
   - Enable the setting
2. Restart VS Code or reload the window

**Option B: Manual Start**

1. Open the Command Palette (`Ctrl+Shift+P` or `Cmd+Shift+P`)
2. Run: `MCP: List Servers`
3. Select `rimWorldCodeRag` and choose "Start Server"

**Option C: From Chat View**

1. Open the Chat view (`Ctrl+Alt+I`)
2. Click the refresh button in the MCP SERVERS section
3. Confirm trust when prompted

### 4. Trust the MCP Server

When you start the server for the first time, VS Code will ask you to confirm that you trust it:

1. Review the server configuration
2. Click "Trust" to allow the server to run

You can reset trust anytime using: `MCP: Reset Trust` from the Command Palette.

## Using the MCP Tools

Once the server is running, you can use its tools in GitHub Copilot Chat:

### Available Tools

1. **rough_search** - Semantic search through RimWorld code
2. **get_uses** - Find where a symbol is used
3. **get_used_by** - Find what uses a specific symbol
4. **get_item** - Get detailed information about a code item

### Using Tools in Chat

**Agent Mode (Automatic)**

Open the Chat view and ask questions naturally. Copilot will automatically invoke the appropriate tools:

```
List all classes related to pawn health
```

```
Show me how JobDriver is implemented
```

**Explicit Tool Reference**

You can explicitly reference tools by typing `#` followed by the tool name:

```
#rough_search find combat-related utilities
```

### Managing Tool Approvals

- Click the **Tools** button in the Chat view to toggle tools on/off
- Review and approve tool invocations when prompted
- Use `.github/copilot-instructions.md` for fine-tuned tool usage control

## Environment Variables

The server uses the following environment variables (configured in `mcp.json`):

- `RIMWORLD_INDEX_ROOT` - Path to the index directory (default: `${workspaceFolder}/index`)
- `RIMWORLD_DATA_ROOT` - Path to RimWorld data files (default: `${workspaceFolder}/RimWorldData`)
- `EMBEDDING_SERVER_URL` - (Optional) URL for embedding server

You can override these in `mcp.json` or set them as system environment variables.

## Troubleshooting

### Server Won't Start

1. **Check the Output Log**:

   - Command Palette → `MCP: List Servers`
   - Select `rimWorldCodeRag` → `Show Output`

2. **Verify Prerequisites**:

   ```powershell
   dotnet --version  # Should show 8.0 or later
   ```

3. **Check Index Files**:

   - Ensure the `index/` directory exists
   - Verify it contains `lucene/`, `vec/`, and `graph.db` files

4. **Build the Index** (if missing):
   ```powershell
   cd src/RimWorldCodeRag
   dotnet run -- index --root ../../RimWorldData
   ```

### Tools Not Appearing in Chat

1. **Clear Cached Tools**:

   - Command Palette → `MCP: Reset Cached Tools`

2. **Restart the Server**:

   - Command Palette → `MCP: List Servers`
   - Select `rimWorldCodeRag` → `Restart Server`

3. **Check Tool Limit**:
   - Maximum 128 tools per chat request
   - Deselect unused tools in the Chat view if needed

### "Cannot have more than 128 tools" Error

- Open the Tools picker in the Chat view
- Deselect some tools or whole servers you're not using
- Or enable virtual tools: Settings → `github.copilot.chat.virtualTools.threshold`

## Development Mode

For debugging the MCP server during development, add a `dev` section to `mcp.json`:

```json
{
  "servers": {
    "rimWorldCodeRag": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "${workspaceFolder}/src/RimWorldCodeRag.McpServer/RimWorldCodeRag.McpServer.csproj"
      ],
      "env": {
        "RIMWORLD_INDEX_ROOT": "${workspaceFolder}/index",
        "RIMWORLD_DATA_ROOT": "${workspaceFolder}/RimWorldData"
      },
      "dev": {
        "watch": "src/RimWorldCodeRag.McpServer/**/*.cs",
        "debug": true
      }
    }
  }
}
```

This will:

- Auto-restart the server when C# files change
- Enable debugger attachment for the MCP server process

## Additional Commands

- **List all MCP servers**: `MCP: List Servers`
- **Browse MCP resources**: `MCP: Browse Resources`
- **Open user config**: `MCP: Open User Configuration`
- **Open workspace config**: `MCP: Open Workspace Folder Configuration`
- **Reset trust**: `MCP: Reset Trust`

## Resources

- [VS Code MCP Documentation](https://code.visualstudio.com/docs/copilot/customization/mcp-servers)
- [Model Context Protocol](https://modelcontextprotocol.io/)
- [MCP Server Registry](https://github.com/mcp)

## Notes

- The MCP server runs locally on your machine
- All code analysis happens offline using your local index
- No RimWorld code or data is sent to external services (unless you configure an embedding server)
