# One-time setup of everything the Hexe role-play needs on a fresh Windows PC: the tools
# (git, uv, Ollama, eSpeak NG, Unity Hub + Unity 2022.3.22f1), this project, the three
# Python services at the exact commits they were tested with, and every model they load.
# Safe to run again: anything already in place is skipped, so it also repairs a half setup.
#
# On a PC that has nothing yet, in PowerShell:
#   irm https://raw.githubusercontent.com/Saguny/MediaLabAI/main/Assets/HexeRolePlay/Tools~/setup.ps1 | iex
# From inside a clone: right-click > "Run with PowerShell".
#
# Everything lands next to the project: <workspace>\VRCChatDollKit, \speaches, \Kokoro-FastAPI,
# \SAiD. The workspace defaults to Documents\Workspace; pass -Workspace to change it.
# Downloads are ~15 GB (LLM ~9 GB, Unity ~4 GB, PyTorch builds). Installers may ask for admin.
param([string]$Workspace)

$ErrorActionPreference = "Stop"
$repoUrl      = "https://github.com/Saguny/MediaLabAI.git"
$llmModel     = "qwen2.5:14b-instruct-q4_K_M"
$sttModel     = "Systran/faster-whisper-small"
$unityVersion = "2022.3.22f1"
$unityChanges = "887be4894c44"
$unityHub     = "C:\Program Files\Unity Hub\Unity Hub.exe"
$unityExe     = "C:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
$ollamaExe    = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
$espeakDll    = "C:\Program Files\eSpeak NG\libespeak-ng.dll"
$gpu          = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)

# Run from a clone, the workspace is the folder the project sits in.
$inClone = $PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot "..\..\..\Assets"))
if (-not $Workspace) {
    $Workspace = if ($inClone) { (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path }
                 else { Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Workspace" }
}
$project = if ($inClone) { (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path } else { Join-Path $Workspace "VRCChatDollKit" }
New-Item -ItemType Directory -Force $Workspace | Out-Null

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
# Native tools don't throw on failure; make them.
function Run([scriptblock]$cmd) { & $cmd; if ($LASTEXITCODE -ne 0) { throw "Failed ($LASTEXITCODE): $cmd" } }
function Refresh-Path { $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User") }

function Install($id, [scriptblock]$present) {
    if (& $present) { Write-Host "  $id already installed"; return }
    Write-Host "  installing $id"
    winget install --id $id -e --source winget --silent --accept-package-agreements --accept-source-agreements
    Refresh-Path
    if (-not (& $present)) { throw "$id did not install - install it by hand and run this again." }
}

function Clone($name, $url, $commit) {
    $dir = Join-Path $Workspace $name
    if (-not (Test-Path (Join-Path $dir ".git"))) { Run { git clone $url $dir } }
    Run { git -C $dir fetch --quiet origin }
    Run { git -C $dir checkout --quiet $commit }
    $dir
}

Step "Tools"
Install "Git.Git"             { Get-Command git -ErrorAction SilentlyContinue }
Install "astral-sh.uv"        { Get-Command uv -ErrorAction SilentlyContinue }
Install "Ollama.Ollama"       { Test-Path $ollamaExe }
Install "eSpeak-NG.eSpeak-NG" { Test-Path $espeakDll }
Install "Unity.UnityHub"      { Test-Path $unityHub }
Write-Host ("  GPU: " + $(if ($gpu) { "NVIDIA found, services use CUDA" } else { "none found, services run on CPU (slower)" }))

Step "Project"
if (-not (Test-Path (Join-Path $project ".git"))) { Run { git clone $repoUrl $project } } else { Write-Host "  $project" }
$tools = Join-Path $project "Assets\HexeRolePlay\Tools~"

Step "speaches (Whisper speech-to-text)"
$dir = Clone "speaches" "https://github.com/speaches-ai/speaches.git" "993994f7984bf3fe9655b267448328cf66fccb42"
Push-Location $dir
Run { uv sync }
# Whisper on the GPU needs cuBLAS and cuDNN 9; start_services.ps1 puts them on PATH. After the
# sync, which would remove them as not in the lockfile.
if ($gpu) { Run { uv pip install "nvidia-cublas-cu12==12.*" "nvidia-cudnn-cu12==9.*" } }
Pop-Location

Step "Kokoro-FastAPI (text-to-speech)"
$dir = Clone "Kokoro-FastAPI" "https://github.com/remsky/Kokoro-FastAPI.git" "26eec068d8ce6f559afef83f68933127ac38e315"
Push-Location $dir
if (-not (Test-Path ".venv")) { Run { uv venv --python 3.12 } }
Run { uv pip install -e $(if ($gpu) { ".[gpu]" } else { ".[cpu]" }) }
if (-not (Test-Path "api\src\models\v1_0\kokoro-v1_0.pth")) {
    Run { uv run --no-sync python docker/scripts/download_model.py --output api/src/models/v1_0 }
}
Pop-Location

Step "SAiD (lip-sync blendshapes)"
$dir = Clone "SAiD" "https://github.com/yunik1004/SAiD.git" "bb7b18018358d3156569d8051f386943744a5ea0"
Push-Location $dir
Copy-Item (Join-Path $tools "said\said_service.py") . -Force
if (-not (Test-Path ".venv")) { Run { uv venv --python 3.11 } }
# The torch index is listed first for torch only; best-match keeps numpy & co. from PyPI.
Run { uv pip install --index-strategy unsafe-best-match -r (Join-Path $tools "said\requirements.txt") }
Run { uv pip install --no-deps -e . }
if (-not (Test-Path "SAiD.pth")) {
    Write-Host "  downloading SAiD weights (400 MB)"
    Run { curl.exe -L --fail -o SAiD.pth https://huggingface.co/yunik1004/SAiD/resolve/main/SAiD.pth }
}
Pop-Location

Step "LLM ($llmModel)"
$ollamaUp = { try { Invoke-WebRequest http://127.0.0.1:11434/api/tags -UseBasicParsing -TimeoutSec 2 | Out-Null; $true } catch { $false } }
if (-not (& $ollamaUp)) {
    Start-Process $ollamaExe -ArgumentList "serve" -WindowStyle Hidden
    for ($i = 0; $i -lt 30 -and -not (& $ollamaUp); $i++) { Start-Sleep 1 }
}
Run { & $ollamaExe pull $llmModel }

Step "Unity $unityVersion"
if (Test-Path $unityExe) { Write-Host "  already installed" }
else {
    # Hub's CLI; the editor alone is enough (Windows build support is built in).
    & $unityHub -- --headless install --version $unityVersion --changeset $unityChanges
    if (-not (Test-Path $unityExe)) { Write-Host "  Unity Hub did not install the editor - install $unityVersion from Unity Hub > Installs." -ForegroundColor Yellow }
}

Write-Host "`nDone. Left to do by hand, once:" -ForegroundColor Green
Write-Host "  1. Open Unity Hub, sign in and activate a license (Preferences > Licenses)."
Write-Host "  2. Run $tools\start_services.ps1 - it starts everything and opens the project"
Write-Host "     (the first open imports the whole project and takes a while)."
Write-Host "  3. In Unity: Tools > Hexe Role-Play > Auto-Update from GitHub."
if ($PSScriptRoot) {
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
    if ((Get-Process -Id $parent -ErrorAction SilentlyContinue).Name -eq "explorer") { Read-Host "Press Enter to close" | Out-Null }
}
