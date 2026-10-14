"""HTTP wrapper around SAiD so Unity can request blendshape curves for a piece of speech.

The model is loaded once at startup - loading it per request costs ~14s, versus ~3s of
actual inference, so a persistent service is the difference between usable and not.

POST /generate  (multipart: audio=<wav file>, optional num_steps, guidance_scale)
    -> {"fps": 60, "names": [...32 ARKit names...], "frames": [[f0...f31], ...]}

GET  /health -> {"status": "ok", "device": "cuda:0"}
"""
import io
import time
import logging

import librosa
import numpy as np
import torch
import soundfile as sf
from diffusers import DDIMScheduler
from fastapi import FastAPI, File, Form, UploadFile
from fastapi.responses import JSONResponse

from said.model.diffusion import SAID_UNet1D
from said.util.audio import fit_audio_unet

logging.basicConfig(level=logging.INFO, format="%(asctime)s | %(levelname)s | %(message)s")
log = logging.getLogger("said-service")

WEIGHTS_PATH = "SAiD.pth"
DEVICE = "cuda:0" if torch.cuda.is_available() else "cpu"
FPS = 60

app = FastAPI(title="SAiD blendshape service")
model = None

# Same list (and order) BlendVOCADataset.default_blendshape_classes provides; read from
# the data file so the service doesn't need SAiD's training-side `dataset` package.
with open("data/ARKit_blendshapes.txt", encoding="utf-8") as f:
    blendshape_names = [line.strip() for line in f if line.strip()]


@app.on_event("startup")
def load_model():
    global model
    t0 = time.time()
    log.info("Loading SAiD weights onto %s ...", DEVICE)
    model = SAID_UNet1D(noise_scheduler=DDIMScheduler, feature_dim=-1, prediction_type="epsilon")
    model.load_state_dict(torch.load(WEIGHTS_PATH, map_location=DEVICE, weights_only=True))
    model.to(DEVICE)
    model.eval()
    log.info("Model ready in %.1fs (%d blendshapes @ %d fps)", time.time() - t0, len(blendshape_names), FPS)


@app.get("/health")
def health():
    return {"status": "ok" if model is not None else "loading", "device": DEVICE, "fps": FPS}


@app.get("/names")
def names():
    return {"names": blendshape_names, "fps": FPS}


def _extract_prosody(waveform: np.ndarray, sr: int, n_frames: int) -> dict:
    """F0 and loudness on the same frame grid as the blendshape curves.

    Eyebrow raises track F0 rather than loudness, and precede pitch rises by roughly
    60ms - so the upper face needs the pitch contour, not the mouth-open envelope that
    the blendshape curves already imply. Rigid head motion is likewise strongly
    predicted by F0. Both are returned normalized to 0-1 over the utterance, since
    consumers want "high for this speaker, in this sentence" rather than absolute Hz.
    """
    hop = max(1, int(round(sr / FPS)))

    # yin, not pyin: pyin's Viterbi decoding costs ~25s on a 5s clip, which would dwarf
    # the ~1.4s of actual blendshape generation. yin is a plain autocorrelation estimator
    # and runs in tens of milliseconds. It has no voicing decision of its own, so voicing
    # is derived from energy below - unvoiced frames must not read as "pitch fell to zero"
    # or the brows twitch down on every consonant.
    # Search range and frame length both dominate yin's cost. C3-C6 covers a synthetic
    # speaking voice with headroom; widening it to the full C2-C7 musical range roughly
    # tripled generation time for no useful contour detail. frame_length only needs to
    # span ~2 periods of fmin (131 Hz -> ~370 samples at 24 kHz), so 1024 is ample.
    f0 = librosa.yin(
        waveform,
        fmin=float(librosa.note_to_hz("C3")),
        fmax=float(librosa.note_to_hz("C6")),
        sr=sr,
        frame_length=1024,
        hop_length=hop,
    )

    rms = librosa.feature.rms(y=waveform, frame_length=1024, hop_length=hop)[0]

    # Energy-based voicing: frames well below the clip's own loudness range are silence or
    # unvoiced consonants, where yin's estimate is meaningless.
    rms_ref = float(np.percentile(rms, 90))
    voiced_flag = rms > max(rms_ref * 0.15, 1e-5)

    def fit(arr):
        arr = np.asarray(arr, dtype=np.float32)
        if arr.shape[0] == n_frames:
            return arr
        idx = np.linspace(0, arr.shape[0] - 1, n_frames)
        return np.interp(idx, np.arange(arr.shape[0]), arr).astype(np.float32)

    # Work in semitones: pitch is perceived logarithmically, and a linear Hz scale would
    # make high-pitched passages dominate the brow response.
    voiced = np.isfinite(f0) & voiced_flag
    if voiced.any():
        semis = np.full_like(f0, np.nan, dtype=np.float32)
        median = float(np.nanmedian(f0[voiced]))
        semis[voiced] = 12.0 * np.log2(f0[voiced] / max(median, 1e-6))
        # Hold the last voiced value through unvoiced gaps so the contour is continuous.
        last = 0.0
        filled = np.empty_like(semis)
        for i, v in enumerate(semis):
            if np.isfinite(v):
                last = float(v)
            filled[i] = last
        lo, hi = np.percentile(filled, 5), np.percentile(filled, 95)
        span = max(hi - lo, 1e-3)
        f0_norm = np.clip((filled - lo) / span, 0.0, 1.0)
    else:
        f0_norm = np.zeros_like(rms)

    rms_norm = rms / max(float(rms.max()), 1e-6)

    return {
        "f0": np.round(fit(f0_norm), 4).tolist(),
        "loudness": np.round(fit(rms_norm), 4).tolist(),
        "voiced": fit(voiced.astype(np.float32)).round(3).tolist(),
    }


def _load_wav_mono(raw: bytes, target_sr: int) -> np.ndarray:
    """Decode WAV bytes to mono float32 at target_sr.

    SAiD's own loader takes a path; we get bytes over HTTP, so decode in-memory and
    resample here rather than round-tripping through a temp file on every request.
    """
    data, sr = sf.read(io.BytesIO(raw), dtype="float32", always_2d=True)
    mono = data.mean(axis=1)
    if sr != target_sr:
        duration = mono.shape[0] / sr
        n_target = int(round(duration * target_sr))
        # Linear resample is fine here: wav2vec2 features are robust to it, and this
        # avoids pulling librosa's much slower high-quality resampler into the hot path.
        mono = np.interp(
            np.linspace(0.0, mono.shape[0] - 1, n_target),
            np.arange(mono.shape[0]),
            mono,
        ).astype("float32")
    return mono


@app.post("/generate")
async def generate(
    audio: UploadFile = File(...),
    num_steps: int = Form(50),
    guidance_scale: float = Form(2.0),
):
    if model is None:
        return JSONResponse({"error": "model still loading"}, status_code=503)

    t0 = time.time()
    raw = await audio.read()

    try:
        waveform_np = _load_wav_mono(raw, model.sampling_rate)
    except Exception as exc:
        return JSONResponse({"error": f"could not decode audio: {exc}"}, status_code=400)

    waveform = torch.from_numpy(waveform_np)
    fit = fit_audio_unet(waveform, model.sampling_rate, FPS, 1)
    processed = model.process_audio(fit.waveform).to(DEVICE)

    with torch.no_grad():
        out = model.inference(
            waveform_processed=processed,
            num_inference_steps=num_steps,
            guidance_scale=guidance_scale,
            show_process=False,
        )

    coeffs = out.result[0, : fit.window_size].cpu().numpy()

    try:
        prosody = _extract_prosody(waveform_np, model.sampling_rate, coeffs.shape[0])
    except Exception as exc:
        # Prosody is an enhancement, not a requirement - a failure here should cost the
        # upper-face nuance, not the whole utterance's mouth animation.
        log.warning("prosody extraction failed: %s", exc)
        prosody = None

    elapsed = time.time() - t0
    audio_secs = len(waveform_np) / model.sampling_rate
    log.info(
        "generated %d frames (%.2fs of audio) in %.2fs [%d steps, realtime x%.1f]",
        coeffs.shape[0], audio_secs, elapsed, num_steps, audio_secs / max(elapsed, 1e-6),
    )

    result = {
        "fps": FPS,
        "names": blendshape_names,
        "frames": np.round(coeffs, 4).tolist(),
        "audio_seconds": round(audio_secs, 3),
        "generation_seconds": round(elapsed, 3),
    }
    if prosody is not None:
        result["prosody"] = prosody
    return result


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8900)
