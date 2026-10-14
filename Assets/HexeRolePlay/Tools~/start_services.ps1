# Starts everything the Hexe role-play needs, each in its own minimized window:
#   Ollama (LLM, :11434), speaches (Whisper STT, :8000), Kokoro-FastAPI (TTS, :8880),
#   SAiD (lip-sync blendshapes, :8900) and the Unity project.
# Services that already answer are left alone, so it is safe to run twice.
#
#   Right-click > "Run with PowerShell", or
#   powershell -ExecutionPolicy Bypass -File start_services.ps1 [-NoUnity] [-Stop]
#
# -Stop closes the windows this script opened. Closing a window by hand stops that service too.
param([switch]$NoUnity, [switch]$Stop)

$ErrorActionPreference = "Stop"
# The services sit next to the project, as setup.ps1 lays them out.
$project   = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$workspace = Split-Path $project
$unityExe  = "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"
$ollamaExe = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
$llmModel  = "qwen2.5:14b-instruct-q4_K_M"
$sttModel  = "Systran/faster-whisper-small"
$pidFile   = Join-Path $env:TEMP "hexe_services.json"

$services = @(
    @{ Name = "Ollama"; Port = 11434; Health = "http://127.0.0.1:11434/api/tags"; Dir = $workspace; Script = @"
`$env:OLLAMA_KEEP_ALIVE = "2h"   # keep the model in memory between turns (default unloads after 5 idle minutes)
& '$ollamaExe' serve
"@ },
    @{ Name = "speaches"; Port = 8000; Health = "http://127.0.0.1:8000/health"; Dir = "$workspace\speaches"; Script = @'
$env:PYTHONUTF8 = 1
$env:WHISPER__INFERENCE_DEVICE = "cpu"   # the CUDA build can't find cuDNN 9, CPU int8 is fast enough for "small"
$env:WHISPER__COMPUTE_TYPE = "int8"
$env:STT_MODEL_TTL = -1                  # keep Whisper loaded instead of unloading after 5 idle minutes
$env:CHAT_COMPLETION_BASE_URL = "http://127.0.0.1:11434/v1"
.\.venv\Scripts\uvicorn.exe --factory --host 0.0.0.0 --port 8000 speaches.main:create_app
'@ },
    @{ Name = "Kokoro"; Port = 8880; Health = "http://127.0.0.1:8880/health"; Dir = "$workspace\Kokoro-FastAPI"; Script = @'
$env:PHONEMIZER_ESPEAK_LIBRARY = "C:\Program Files\eSpeak NG\libespeak-ng.dll"
$env:PYTHONUTF8 = 1
$env:PROJECT_ROOT = "$pwd"
$env:USE_GPU = if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { "true" } else { "false" }
$env:PYTHONPATH = "$env:PROJECT_ROOT;$env:PROJECT_ROOT/api"
$env:MODEL_DIR = "src/models"
$env:VOICES_DIR = "src/voices/v1_0"
$env:WEB_PLAYER_PATH = "$env:PROJECT_ROOT/web"
uv run --no-sync uvicorn api.src.main:app --host 0.0.0.0 --port 8880
'@ },
    @{ Name = "SAiD"; Port = 8900; Health = "http://127.0.0.1:8900/health"; Dir = "$workspace\SAiD"; Script = @'
$env:PYTHONUTF8 = 1
.\.venv\Scripts\python.exe said_service.py
'@ }
)

function Test-Up($url) {
    try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 | Out-Null; $true } catch { $false }
}

if ($Stop) {
    if (Test-Path $pidFile) {
        foreach ($entry in (Get-Content $pidFile | ConvertFrom-Json)) {
            if (Get-Process -Id $entry.Pid -ErrorAction SilentlyContinue) {
                taskkill /T /F /PID $entry.Pid | Out-Null
                Write-Host "Stopped $($entry.Name)"
            }
        }
        Remove-Item $pidFile
    } else {
        Write-Host "Nothing to stop (no windows opened by this script are recorded)."
    }
    return
}

# Launch whatever isn't answering yet.
$started = @()
foreach ($s in $services) {
    if (Test-Up $s.Health) { Write-Host ("{0,-9} already running on :{1}" -f $s.Name, $s.Port); continue }
    $body = "`$host.UI.RawUI.WindowTitle = 'Hexe - $($s.Name) :$($s.Port)'`nSet-Location '$($s.Dir)'`n$($s.Script)"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    $p = Start-Process powershell -ArgumentList "-NoExit", "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encoded -WindowStyle Minimized -PassThru
    $started += [pscustomobject]@{ Name = $s.Name; Pid = $p.Id }
    Write-Host ("{0,-9} starting on :{1}" -f $s.Name, $s.Port)
}
if ($started.Count -gt 0) {
    $previous = if (Test-Path $pidFile) { @(Get-Content $pidFile | ConvertFrom-Json) } else { @() }
    ConvertTo-Json @($previous + $started) | Set-Content $pidFile -Encoding UTF8
}

if (-not $NoUnity) {
    $unityOpen = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -like "*VRCChatDollKit*" }
    if ($unityOpen) { Write-Host "Unity     already open" }
    elseif (Test-Path $unityExe) { Start-Process $unityExe -ArgumentList "-projectPath", "`"$project`""; Write-Host "Unity     opening project" }
    else { Write-Host "Unity     editor not found at $unityExe - open the project from Unity Hub" }
}

# Wait for every service to answer. Kokoro and SAiD load their models before they start listening.
Write-Host "`nWaiting for services..."
$deadline = (Get-Date).AddMinutes(3)
$pending = [System.Collections.ArrayList]@($services)
while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
    foreach ($s in @($pending)) {
        if (Test-Up $s.Health) { Write-Host ("  {0,-9} ready" -f $s.Name) -ForegroundColor Green; $pending.Remove($s) }
    }
    if ($pending.Count -gt 0) { Start-Sleep -Seconds 2 }
}
foreach ($s in $pending) { Write-Host ("  {0,-9} NOT responding - restore its window to see the error" -f $s.Name) -ForegroundColor Red }

# Load the models now so the first question in a session isn't slow (the LLM cold load took ~60 s).
if (Test-Up $services[0].Health) {
    Write-Host "Loading $llmModel into memory..."
    try {
        Invoke-RestMethod "http://127.0.0.1:11434/api/generate" -Method Post -ContentType "application/json" -TimeoutSec 300 `
            -Body (@{ model = $llmModel; keep_alive = "2h" } | ConvertTo-Json) | Out-Null
        Write-Host "  LLM       loaded" -ForegroundColor Green
    } catch { Write-Host "  LLM       failed to load: $($_.Exception.Message) (is the model pulled? ollama pull $llmModel)" -ForegroundColor Red }
}
if (Test-Up $services[1].Health) {
    Write-Host "Loading $sttModel..."
    # Downloads it on a fresh machine; answers "already exists" otherwise.
    try { Invoke-WebRequest "http://127.0.0.1:8000/v1/models/$sttModel" -Method Post -UseBasicParsing -TimeoutSec 600 | Out-Null }
    catch { Write-Host "  Whisper   download failed: $($_.Exception.Message)" -ForegroundColor Red }
    # Half a second of silence as a 16 kHz 16-bit mono WAV.
    $wav = Join-Path $env:TEMP "hexe_warmup.wav"
    $dataBytes = 16000
    $w = New-Object IO.BinaryWriter([IO.File]::Create($wav))
    $w.Write([Text.Encoding]::ASCII.GetBytes("RIFF")); $w.Write([int](36 + $dataBytes)); $w.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
    $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]16000); $w.Write([int]32000); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes("data")); $w.Write([int]$dataBytes); $w.Write((New-Object byte[] $dataBytes)); $w.Close()
    $code = curl.exe -s -o NUL -w "%{http_code}" --max-time 180 -F "file=@$wav" -F "model=$sttModel" -F "language=en" "http://127.0.0.1:8000/v1/audio/transcriptions"
    if ($code -eq "200") { Write-Host "  Whisper   loaded" -ForegroundColor Green } else { Write-Host "  Whisper   warm-up returned HTTP $code" -ForegroundColor Red }
    Remove-Item $wav -ErrorAction SilentlyContinue
}

Write-Host "`nDone. In Unity: open SampleScene, press Play, then Space to start (Esc ends, Tab ends a turn, F1 operator panel)."
# Keep the window open when started from Explorer (right-click > Run with PowerShell).
$parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
if ((Get-Process -Id $parent -ErrorAction SilentlyContinue).Name -eq "explorer") { Read-Host "Press Enter to close" | Out-Null }
