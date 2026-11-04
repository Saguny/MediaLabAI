"""Chatterbox Turbo for the Hexe role-play: an expressive voice that keeps up with a conversation.

Turbo (Resemble AI, https://github.com/resemble-ai/chatterbox) generates faster than real time
on the GPU the role-play shares with its language model, where full Chatterbox takes about
twice as long as the speech it makes. It also understands [laugh], [chuckle], [sigh] and the like
in the text. Speaks in voices cloned from the short samples in chatterbox/voices.

Every line is kept on disk. The play is mostly scripted, so after the first time a line comes
back at once, also after a restart; only what the language model improvises is generated live.

    python turbo_service.py --voices <folder of voice samples>      # :8880

    GET  /health                                                    {"status": "healthy"}
    POST /v1/audio/speech  {"input": "Hello!", "voice": "af_bella"} a 16-bit PCM WAV

The request is the OpenAI one Unity already sends to Kokoro. Other fields it carries (speed,
exaggeration, cfg_weight) are ignored: Turbo has no such controls.
"""
import argparse
import hashlib
import io
import threading
import wave
from pathlib import Path

import numpy as np
import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel

REPO = "ResembleAI/chatterbox-turbo"
FILES = ["*.safetensors", "*.json", "*.txt", "*.pt", "*.model"]
CACHE = Path("cache")

app = FastAPI()
state = {"model": None, "voices": {}, "fallback": None}
gpu = threading.Lock()


class Speech(BaseModel):
    input: str
    voice: str = ""


def to_wav(samples: np.ndarray, rate: int) -> bytes:
    pcm = (np.clip(samples, -1.0, 1.0) * 32767.0).astype(np.int16)
    out = io.BytesIO()
    with wave.open(out, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())
    return out.getvalue()


@app.get("/health")
def health():
    return {"status": "healthy"}


@app.post("/v1/audio/speech")
def speech(request: Speech):
    text = request.input.strip()
    if not text:
        raise HTTPException(400, "no text")
    voice = request.voice if request.voice in state["voices"] else state["fallback"]
    conds, sample_hash = state["voices"][voice]

    key = hashlib.sha256(f"{REPO}|{sample_hash}|{text}".encode("utf-8")).hexdigest()
    cached = CACHE / f"{key}.wav"
    if cached.exists():
        return Response(cached.read_bytes(), media_type="audio/wav")

    model = state["model"]
    with gpu:
        model.conds = conds
        audio = model.generate(text)
    data = to_wav(audio.squeeze().cpu().numpy(), model.sr)

    # Through a temporary name, so that a crash mid-write can't leave half a line in the cache.
    partial = cached.with_suffix(".part")
    partial.write_bytes(data)
    partial.replace(cached)
    return Response(data, media_type="audio/wav")


def main():
    ap = argparse.ArgumentParser(description="Chatterbox Turbo over an OpenAI-style HTTP API")
    ap.add_argument("--voices", required=True, help="folder of WAV samples, one per voice, named after it")
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--port", type=int, default=8880)
    args = ap.parse_args()

    from chatterbox.tts_turbo import ChatterboxTurboTTS   # slow import; after argument errors
    from huggingface_hub import snapshot_download

    CACHE.mkdir(exist_ok=True)
    # from_pretrained insists on a Hugging Face login, which these public files don't need.
    model = ChatterboxTurboTTS.from_local(snapshot_download(REPO, token=False, allow_patterns=FILES), device=args.device)
    state["model"] = model

    for sample in sorted(Path(args.voices).glob("*.wav")):
        model.prepare_conditionals(str(sample))
        state["voices"][sample.stem] = (model.conds, hashlib.sha256(sample.read_bytes()).hexdigest())
    if not state["voices"]:
        raise SystemExit(f"no voice samples in {args.voices}")
    state["fallback"] = next(iter(state["voices"]))

    # The first generation initialises a lot lazily (~20 s); do it now, not on the visitor's first line.
    model.generate("Warming up the voice.")
    print(f"Chatterbox Turbo ready, voices: {', '.join(state['voices'])}, http://127.0.0.1:{args.port}/v1/audio/speech")
    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
