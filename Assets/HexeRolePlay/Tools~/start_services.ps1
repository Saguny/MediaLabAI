# Starts everything the Hexe role-play needs, each in its own minimized window:
#   Ollama (LLM, :11434), speaches (Whisper STT, :8000), Chatterbox or Kokoro (TTS, :8880),
#   SAiD (lip-sync blendshapes, :8900), the webcam face tracker (UDP :39540, health :8910),
#   speech emotion (emotion2vec+, :8920) and the Unity project.
# Services that already answer are left alone, so it is safe to run twice.
#
#   Right-click > "Run with PowerShell", or
#   powershell -ExecutionPolicy Bypass -File start_services.ps1 [-NoUnity] [-Stop] [-Camera 1] [-Tts kokoro] [-Stt gpu]
#
# -Stt picks where Whisper runs: cpu (default, ~1.7 s a reply) or gpu (~0.2 s, but only when
# the card has memory to spare next to the LLM and the voice).
#
# -Tts picks the voice engine: turbo (default) is expressive and keeps scripted lines on disk,
# kokoro is flat and the fastest, chatterbox is the most expressive but too slow to converse.
# All answer on :8880 with the same voice names, so Unity needs no change.
#
# -Stop closes the windows this script opened. Closing a window by hand stops that service too.
param([switch]$NoUnity, [switch]$Stop, [int]$Camera = 0, [ValidateSet("turbo", "chatterbox", "kokoro")][string]$Tts = "turbo",
      [ValidateSet("cpu", "gpu")][string]$Stt = "cpu")

$ErrorActionPreference = "Stop"
# The services sit next to the project, as setup.ps1 lays them out.
$project   = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$workspace = Split-Path $project
$unityExe  = "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"
$ollamaExe = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
$llmModel  = "qwen2.5:14b-instruct-q4_K_M"
$hexeModel = "hexe-qwen"   # what the scene asks for; created below
$sttModel  = "Systran/faster-whisper-small"
$pidFile   = Join-Path $env:TEMP "hexe_services.json"

$services = @(
    @{ Name = "Ollama"; Port = 11434; Health = "http://127.0.0.1:11434/api/tags"; Dir = $workspace; Script = @"
`$env:OLLAMA_KEEP_ALIVE = "-1"   # keep the model in memory for good (default unloads after 5 idle minutes; a cold load takes ~60 s)
& '$ollamaExe' serve
"@ },
    @{ Name = "speaches"; Port = 8000; Health = "http://127.0.0.1:8000/health"; Dir = "$workspace\speaches"; Script = @'
$env:PYTHONUTF8 = 1
# -Stt gpu: a reply's transcript takes ~0.2 s instead of ~1.7 s, but only while the card has
# room. Beside the 14B LLM, Turbo, SAiD and emotion2vec a 16 GB card is full and Whisper took
# ~10 s, so the CPU is the default. ctranslate2 ships only cuDNN's loader; cuBLAS and the rest
# of cuDNN come as pip wheels (setup.ps1) and are found on PATH.
if ($env:HEXE_STT_DEVICE -eq "gpu" -and (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) {
    $nvidia = "$pwd\.venv\Lib\site-packages\nvidia"
    $env:Path = "$nvidia\cublas\bin;$nvidia\cudnn\bin;$env:Path"
    $env:WHISPER__INFERENCE_DEVICE = "cuda"
    $env:WHISPER__COMPUTE_TYPE = "float16"
} else {
    $env:WHISPER__INFERENCE_DEVICE = "cpu"
    $env:WHISPER__COMPUTE_TYPE = "int8"
    $env:WHISPER__CPU_THREADS = 8        # ctranslate2 uses 4 by default; 8 measured ~1.7 s a reply on a Ryzen 7 5700X3D
}
$env:STT_MODEL_TTL = -1                  # keep Whisper loaded instead of unloading after 5 idle minutes
$env:CHAT_COMPLETION_BASE_URL = "http://127.0.0.1:11434/v1"
.\.venv\Scripts\uvicorn.exe --factory --host 0.0.0.0 --port 8000 speaches.main:create_app
'@ },
    $(if ($Tts -eq "turbo") {
    @{ Name = "Turbo"; Port = 8880; Health = "http://127.0.0.1:8880/health"; Dir = "$workspace\chatterbox-turbo"; Script = @"
`$env:PYTHONUTF8 = 1
.\.venv\Scripts\python.exe -I turbo_service.py --voices '$PSScriptRoot\chatterbox\voices'
"@ }
    } elseif ($Tts -eq "chatterbox") {
    @{ Name = "Chatterbox"; Port = 8880; Health = "http://127.0.0.1:8880/health"; Dir = "$workspace\chatterbox-tts-api"; Script = @'
$env:PYTHONUTF8 = 1
.\.venv\Scripts\python.exe main.py
'@ }
    } else {
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
'@ }
    }),
    @{ Name = "SAiD"; Port = 8900; Health = "http://127.0.0.1:8900/health"; Dir = "$workspace\SAiD"; Script = @'
$env:PYTHONUTF8 = 1
.\.venv\Scripts\python.exe said_service.py
'@ },
    # Films the visitor's face for the replay movie (blendshapes only, never the image).
    @{ Name = "FaceTrack"; Port = 8910; Health = "http://127.0.0.1:8910/health"; Dir = "$workspace\facetrack"; Script = @"
`$env:PYTHONUTF8 = 1
.\.venv\Scripts\python.exe -I hexe_facetrack.py --camera $Camera
"@ }
    @{ Name = "Emotion"; Port = 8920; Health = "http://127.0.0.1:8920/health"; Dir = "$workspace\emotion"; Script = @'
$env:PYTHONUTF8 = 1
.\.venv\Scripts\python.exe -I emotion_service.py
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

# Launch whatever isn't answering yet. The service windows inherit this.
$env:HEXE_STT_DEVICE = $Stt
$started = @()
$notInstalled = @()
foreach ($s in $services) {
    if (Test-Up $s.Health) { Write-Host ("{0,-9} already running on :{1}" -f $s.Name, $s.Port); continue }
    # A half setup: a window for it would only die, and the wait below would run out for nothing.
    $installed = if ($s.Name -eq "Ollama") { Test-Path $ollamaExe } else { Test-Path "$($s.Dir)\.venv\Scripts\python.exe" }
    if (-not $installed) {
        Write-Host ("{0,-9} not installed - run setup.ps1" -f $s.Name) -ForegroundColor Red
        $notInstalled += $s
        continue
    }
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

# Wait for every service to answer. The TTS, SAiD and emotion services load their models before
# they start listening; on the very first start Chatterbox also downloads its model (~3 GB).
Write-Host "`nWaiting for services..."
$deadline = (Get-Date).AddMinutes(8)
$pending = [System.Collections.ArrayList]@($services | Where-Object { $notInstalled -notcontains $_ })
$problems = $notInstalled.Count
while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
    foreach ($s in @($pending)) {
        if (-not (Test-Up $s.Health)) { continue }
        # Chatterbox answers /health while it loads, and still after its model failed to load,
        # when every line it is asked for comes back HTTP 500. What counts is what the answer says.
        if ($s.Name -eq "Chatterbox") {
            $health = try { Invoke-RestMethod $s.Health -TimeoutSec 5 } catch { $null }
            if ($health.status -eq "error") {
                Write-Host ("  {0,-9} model failed to load: {1}" -f $s.Name, $health.initialization_error) -ForegroundColor Red
                Write-Host "            Run setup.ps1 again, or start with -Tts kokoro for the fast voices." -ForegroundColor Red
                $problems++
                $pending.Remove($s)
                continue
            }
            if ($health.status -ne "healthy") { continue }
        }
        Write-Host ("  {0,-9} ready" -f $s.Name) -ForegroundColor Green
        $pending.Remove($s)
    }
    if ($pending.Count -gt 0) { Start-Sleep -Seconds 2 }
}
foreach ($s in $pending) { Write-Host ("  {0,-9} NOT responding - restore its window to see the error" -f $s.Name) -ForegroundColor Red; $problems++ }

# Load the models now so the first question in a session isn't slow (the LLM cold load took ~60 s).
if (Test-Up $services[0].Health) {
    # Unity asks for $hexeModel: the same weights with a 4k context. At Ollama's default 32k
    # the 14B model takes 15 GB, which no longer fits beside SAiD and Kokoro on a 16 GB card;
    # it spills to the CPU and a two-sentence reply takes minutes. The OpenAI-style endpoint
    # Unity uses cannot set the context, so it goes in a Modelfile. Cheap to redo every start.
    $modelfile = Join-Path $env:TEMP "hexe.Modelfile"
    "FROM $llmModel`nPARAMETER num_ctx 4096" | Set-Content $modelfile -Encoding ASCII
    & $ollamaExe create $hexeModel -f $modelfile | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  LLM       could not build $hexeModel (error above). Is it pulled? Run setup.ps1, or: ollama pull $llmModel" -ForegroundColor Red
        $problems++
    } else {
        Write-Host "Loading $hexeModel into memory..."
        try {
            Invoke-RestMethod "http://127.0.0.1:11434/api/generate" -Method Post -ContentType "application/json" -TimeoutSec 300 `
                -Body (@{ model = $hexeModel; keep_alive = -1 } | ConvertTo-Json) | Out-Null
            Write-Host "  LLM       loaded" -ForegroundColor Green
        } catch {
            # Ollama says why in the body (out of memory, too old for this model); the status alone is just "500".
            Write-Host "  LLM       failed to load: $($_.Exception.Message) $($_.ErrorDetails.Message)" -ForegroundColor Red
            $problems++
        }
    }
}
if (Test-Up $services[1].Health) {
    Write-Host "Loading $sttModel..."
    # Downloads it on a fresh machine; answers "already exists" otherwise.
    try { Invoke-WebRequest "http://127.0.0.1:8000/v1/models/$sttModel" -Method Post -UseBasicParsing -TimeoutSec 600 | Out-Null }
    catch { Write-Host "  Whisper   download failed: $($_.Exception.Message) $($_.ErrorDetails.Message)" -ForegroundColor Red; $problems++ }
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

# Chatterbox speaks in voices cloned from a short sample; register the coach's and the
# witch's (made from Kokoro's af_bella and bf_isabella) once.
if ($Tts -eq "chatterbox" -and (Test-Up $services[2].Health)) {
    try {
        $known = @((Invoke-RestMethod "http://127.0.0.1:8880/voices" -TimeoutSec 10).voices | ForEach-Object { $_.name })
        foreach ($wav in Get-ChildItem (Join-Path $PSScriptRoot "chatterbox\voices") -Filter *.wav) {
            if ($known -contains $wav.BaseName) { continue }
            $code = curl.exe -s -o NUL -w "%{http_code}" -F "voice_name=$($wav.BaseName)" -F "voice_file=@$($wav.FullName)" -F "language=en" "http://127.0.0.1:8880/voices"
            if ($code -eq "200" -or $code -eq "201") { Write-Host "  voice     $($wav.BaseName) added" -ForegroundColor Green } else { Write-Host "  voice     $($wav.BaseName) upload returned HTTP $code" -ForegroundColor Red }
        }
    } catch { Write-Host "  Chatterbox voices could not be checked: $($_.Exception.Message)" -ForegroundColor Red }
}

if ($problems -gt 0) { Write-Host "`n$problems problem(s) above: the role-play will not run properly until they are sorted." -ForegroundColor Red }
else { Write-Host "`nDone. In Unity: open SampleScene, press Play, then Space to start (Esc ends, Tab ends a turn, F1 operator panel)." }
# Keep the window open when started from Explorer (right-click > Run with PowerShell).
$parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
if ((Get-Process -Id $parent -ErrorAction SilentlyContinue).Name -eq "explorer") { Read-Host "Press Enter to close" | Out-Null }
