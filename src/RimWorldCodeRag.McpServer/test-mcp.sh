#!/bin/bash
# test-mcp.sh - Basic MCP server test script for Linux/macOS

set -e

# Get script directory and navigate to MCP server
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MCP_DIR="$REPO_ROOT/src/RimWorldCodeRag.McpServer"

cd "$MCP_DIR"

echo "Testing MCP server..."
echo "Working directory: $(pwd)"
echo ""

# Check if dotnet is available
if ! command -v dotnet &> /dev/null; then
    echo "Error: dotnet CLI is not installed or not in PATH"
    exit 1
fi

# Check if the project file exists
if [ ! -f "RimWorldCodeRag.McpServer.csproj" ]; then
    echo "Error: RimWorldCodeRag.McpServer.csproj not found"
    exit 1
fi

# Build the project
echo "Building MCP server..."
dotnet build --verbosity quiet
if [ $? -ne 0 ]; then
    echo "Error: Build failed"
    exit 1
fi

echo "Build successful!"
echo ""
echo "To test the MCP server:"
echo "1. Set environment variables:"
echo "   export RIMWORLD_INDEX_ROOT=\"$REPO_ROOT/index\""
echo "   export EMBEDDING_SERVER_URL=\"http://127.0.0.1:5000\""
echo ""
echo "2. Start the server:"
echo "   dotnet run"
echo ""
echo "3. In another terminal, test with:"
echo "   echo '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}' | dotnet run"