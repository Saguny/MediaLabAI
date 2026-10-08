using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ChatdollKit.Extension.OpenAI;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

// Exercises the reading analysis end to end without a microphone: Hansel's lines are
// synthesized twice - once read plainly, once from the scenario's expressive modelText - then
// transcribed with Whisper, measured, summarised and compared exactly as a visitor's takes
// would be. Useful for checking the services and for tuning ReadingFeedback thresholds.
public static class HexeAnalysisHarness
{
    [MenuItem("Tools/Hexe Role-Play/Debug/Analyze Synthetic Takes")]
    public static void Run()
    {
        var scenario = HexeScenario.Load("hexe_scenario.json");
        var tts = Object.FindObjectOfType<OpenAITTSLoader>();
        var stt = Object.FindObjectOfType<OpenAIVoiceRequestProvider>();
        var director = Object.FindObjectOfType<HexeDirector>();
        if (scenario == null || tts == null || stt == null)
        {
            Debug.LogError("HexeAnalysisHarness: needs the scenario, an OpenAITTSLoader and an OpenAIVoiceRequestProvider in the scene.");
            return;
        }
        var thresholds = director != null ? director.Thresholds : new ReadingFeedback.Thresholds();
        var students = scenario.scene.Where(l => l.IsStudent).ToList();
        var report = new StringBuilder();

        var takes = new List<ReadingFeedback.TakeSummary>();
        var variants = new[]
        {
            ("plain", "am_michael", 1.05f, false),
            ("expressive", "am_puck", 1.0f, true),
        };
        try
        {
            foreach (var (label, voice, speed, expressive) in variants)
            {
                var metrics = new List<ReadingAnalyzer.LineMetrics>();
                for (var i = 0; i < students.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Hexe analysis", $"{label} take, line {i + 1}", (takes.Count * students.Count + i) / (float)(students.Count * 2));
                    var text = expressive && !string.IsNullOrEmpty(students[i].modelText) ? students[i].modelText : students[i].text;
                    var wav = Post(tts.SpeechUrl, "application/json", Encoding.UTF8.GetBytes(
                        $"{{\"model\":\"tts-1\",\"input\":{Newtonsoft.Json.JsonConvert.ToString(text)},\"voice\":\"{voice}\",\"response_format\":\"wav\",\"speed\":{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"), tts.ApiKey);
                    var clip = StreamingWavDecoder.ToAudioClip(wav, "harness");
                    var samples = new float[clip.samples * clip.channels];
                    clip.GetData(samples, 0);
                    var rate = clip.frequency;
                    Object.DestroyImmediate(clip);

                    var form = new WWWForm();
                    form.AddField("model", stt.Model);
                    form.AddField("language", string.IsNullOrEmpty(stt.Language) ? "en" : stt.Language);
                    form.AddField("response_format", "verbose_json");
                    form.AddField("timestamp_granularities[]", "word");
                    form.AddBinaryData("file", WhisperClient.ToWav(samples, rate), "speech.wav", "audio/wav");
                    var json = PostForm(stt.TranscriptionUrl, form);
                    var result = new WhisperClient.Result();
                    WhisperClient.Parse(json, result);

                    var m = ReadingAnalyzer.Analyze(students[i].text, result, samples, rate, -70f);
                    metrics.Add(m);
                    report.AppendLine($"[{label}] line {i + 1}: acc={m.Accuracy:P0} wpm={m.Wpm:F0} pauses={m.Pauses.Count} hes={m.Hesitations} loud={m.LoudnessDb:F1}dB pitch={m.PitchSt:F1}st range={m.PitchRangeSt:F1}st heard=\"{m.RecognizedText}\"");
                }

                var summary = ReadingFeedback.Summarize(metrics, students, thresholds);
                takes.Add(summary);
                var plan = ReadingFeedback.PlanFirstFeedback(summary, thresholds);
                report.AppendLine($"[{label}] grid: volume {summary.Volume}, accuracy {summary.AccuracyLevel} ({summary.Accuracy:P0}), expression {summary.Expression}, speed {summary.Speed} ({summary.Wpm:F0} wpm)");
                report.AppendLine($"[{label}] variation {summary.Variation:F2}, fit {summary.Fit:F2}, pitch range {summary.PitchRangeSt:F1} st, line fits [{string.Join(", ", summary.LineFit.Select(f => f.ToString("F2")))}]");
                report.AppendLine($"[{label}] first-feedback plan: focus {plan.Focus}, positive {plan.PositiveKey}, observation {plan.ObservationKey}, question {plan.QuestionKey}, tip {plan.TipKey}");
                report.AppendLine();
            }

            var cmp = ReadingFeedback.Compare(takes[0], takes[1]);
            report.AppendLine($"comparison plain -> expressive: measurable {cmp.Measurable}, better {cmp.Better}, score {cmp.Score:F2}, changes [{string.Join(", ", cmp.ChangeKeys)}]");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        var path = Path.GetFullPath("Temp/hexe_analysis_report.txt");
        File.WriteAllText(path, report.ToString());
        Debug.Log("HexeAnalysisHarness:\n" + report + "\nWritten to " + path);
    }

    private static byte[] Post(string url, string contentType, byte[] body, string apiKey)
    {
        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", contentType);
            if (!string.IsNullOrEmpty(apiKey)) req.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            req.timeout = 60;
            var op = req.SendWebRequest();
            while (!op.isDone) System.Threading.Thread.Sleep(10);
            if (req.result != UnityWebRequest.Result.Success) throw new System.Exception($"{url}: {req.error}");
            return req.downloadHandler.data;
        }
    }

    private static string PostForm(string url, WWWForm form)
    {
        using (var req = UnityWebRequest.Post(url, form))
        {
            req.timeout = 60;
            var op = req.SendWebRequest();
            while (!op.isDone) System.Threading.Thread.Sleep(10);
            if (req.result != UnityWebRequest.Result.Success) throw new System.Exception($"{url}: {req.error}");
            return req.downloadHandler.text;
        }
    }
}
