# One-time setup of everything the Hexe role-play needs on a fresh Windows PC: the tools
# (git, uv, Ollama, eSpeak NG, ffmpeg, Unity Hub + Unity 2022.3.22f1), this project, the
# Python services at the exact commits they were tested with, and every model they load.
# Safe to run again: anything already in place is skipped, so it also repairs a half setup.
# A step that fails does not stop the others; the end of the run lists what to look at.
#
# On a PC that has nothing yet, in PowerShell:
#   irm https://raw.githubusercontent.com/Saguny/MediaLabAI/main/Assets/HexeRolePlay/Tools~/setup.ps1 | iex
# From inside a clone: right-click > "Run with PowerShell".
#
# Everything lands next to the project: <workspace>\VRCChatDollKit, \speaches, \Kokoro-FastAPI,
# \chatterbox-tts-api, \SAiD, \facetrack, \emotion. The workspace defaults to Documents\Workspace; pass -Workspace to change it.
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
$failed       = New-Object System.Collections.ArrayList

# Run from a clone, the workspace is the folder the project sits in.
$inClone = $PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot "..\..\..\Assets"))
if (-not $Workspace) {
    $Workspace = if ($inClone) { (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path }
                 else { Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Workspace" }
}
$project = if ($inClone) { (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path } else { Join-Path $Workspace "VRCChatDollKit" }
$tools   = Join-Path $project "Assets\HexeRolePlay\Tools~"
New-Item -ItemType Directory -Force $Workspace | Out-Null
$log = Join-Path $Workspace "hexe_setup.log"
Start-Transcript -Path $log -Force | Out-Null

function Fail($text) { Write-Host "  FAILED: $text" -ForegroundColor Red; [void]$failed.Add($text) }

function Step($text, [scriptblock]$work) {
    Write-Host "`n== $text" -ForegroundColor Cyan
    $here = Get-Location
    try { & $work } catch { Fail "$text - $_" } finally { Set-Location $here }
}

# Native tools don't throw on failure; make them.
function Run([scriptblock]$cmd) { & $cmd; if ($LASTEXITCODE -ne 0) { throw "Failed ($LASTEXITCODE): $cmd" } }
function Refresh-Path { $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User") }

# For downloads: servers answer 5xx now and then and big transfers drop. All of these resume.
function Retry([scriptblock]$cmd) {
    for ($try = 1; ; $try++) {
        try { Run $cmd; return }
        catch {
            if ($try -ge 4) { throw }
            Write-Host "  $_ - trying again in $($try * 15) s" -ForegroundColor Yellow
            Start-Sleep ($try * 15)
        }
    }
}

# Under a .part name until complete, or a broken download would count as done on the next run.
function Download($url, $file) {
    if (Test-Path $file) { return }
    Retry { curl.exe -L --fail -C - -o "$file.part" $url }
    Move-Item "$file.part" $file
}

# Into the Hugging Face cache the services read, from the venv of the current folder. Here rather
# than on the service's first start, where a failed download only looks like it never comes up.
function Get-HfModel($repo, $files) {
    $allow = if ($files) { ", allow_patterns=['" + ($files -join "','") + "']" } else { "" }
    Retry { .\.venv\Scripts\python.exe -I -c "from huggingface_hub import snapshot_download; snapshot_download('$repo'$allow)" }
}

function Install($id, [scriptblock]$present) {
    if (& $present) { Write-Host "  $id already installed"; return }
    Write-Host "  installing $id"
    winget install --id $id -e --source winget --silent --accept-package-agreements --accept-source-agreements
    Refresh-Path
    if (-not (& $present)) { Fail "$id did not install - install it by hand and run this again." }
}

function Clone($name, $url, $commit) {
    $dir = Join-Path $Workspace $name
    if (-not (Test-Path (Join-Path $dir ".git"))) { Run { git clone $url $dir } }
    Run { git -C $dir fetch --quiet origin }
    Run { git -C $dir checkout --quiet $commit }
    $dir
}

Step "Tools" {
    Install "Git.Git"             { Get-Command git -ErrorAction SilentlyContinue }
    Install "astral-sh.uv"        { Get-Command uv -ErrorAction SilentlyContinue }
    Install "Ollama.Ollama"       { Test-Path $ollamaExe }
    Install "eSpeak-NG.eSpeak-NG" { Test-Path $espeakDll }
    # Encodes the replay film to MP4. Unity also finds it in winget's folder if PATH is stale.
    Install "Gyan.FFmpeg"         { (Get-Command ffmpeg -ErrorAction SilentlyContinue) -or (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg*" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue) }
    Install "Unity.UnityHub"      { Test-Path $unityHub }
    Write-Host ("  GPU: " + $(if ($gpu) { "NVIDIA found, services use CUDA" } else { "none found, services run on CPU (slower)" }))
    $freeGb = [int]((Get-Item $Workspace).PSDrive.Free / 1GB)
    Write-Host "  Disk: $freeGb GB free on $((Get-Item $Workspace).PSDrive.Name):"
    if ($freeGb -lt 40) { Write-Host "  That may not be enough: a full install takes tens of GB." -ForegroundColor Yellow }
}

Step "Project" {
    if (-not (Test-Path (Join-Path $project ".git"))) { Run { git clone $repoUrl $project } }
    else {
        Write-Host "  $project"
        # The steps below read their files from this clone, so an old one fails on files it lacks.
        git -C $project pull --ff-only
        if ($LASTEXITCODE -ne 0) { Fail "Could not update $project (local edits in the way?). Open it in Unity to auto-update, or commit or stash them, and run this again." }
    }
}

Step "speaches (Whisper speech-to-text)" {
    $dir = Clone "speaches" "https://github.com/speaches-ai/speaches.git" "993994f7984bf3fe9655b267448328cf66fccb42"
    Set-Location $dir
    Run { uv sync }
    # Whisper on the GPU needs cuBLAS and cuDNN 9; start_services.ps1 puts them on PATH. After the
    # sync, which would remove them as not in the lockfile.
    if ($gpu) { Run { uv pip install "nvidia-cublas-cu12==12.*" "nvidia-cudnn-cu12==9.*" } }
    Get-HfModel $sttModel
}

Step "Kokoro-FastAPI (text-to-speech)" {
    $dir = Clone "Kokoro-FastAPI" "https://github.com/remsky/Kokoro-FastAPI.git" "26eec068d8ce6f559afef83f68933127ac38e315"
    Set-Location $dir
    if (-not (Test-Path ".venv")) { Run { uv venv --python 3.12 } }
    Run { uv pip install -e $(if ($gpu) { ".[gpu]" } else { ".[cpu]" }) }
    if (-not (Test-Path "api\src\models\v1_0\kokoro-v1_0.pth")) {
        Retry { uv run --no-sync python docker/scripts/download_model.py --output api/src/models/v1_0 }
    }
}

Step "Chatterbox (expressive text-to-speech; Kokoro stays as the fast fallback)" {
    $dir = Clone "chatterbox-tts-api" "https://github.com/travisvn/chatterbox-tts-api.git" "a5f466128e4baa8e4cceb3bba9b7ca9de6f7ec6b"
    Set-Location $dir
    Copy-Item (Join-Path $tools "chatterbox\hexe.env") .env -Force
    if (-not (Test-Path ".venv")) { Run { uv venv --python 3.11 } }
    # Its requirements take torch from PyPI, which on Windows is the CPU build; the CUDA build
    # comes from PyTorch's own index.
    $torchIndex = if ($gpu) { "https://download.pytorch.org/whl/cu124" } else { "https://download.pytorch.org/whl/cpu" }
    $torchPkgs = if ($gpu) { @("torch==2.6.0+cu124", "torchaudio==2.6.0+cu124") } else { @("torch==2.6.0", "torchaudio==2.6.0") }
    # setuptools: Chatterbox's watermarker (resemble-perth) imports pkg_resources, which uv's
    # environments do not include, and fails to load the model without it.
    Run { uv pip install --index-strategy unsafe-best-match --extra-index-url $torchIndex @torchPkgs "setuptools<81" -r requirements.txt }
    Write-Host "  downloading the Chatterbox model (~3 GB)"
    # The files its from_pretrained loads; the repository holds other, larger models too.
    Get-HfModel "ResembleAI/chatterbox" @("ve.safetensors", "t3_cfg.safetensors", "s3gen.safetensors", "tokenizer.json", "conds.pt")
}

Step "SAiD (lip-sync blendshapes)" {
    $dir = Clone "SAiD" "https://github.com/yunik1004/SAiD.git" "bb7b18018358d3156569d8051f386943744a5ea0"
    Set-Location $dir
    Copy-Item (Join-Path $tools "said\said_service.py") . -Force
    if (-not (Test-Path ".venv")) { Run { uv venv --python 3.11 } }
    # The torch index is listed first for torch only; best-match keeps numpy & co. from PyPI.
    Run { uv pip install --index-strategy unsafe-best-match -r (Join-Path $tools "said\requirements.txt") }
    Run { uv pip install --no-deps -e . }
    Write-Host "  downloading SAiD weights (400 MB)"
    Download "https://huggingface.co/yunik1004/SAiD/resolve/main/SAiD.pth" "SAiD.pth"
}

Step "Face tracking (webcam -> ARKit blendshapes, for the replay film)" {
    $dir = Join-Path $Workspace "facetrack"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Location $dir
    Copy-Item (Join-Path $tools "facetrack\hexe_facetrack.py") . -Force
    if (-not (Test-Path ".venv")) { Run { uv venv --python 3.11 } }
    Run { uv pip install -r (Join-Path $tools "facetrack\requirements.txt") }
    Download "https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task" "face_landmarker.task"
}

Step "Speech emotion (emotion2vec+, judges how a read line sounds)" {
    $dir = Join-Path $Workspace "emotion"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Location $dir
    Copy-Item (Join-Path $tools "emotion\emotion_service.py") . -Force
    if (-not (Test-Path ".venv")) { Run { uv venv --python 3.11 } }
    # CPU build on purpose: the model is small and the GPU is shared by the LLM, Whisper and TTS.
    Run { uv pip install --index-strategy unsafe-best-match --extra-index-url https://download.pytorch.org/whl/cpu torch==2.6.0 torchaudio==2.6.0 -r (Join-Path $tools "emotion\requirements.txt") }
    # funasr's name on Hugging Face for iic/emotion2vec_plus_base, which the service asks for.
    Get-HfModel "emotion2vec/emotion2vec_plus_base"
}

Step "LLM ($llmModel)" {
    $ollamaUp = { try { Invoke-WebRequest http://127.0.0.1:11434/api/tags -UseBasicParsing -TimeoutSec 2 | Out-Null; $true } catch { $false } }
    if (-not (& $ollamaUp)) {
        Start-Process $ollamaExe -ArgumentList "serve" -WindowStyle Hidden
        for ($i = 0; $i -lt 30 -and -not (& $ollamaUp); $i++) { Start-Sleep 1 }
    }
    Retry { & $ollamaExe pull $llmModel }
}

Step "Unity $unityVersion" {
    if (Test-Path $unityExe) { Write-Host "  already installed" }
    else {
        # Hub's CLI; the editor alone is enough (Windows build support is built in).
        & $unityHub -- --headless install --version $unityVersion --changeset $unityChanges
        if (-not (Test-Path $unityExe)) { Write-Host "  Unity Hub did not install the editor - install $unityVersion from Unity Hub > Installs." -ForegroundColor Yellow }
    }
}

if ($failed.Count -gt 0) {
    Write-Host "`nNot finished. These steps failed (their errors are further up in this window):" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Run this again once they are sorted: finished steps are skipped and downloads resume."
    Write-Host "This list is also in $log"
} else {
    Write-Host "`nDone. Left to do by hand, once:" -ForegroundColor Green
    Write-Host "  1. Open Unity Hub, sign in and activate a license (Preferences > Licenses)."
    Write-Host "  2. Run $tools\start_services.ps1 - it starts everything and opens the project"
    Write-Host "     (the first open imports the whole project and takes a while)."
    Write-Host "  3. In Unity: Tools > Hexe Role-Play > Auto-Update from GitHub."
}
Stop-Transcript | Out-Null
if ($PSScriptRoot) {
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
    if ((Get-Process -Id $parent -ErrorAction SilentlyContinue).Name -eq "explorer") { Read-Host "Press Enter to close" | Out-Null }
}
