using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using ChatdollKit.Model;
using ChatdollKit.IO;

namespace ChatdollKit.Extension.OpenAI
{
    public class OpenAITTSLoader : WebVoiceLoaderBase
    {
        public override VoiceLoaderType Type { get; } = VoiceLoaderType.TTS;
        public string _Name = "OpenAI";
        public override string Name
        {
            get
            {
                return _Name;
            }
        }
        public bool _IsDefault = true;
        public override bool IsDefault
        {
            get
            {
                return _IsDefault;
            }
            set
            {
                _IsDefault = value;
            }
        }

        public string ApiKey;
        public string Voice = "nova";
        public float Speed = 1.0f;
        public string SpeechUrl;

        public void Configure(string apiKey, string voice, bool overwrite = false)
        {
            ApiKey = string.IsNullOrEmpty(ApiKey) || overwrite ? apiKey : ApiKey;
            Voice = string.IsNullOrEmpty(Voice) || overwrite ? voice : Voice;
        }

        // See API document: https://platform.openai.com/docs/api-reference/audio/createSpeech
        protected override async UniTask<AudioClip> DownloadAudioClipAsync(Voice voice, CancellationToken token)
        {
            if (token.IsCancellationRequested) { return null; };

            if (string.IsNullOrEmpty(voice.Text.Replace(" ", "").Replace("\n", "").Trim()))
            {
                Debug.LogWarning("Query for OpenAI TTS is empty");
                return null;
            }

            var headers = new Dictionary<string, string>()
            {
                { "Authorization", $"Bearer {ApiKey}"  },
                { "Content-Type", "application/json" }
            };

            var request = new Dictionary<string, object>() {
                { "model", "tts-1" },
                { "input", voice.Text },
                { "voice", Voice },
                // WAV rather than mp3: SaidBlendshapePlayer feeds these same bytes to the
                // SAiD service, which decodes WAV, and Unity's own mp3 decoding has been
                // the flakier of the two. opus, aac and flac are not supported by Unity.
                { "response_format", "wav" },
                { "speed", Speed }
            };
            // Engine-specific extras per request (e.g. Chatterbox's exaggeration and
            // cfg_weight). Servers that do not know a field ignore it.
            if (voice.TTSConfig != null)
            {
                foreach (var kv in voice.TTSConfig.Params) request[kv.Key] = kv.Value;
            }
            var data = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(request));

            var url = string.IsNullOrEmpty(SpeechUrl) ? "https://api.openai.com/v1/audio/speech" : SpeechUrl;
#if UNITY_WEBGL && !UNITY_EDITOR
            return await DownloadAudioClipWebGLAsync(url, data, headers, token);
#else
            // The text travels with the request so the animation layer can spot laughter
            // before the audio exists, and prepare a laugh envelope for it.
            return await DownloadAudioClipNativeAsync(url, data, headers, token, voice.Text);
#endif
        }

        protected async UniTask<AudioClip> DownloadAudioClipNativeAsync(string url, byte[] data, Dictionary<string, string> headers, CancellationToken token, string spokenText = null)
        {
            // Plain byte download rather than UnityWebRequestMultimedia: Kokoro streams its
            // WAV responses with 0xFFFFFFFF placeholder chunk sizes, which FMOD refuses to
            // load. StreamingWavDecoder parses those bytes itself, and we need the raw
            // buffer anyway to hand the same audio to SAiD.
            using (var www = new UnityWebRequest(url, "POST"))
            {
                www.timeout = Timeout;
                www.downloadHandler = new DownloadHandlerBuffer();

                www.SetRequestHeader("Authorization", headers["Authorization"]);
                www.SetRequestHeader("Content-Type", headers["Content-Type"]);

                www.uploadHandler = new UploadHandlerRaw(data);

                await www.SendWebRequest().ToUniTask();

                if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError($"Error occured while processing text-to-speech voice: {www.error}");
                }
                else if (www.isDone)
                {
                    var wavBytes = www.downloadHandler.data;
                    var clip = StreamingWavDecoder.ToAudioClip(wavBytes, "tts");

                    // Generate the speech-driven face curves for this utterance now, while
                    // it is still queued - it plays seconds from now, and generation takes
                    // ~1.4s per 5s of speech. Fire-and-forget: if SAiD is down or slow the
                    // clip still plays, just without model-driven mouth animation.
                    var player = SaidBlendshapePlayer.Instance;
                    if (player != null && clip != null)
                    {
                        _ = player.PrepareForClipAsync(clip, wavBytes, spokenText);
                    }

                    return clip;
                }
            }
            return null;
        }

        protected async UniTask<AudioClip> DownloadAudioClipWebGLAsync(string url, byte[] data, Dictionary<string, string> headers, CancellationToken token)
        {
            Debug.LogWarning("Unity doesn't support playback MP3 on WebGL.");

            var resp = await client.PostBytesAsync(url, data, headers, cancellationToken: token);
            return AudioConverter.PCMToAudioClip(resp.Data, 1, 8000);
        }
    }
}
