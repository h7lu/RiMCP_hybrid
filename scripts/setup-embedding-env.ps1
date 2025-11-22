[CmdletBinding()]
param(
    [string]$Python = "",
    [string]$ModelRepo = "intfloat/e5-base-v2"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".." )).Path
$venvDir = Join-Path $repoRoot "src\RimWorldCodeRag\.venv"
$modelDir = Join-Path $repoRoot "src\RimWorldCodeRag\models\e5-base-v2"
$embedScript = Join-Path $repoRoot "src\RimWorldCodeRag\python\embed.py"

function Get-BasePython {
    param([string]$Override)

    if ($Override) {
        $resolved = Get-Command $Override -ErrorAction SilentlyContinue
        if (-not $resolved) {
            throw "Could not find python executable '$Override'."
        }
        return $resolved.Path
    }

    $pyLauncher = Get-Command "py" -ErrorAction SilentlyContinue
    if ($pyLauncher) {
        $candidate = & py -3 -c "import sys; print(sys.executable)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $candidate) {
            return $candidate.Trim()
        }
    }

    foreach ($name in @("python", "python3")) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) {
            return $cmd.Path
        }
    }

    throw "Python 3.9+ is required but was not found. Install Python or pass -Python <path>."
}

function Ensure-Venv {
    param([string]$PythonExe, [string]$VenvDirectory)

    $venvPython = Join-Path $VenvDirectory "Scripts\python.exe"
    if (-not (Test-Path $venvPython)) {
        Write-Host "Creating virtual environment at $VenvDirectory ..."
        & $PythonExe "-m" "venv" $VenvDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create virtual environment."
        }
    }
    else {
        Write-Host "Virtual environment already exists at $VenvDirectory."
    }

    return (Resolve-Path $venvPython).Path
}

function Ensure-Packages {
    param([string]$PythonExe)

    Write-Host "Installing python packages (torch, transformers) ..."
    & $PythonExe "-m" "pip" "install" "--upgrade" "pip"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to upgrade pip in virtual environment."
    }

    & $PythonExe "-m" "pip" "install" "--upgrade" "numpy<2" "scikit-learn>=1.2,<1.6"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install base python dependencies."
    }

    & $PythonExe "-m" "pip" "install" "--upgrade" "--extra-index-url" "https://download.pytorch.org/whl/cu121" "torch==2.2.*"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install CUDA-enabled torch."
    }

    & $PythonExe "-m" "pip" "install" "--upgrade" "transformers==4.45.*" "flask>=3.0"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install python dependencies."
    }
}

function Ensure-Model {
    param([string]$Repo, [string]$TargetDirectory)

    $git = Get-Command "git" -ErrorAction SilentlyContinue
    if (-not $git) {
        throw "git is required but not found. Install Git and retry."
    }

    if (Test-Path $TargetDirectory) {
        Write-Host "Model directory already exists at $TargetDirectory; skipping clone."
        return
    }

    $parent = Split-Path $TargetDirectory -Parent
    if (-not (Test-Path $parent)) {
        [void](New-Item -ItemType Directory -Path $parent -Force)
    }

    Write-Host "Cloning model repository $Repo ..."
    try {
        & git lfs install *> $null
    }
    catch {
        Write-Warning "git lfs install failed; continuing."
    }

    Push-Location $parent
    try {
        & git clone "https://huggingface.co/$Repo" (Split-Path $TargetDirectory -Leaf)
        if ($LASTEXITCODE -ne 0) {
            throw "git clone failed."
        }
    }
    finally {
        Pop-Location
    }
}

$basePython = Get-BasePython -Override $Python
$venvPython = Ensure-Venv -PythonExe $basePython -VenvDirectory $venvDir
Ensure-Packages -PythonExe $venvPython
Ensure-Model -Repo $ModelRepo -TargetDirectory $modelDir

$resolvedModelDir = (Resolve-Path $modelDir).Path
$resolvedEmbedScript = (Resolve-Path $embedScript).Path

Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host "  Python interpreter: $venvPython"
Write-Host "  Model directory:   $resolvedModelDir"
Write-Host ""
Write-Host "Example indexing command:" -ForegroundColor Cyan
$example = @(
    "dotnet run --project src\RimWorldCodeRag -- index",
    "  --root Source_CSharp_Rimworld",
    "  --lucene index\lucene",
    "  --vec index\vec",
    "  --graph index\graph.db",
    "  --model `"$resolvedModelDir`"",
    "  --python-script `"$resolvedEmbedScript`"",
    "  --python-exec `"$venvPython`"",
    "  --force"
)
$example | ForEach-Object { Write-Host $_ }
