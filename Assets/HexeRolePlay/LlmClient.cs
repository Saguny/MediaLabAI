using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// One-shot, non-streaming chat completion against an OpenAI-compatible endpoint (the local
// Ollama server). The role-play asks the model small, bounded questions - classify this
// answer, react to it in a sentence - and needs the whole reply before acting on it, so
// ChatdollKit's streaming skill pipeline is the wrong shape here.
public class LlmClient
{
    public string Url;
    public string ApiKey;
    public string Model;
    public float TimeoutSec = 8f;

    public bool IsConfigured => !string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(Model);

    // Returns null on any failure; every caller has a scripted fallback, so an unreachable
    // or slow model degrades the conversation rather than stopping it.
    public async UniTask<string> CompleteAsync(string system, string user, float temperature, int maxTokens, bool json, CancellationToken token)
    {
        if (!IsConfigured) return null;

        var body = new Dictionary<string, object>
        {
            { "model", Model },
            { "stream", false },
            { "temperature", temperature },
            { "max_tokens", maxTokens },
            { "messages", new[]
                {
                    new Dictionary<string, string> { { "role", "system" }, { "content", system } },
                    new Dictionary<string, string> { { "role", "user" }, { "content", user } }
                }
            }
        };
        if (json)
        {
            body["response_format"] = new Dictionary<string, string> { { "type", "json_object" } };
        }

        using (var req = new UnityWebRequest(Url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body)));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(ApiKey)) req.SetRequestHeader("Authorization", $"Bearer {ApiKey}");
            req.timeout = Mathf.CeilToInt(TimeoutSec);

            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: token);
                var root = JObject.Parse(req.downloadHandler.text);
                return ((string)root["choices"]?[0]?["message"]?["content"])?.Trim();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"LlmClient: request failed: {ex.Message}");
                return null;
            }
        }
    }

    // Pulls the first JSON object out of a reply, tolerating the prose or code fences a
    // small local model sometimes wraps around it even in JSON mode.
    public static JObject ExtractJson(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return null;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            return JObject.Parse(reply.Substring(start, end - start + 1));
        }
        catch
        {
            return null;
        }
    }
}
