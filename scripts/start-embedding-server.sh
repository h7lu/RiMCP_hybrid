#!/bin/bash
# start-embedding-server.sh - Start the persistent embedding server
# Cross-platform shell script for Linux/macOS/Windows (via Git Bash/MSYS2)

set -e

# Default parameters
PORT=5000
SERVER_HOST="127.0.0.1"
MODEL=""

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --port)
            PORT="$2"
            shift 2
            ;;
        --host)
            SERVER_HOST="$2"
            shift 2
            ;;
        --model)
            MODEL="$2"
            shift 2
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: $0 [--port PORT] [--host HOST] [--model MODEL_DIR]"
            exit 1
            ;;
    esac
done

# Detect OS
detect_os() {
    case "$(uname -s)" in
        Linux*)     echo "linux" ;;
        Darwin*)    echo "macos" ;;
        CYGWIN*|MINGW*|MSYS*) echo "windows" ;;
        *)          echo "unknown" ;;
    esac
}

OS=$(detect_os)

# Get repository root
get_repo_root() {
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    echo "$(cd "$SCRIPT_DIR/.." && pwd)"
}

# Set default model path if not provided
if [ -z "$MODEL" ]; then
    REPO_ROOT=$(get_repo_root)
    MODEL="$REPO_ROOT/src/RimWorldCodeRag/models/e5-base-v2"
fi

# Set virtual environment python path
REPO_ROOT=$(get_repo_root)
case $OS in
    windows)
        VENV_PYTHON="$REPO_ROOT/src/RimWorldCodeRag/.venv/Scripts/python.exe"
        ;;
    *)
        VENV_PYTHON="$REPO_ROOT/src/RimWorldCodeRag/.venv/bin/python"
        ;;
esac

SERVER_SCRIPT="$REPO_ROOT/src/RimWorldCodeRag/python/embedding_server.py"

# Check prerequisites
if [ ! -f "$VENV_PYTHON" ]; then
    echo "Error: Virtual environment not found at $VENV_PYTHON."
    echo "Run setup-embedding-env.sh first."
    exit 1
fi

if [ ! -f "$SERVER_SCRIPT" ]; then
    echo "Error: Server script not found at $SERVER_SCRIPT"
    exit 1
fi

if [ ! -d "$MODEL" ]; then
    echo "Error: Model directory not found at $MODEL"
    exit 1
fi

# Start the server
echo -e "\033[0;36mStarting embedding server...\033[0m"
echo -e "\033[0;90m  Host: $SERVER_HOST\033[0m"
echo -e "\033[0;90m  Port: $PORT\033[0m"
echo -e "\033[0;90m  Model: $MODEL\033[0m"
echo ""
echo -e "\033[0;33mServer will load model on startup (may take 10-20 seconds)...\033[0m"
echo -e "\033[0;33mPress Ctrl+C to stop the server\033[0m"
echo ""

# Run the server
"$VENV_PYTHON" "$SERVER_SCRIPT" --model "$MODEL" --host "$SERVER_HOST" --port "$PORT"