#!/bin/bash
# setup-embedding-env.sh - Setup Python virtual environment and download models
# Cross-platform shell script for Linux/macOS/Windows (via Git Bash/MSYS2)

set -e

# Default parameters
PYTHON=""
MODEL_REPO="intfloat/e5-base-v2"

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --python)
            PYTHON="$2"
            shift 2
            ;;
        --model-repo)
            MODEL_REPO="$2"
            shift 2
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: $0 [--python PYTHON_EXE] [--model-repo MODEL_REPO]"
            exit 1
            ;;
    esac
done

# Detect OS and architecture
detect_os() {
    case "$(uname -s)" in
        Linux*)     echo "linux" ;;
        Darwin*)    echo "macos" ;;
        CYGWIN*|MINGW*|MSYS*) echo "windows" ;;
        *)          echo "unknown" ;;
    esac
}

detect_arch() {
    case "$(uname -m)" in
        x86_64)     echo "x64" ;;
        aarch64)    echo "arm64" ;;
        arm64)      echo "arm64" ;;
        *)          echo "x64" ;;  # default fallback
    esac
}

OS=$(detect_os)
ARCH=$(detect_arch)

echo "Detected OS: $OS, Architecture: $ARCH"

# Install .NET SDK if not present
install_dotnet() {
    if ! command -v dotnet &> /dev/null; then
        echo "Installing .NET SDK..."

        case $OS in
            linux)
                # Install .NET on Linux
                wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
                sudo dpkg -i packages-microsoft-prod.deb
                rm packages-microsoft-prod.deb
                sudo apt-get update
                sudo apt-get install -y dotnet-sdk-8.0
                ;;
            macos)
                # Install .NET on macOS
                if command -v brew &> /dev/null; then
                    brew install --cask dotnet-sdk
                else
                    echo "Please install Homebrew first: https://brew.sh/"
                    echo "Then run: brew install --cask dotnet-sdk"
                    exit 1
                fi
                ;;
            windows)
                # On Windows, assume it's already installed or user will install manually
                echo "Please download and install .NET 8.0 SDK from: https://dotnet.microsoft.com/download"
                echo "Then re-run this script."
                exit 1
                ;;
        esac

        echo ".NET SDK installed successfully."
    else
        echo ".NET SDK is already installed: $(dotnet --version)"
    fi
}

# Get repository root
get_repo_root() {
    # Try to find the script directory
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    echo "$(cd "$SCRIPT_DIR/.." && pwd)"
}

# Find Python executable
get_base_python() {
    local override="$1"

    if [ -n "$override" ]; then
        if ! command -v "$override" &> /dev/null; then
            echo "Error: Could not find python executable '$override'."
            exit 1
        fi
        echo "$override"
        return
    fi

    # Try different python commands
    for cmd in python3 python python3.9 python3.10 python3.11 python3.12; do
        if command -v "$cmd" &> /dev/null; then
            # Check version
            if "$cmd" -c "import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)" 2>/dev/null; then
                echo "$cmd"
                return
            fi
        fi
    done

    echo "Error: Python 3.9+ is required but was not found."
    echo "Please install Python 3.9 or later and try again."
    exit 1
}

# Ensure virtual environment exists
ensure_venv() {
    local python_exe="$1"
    local venv_dir="$2"

    local venv_python
    case $OS in
        windows)
            venv_python="$venv_dir/Scripts/python.exe"
            ;;
        *)
            venv_python="$venv_dir/bin/python"
            ;;
    esac

    if [ ! -f "$venv_python" ]; then
        echo "Creating virtual environment at $venv_dir ..."
        "$python_exe" -m venv "$venv_dir"
        if [ $? -ne 0 ]; then
            echo "Error: Failed to create virtual environment."
            exit 1
        fi
    else
        echo "Virtual environment already exists at $venv_dir."
    fi

    echo "$venv_python"
}

# Install Python packages
ensure_packages() {
    local python_exe="$1"

    echo "Installing python packages (torch, transformers) ..."

    "$python_exe" -m pip install --upgrade pip
    if [ $? -ne 0 ]; then
        echo "Error: Failed to upgrade pip in virtual environment."
        exit 1
    fi

    # Install base dependencies
    "$python_exe" -m pip install --upgrade "numpy<2" "scikit-learn>=1.2,<1.6"
    if [ $? -ne 0 ]; then
        echo "Error: Failed to install base python dependencies."
        exit 1
    fi

    # Install PyTorch (try CUDA first, fallback to CPU)
    echo "Installing PyTorch..."
    case $OS in
        linux)
            # Try CUDA version first
            "$python_exe" -m pip install --upgrade torch==2.2.* torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121 || \
            "$python_exe" -m pip install --upgrade torch==2.2.* torchvision torchaudio --index-url https://download.pytorch.org/whl/cpu
            ;;
        macos)
            # macOS - use MPS or CPU
            "$python_exe" -m pip install --upgrade torch==2.2.* torchvision torchaudio
            ;;
        windows)
            # Windows - try CUDA first
            "$python_exe" -m pip install --upgrade torch==2.2.* torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121 || \
            "$python_exe" -m pip install --upgrade torch==2.2.* torchvision torchaudio --index-url https://download.pytorch.org/whl/cpu
            ;;
    esac

    if [ $? -ne 0 ]; then
        echo "Error: Failed to install PyTorch."
        exit 1
    fi

    # Install other dependencies
    "$python_exe" -m pip install --upgrade "transformers==4.45.*" "flask>=3.0"
    if [ $? -ne 0 ]; then
        echo "Error: Failed to install python dependencies."
        exit 1
    fi
}

# Download model
ensure_model() {
    local repo="$1"
    local target_dir="$2"

    if ! command -v git &> /dev/null; then
        echo "Error: git is required but not found. Install Git and retry."
        exit 1
    fi

    if [ -d "$target_dir" ]; then
        echo "Model directory already exists at $target_dir; skipping clone."
        return
    fi

    local parent_dir="$(dirname "$target_dir")"
    mkdir -p "$parent_dir"

    echo "Cloning model repository $repo ..."

    # Try to install git lfs
    git lfs install 2>/dev/null || echo "Warning: git lfs install failed; continuing."

    # Clone the model
    cd "$parent_dir"
    git clone "https://huggingface.co/$repo" "$(basename "$target_dir")"
    if [ $? -ne 0 ]; then
        echo "Error: git clone failed."
        exit 1
    fi
}

# Main execution
REPO_ROOT=$(get_repo_root)
VENV_DIR="$REPO_ROOT/src/RimWorldCodeRag/.venv"
MODEL_DIR="$REPO_ROOT/src/RimWorldCodeRag/models/e5-base-v2"
EMBED_SCRIPT="$REPO_ROOT/src/RimWorldCodeRag/python/embed.py"

echo "Repository root: $REPO_ROOT"

# Install .NET SDK first
install_dotnet

# Setup Python environment
BASE_PYTHON=$(get_base_python "$PYTHON")
echo "Using Python: $BASE_PYTHON"

VENV_PYTHON=$(ensure_venv "$BASE_PYTHON" "$VENV_DIR")
ensure_packages "$VENV_PYTHON"
ensure_model "$MODEL_REPO" "$MODEL_DIR"

# Resolve paths
RESOLVED_MODEL_DIR=$(cd "$MODEL_DIR" && pwd)
RESOLVED_EMBED_SCRIPT=$(cd "$EMBED_SCRIPT" && pwd)

echo ""
echo -e "\033[0;32mSetup complete.\033[0m"
echo "  Python interpreter: $VENV_PYTHON"
echo "  Model directory:   $RESOLVED_MODEL_DIR"
echo ""
echo -e "\033[0;36mExample indexing command:\033[0m"
echo "dotnet run --project src/RimWorldCodeRag -- index \\"
echo "  --root RimWorldData \\"
echo "  --force"