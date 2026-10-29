"""Speech emotion recognition for the Hexe role-play: how did the visitor's line *sound*?

The reading analysis in Unity measures prosody (pitch, loudness, pace) and can only say that
a voice changed. This service adds a judgement from a model trained for exactly that:
emotion2vec+ (Ma et al. 2024, https://github.com/ddlBoJack/emotion2vec), a speech emotion
model that classifies an utterance into angry, disgusted, fearful, happy, neutral, sad,
surprised (plus "other" and "unknown"). Unity combines it with face cues from the webcam
tracker and the intended emotion of the line.

Runs on the CPU by default - an utterance takes a fraction of a second and the GPU is busy
with Whisper, the language model and the voices.

    python emotion_service.py                    # base model, :8920
    python emotion_service.py --model large --device cuda

    GET  /health                                 {"status": "ok", "model": ...}
    POST /emotion   body: a WAV file             {"label": "fearful", "scores": {"fearful": 0.61, ...}}
"""
import argparse
import io
import wave

import numpy as np
import uvicorn
from fastapi import FastAPI, HTTPException, Request

MODELS = {"base": "iic/emotion2vec_plus_base", "large": "iic/emotion2vec_plus_large"}
RATE = 16000
MIN_SECONDS = 0.4

app = FastAPI()
state = {"model": None, "name": None}


def read_wav(data: bytes) -> np.ndarray:
    """Mono float32 at 16 kHz from a PCM16 WAV (what Unity's WhisperClient.ToWav writes)."""
    try:
        with wave.open(io.BytesIO(data)) as w:
            if w.getsampwidth() != 2:
                raise HTTPException(400, "expected 16-bit PCM")
            rate, channels = w.getframerate(), w.getnchannels()
            pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
    except (wave.Error, EOFError) as ex:
        raise HTTPException(400, f"not a WAV file: {ex}")
    audio = pcm.astype(np.float32) / 32768.0
    if channels > 1:
        audio = audio.reshape(-1, channels).mean(axis=1)
    if rate != RATE and len(audio) > 1:
        positions = np.linspace(0, len(audio) - 1, int(len(audio) * RATE / rate))
        audio = np.interp(positions, np.arange(len(audio)), audio).astype(np.float32)
    return audio


def english(label: str) -> str:
    # The model's labels are bilingual, e.g. "生气/angry".
    return label.split("/")[-1].strip().lower()


@app.get("/health")
def health():
    return {"status": "ok", "model": state["name"]}


@app.post("/emotion")
async def emotion(request: Request):
    audio = read_wav(await request.body())
    if len(audio) < MIN_SECONDS * RATE:
        raise HTTPException(400, "too short")
    result = state["model"].generate(audio, granularity="utterance", extract_embedding=False)[0]
    scores = {english(l): round(float(s), 4) for l, s in zip(result["labels"], result["scores"])}
    label = max(scores, key=scores.get)
    return {"label": label, "scores": scores}


def main():
    ap = argparse.ArgumentParser(description="Speech emotion recognition (emotion2vec+) over HTTP")
    ap.add_argument("--model", choices=sorted(MODELS), default="base")
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--port", type=int, default=8920)
    args = ap.parse_args()

    from funasr import AutoModel   # slow import; after argument errors

    state["name"] = MODELS[args.model]
    state["model"] = AutoModel(model=state["name"], hub="hf", device=args.device, disable_update=True)
    # The first inference initialises a lot lazily; do it now, not on the visitor's first line.
    state["model"].generate(np.zeros(RATE, dtype=np.float32), granularity="utterance", extract_embedding=False)
    print(f"emotion2vec+ {args.model} ready on {args.device}, http://127.0.0.1:{args.port}/emotion")
    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
