using System;
using UnityEngine;

// Builds an AudioClip from raw WAV bytes without going through FMOD.
//
// Kokoro-FastAPI streams its WAV responses, which means the RIFF and `data` chunk size
// fields are written as 0xFFFFFFFF placeholders rather than real lengths - the writer
// doesn't know the total up front. Python's soundfile shrugs and reads to EOF, but
// UnityWebRequestMultimedia/FMOD rejects the file outright ("Error loading file"), so we
// parse it ourselves and treat a placeholder size as "rest of the buffer".
public static class StreamingWavDecoder
{
    public static AudioClip ToAudioClip(byte[] wav, string clipName = "tts")
    {
        if (wav == null || wav.Length < 44)
        {
            Debug.LogError("StreamingWavDecoder: buffer too small to be a WAV.");
            return null;
        }

        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
            wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
        {
            Debug.LogError("StreamingWavDecoder: not a RIFF/WAVE buffer.");
            return null;
        }

        int channels = 1;
        int sampleRate = 24000;
        int bitsPerSample = 16;
        int audioFormat = 1;
        int dataOffset = -1;
        int dataLength = 0;

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var chunkId = new string(new[] { (char)wav[pos], (char)wav[pos + 1], (char)wav[pos + 2], (char)wav[pos + 3] });
            var chunkSize = BitConverter.ToInt32(wav, pos + 4);
            var body = pos + 8;

            if (chunkId == "fmt ")
            {
                audioFormat = BitConverter.ToInt16(wav, body);
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bitsPerSample = BitConverter.ToInt16(wav, body + 14);
            }
            else if (chunkId == "data")
            {
                dataOffset = body;
                // Streamed writers put -1 (0xFFFFFFFF) here; take everything that's left.
                dataLength = (chunkSize < 0 || chunkSize > wav.Length - body) ? wav.Length - body : chunkSize;
                break;
            }

            if (chunkSize < 0 || chunkSize > wav.Length - body) break;
            pos = body + chunkSize + (chunkSize % 2);
        }

        if (dataOffset < 0 || dataLength <= 0)
        {
            Debug.LogError("StreamingWavDecoder: no usable data chunk found.");
            return null;
        }

        if (audioFormat != 1)
        {
            Debug.LogError($"StreamingWavDecoder: only PCM is supported, got audioFormat={audioFormat}.");
            return null;
        }

        float[] samples;
        if (bitsPerSample == 16)
        {
            var count = dataLength / 2;
            samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToInt16(wav, dataOffset + i * 2) / 32768f;
            }
        }
        else if (bitsPerSample == 32)
        {
            var count = dataLength / 4;
            samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToSingle(wav, dataOffset + i * 4);
            }
        }
        else if (bitsPerSample == 8)
        {
            samples = new float[dataLength];
            for (var i = 0; i < dataLength; i++)
            {
                samples[i] = (wav[dataOffset + i] - 128) / 128f;
            }
        }
        else
        {
            Debug.LogError($"StreamingWavDecoder: unsupported bit depth {bitsPerSample}.");
            return null;
        }

        var frames = samples.Length / Mathf.Max(1, channels);
        if (frames <= 0)
        {
            Debug.LogError("StreamingWavDecoder: decoded zero frames.");
            return null;
        }

        var clip = AudioClip.Create(clipName, frames, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
