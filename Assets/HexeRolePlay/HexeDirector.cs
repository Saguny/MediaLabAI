using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using ChatdollKit.Dialog;
using ChatdollKit.IO;
using ChatdollKit.LLM.ChatGPT;
using ChatdollKit.Extension.OpenAI;
using ChatdollKit.Model;
using UnityEngine;

// Runs the Hansel-and-Gretel reading role-play from greeting to goodbye.
//
//   1  Intro          coach mode: short questions, each waiting for an answer
//   2  Scene, take 1  the avatar turns into the witch; the visitor reads Hansel
//   3  Cut            the avatar steps out of the role, visibly and audibly
//   4  Feedback       one observation, one question about the character, one tip
//   5  Scene, take 2  "Let's try again. I start!"
//   6  Comparison     what changed between the takes, stated only if it was measured
//   7  Debrief        what the visitor was training, in short turns
//   8  Playback       both takes, as GameLet's Recording Studio would
//
// Design rules, from the project brief:
//   - Hansel reads more than the witch speaks; the witch gets short cue lines.
//   - Explanation and role-play never mix. Every switch goes through a costume change.
//   - Short sentences, many turns. Every question waits for an answer - rhetorical ones too.
//   - Feedback is grounded: observations only when the analysis supports them, otherwise a
//     question. The LLM understands and reacts to the visitor, but never makes the
//     observations up; those come from ReadingFeedback.
//
// The scripted spine keeps the scene reliable. The LLM handles what cannot be scripted -
// unexpected answers, questions, natural reactions - and every LLM call has a fallback, so
// the play still runs if the model is slow or offline.
public class HexeDirector : MonoBehaviour
{
    [Header("References (filled by Tools > Hexe Role-Play > Set Up Scene)")]
    public ChatdollKit.ChatdollKit Chatdoll;
    public DialogController Dialog;
    public ModelController Model;
    public ChatdollMicrophone Microphone;
    public AvatarPerformer Performer;
    public SpeechCapture Capture;
    public SceneRecorder Recorder;
    public HexeStageUI UI;
    public StageEffects Stage;

    [Header("Scenario")]
    public string ScenarioFile = "hexe_scenario.json";

    [Header("Starting a session")]
    [Tooltip("Start the role-play when ChatdollKit's wake word is heard, instead of ChatdollKit's own chat.")]
    public bool StartOnWakeWord = true;
    public KeyCode StartKey = KeyCode.Space;
    public KeyCode AbortKey = KeyCode.Escape;
    public KeyCode EndTurnKey = KeyCode.Tab;
    public bool AutoStartOnPlay = false;

    [Header("Listening")]
    public float AnswerTimeout = 8f;
    // People answer within a few hundred milliseconds of each other (Stivers et al. 2009), so
    // waiting out a fixed second of silence before even starting to transcribe is already
    // slower than any human. Instead a short pause triggers transcription, and the turn
    // ends there unless the words so far sound unfinished ("and...", "because", "um").
    [Tooltip("A pause this long in an answer starts transcribing it and ends the turn if it sounds finished.")]
    public float AnswerPauseCheck = 0.3f;
    [Tooltip("A pause this long ends an answer regardless, even mid-thought.")]
    public float AnswerHardEndSilence = 1.3f;
    public float AnswerMaxDuration = 15f;
    [Tooltip("A pause this long while reading triggers a check of whether the line is finished.")]
    public float ReadingPauseCheck = 0.5f;
    [Tooltip("A pause this long while reading ends the line regardless.")]
    public float ReadingHardEndSilence = 4f;
    public float ReadingNoSpeechTimeout = 10f;
    public float ReadingMaxDuration = 30f;
    [Range(0.3f, 1f)] public float ReadingCompleteCoverage = 0.75f;
    [Tooltip("Seconds after the avatar stops speaking before listening starts, so the tail of her own voice is not recorded.")]
    public float ListenDelay = 0.25f;

    [Header("Feedback")]
    public ReadingFeedback.Thresholds Thresholds = new ReadingFeedback.Thresholds();
    public bool ShowCriteriaCard = true;
    public bool PlaybackBothTakes = true;

    [Header("Language model")]
    public bool UseLlm = true;
    public float LlmTimeoutSec = 8f;
    [Tooltip("Answers longer than this many words go to the model even if they contain a yes or no, so what the visitor actually said gets a reaction.")]
    public int RuleMaxWords = 4;
    [Tooltip("How often per line the witch may react in character to something that is not the line, before the scene simply moves on.")]
    public int MaxOffScriptReplies = 2;
    [Tooltip("If the model has not answered by then, a short acknowledgement fills the gap.")]
    public float AckAfterSeconds = 0.5f;

    [Header("Operator")]
    [Tooltip("F1 toggles a small panel for typing answers, forcing the end of a turn and watching the analysis.")]
    public bool ShowOperatorPanel = true;

    public bool IsRunning { get; private set; }
    public string Phase { get; private set; } = "Idle";

    private HexeScenario scenario;
    private WhisperClient whisper;
    private LlmClient llm;
    private CancellationTokenSource sessionCts;
    private bool recordingAllowed;
    private readonly List<ReadingFeedback.TakeSummary> takes = new List<ReadingFeedback.TakeSummary>();
    private bool readingActive;
    private string lastHeard = "";
    private string operatorText = "";
    private string llmStatus = "checking...";
    private SocialReactions reactions;

    private class StopRequested : Exception { }

    private enum Intent { Silent, Yes, No, DontKnow, Question, Stop, Other }

    private class Answer
    {
        public string Text = "";
        public Intent Intent = Intent.Silent;
        public List<string> Emotions = new List<string>();
        public Intent RuleIntent = Intent.Other;   // what the keyword rules made of it, kept as the fallback
        public string Reply;   // the model's suggested reaction, if it was consulted
        public bool Silent => Intent == Intent.Silent;
    }

    // ================================================================== lifecycle

    private void Start()
    {
        scenario = HexeScenario.Load(ScenarioFile);
        if (scenario == null)
        {
            enabled = false;
            return;
        }

        var stt = Chatdoll != null ? Chatdoll.GetComponent<OpenAIVoiceRequestProvider>() : null;
        whisper = new WhisperClient
        {
            Url = stt != null ? stt.TranscriptionUrl : null,
            ApiKey = stt != null ? stt.ApiKey : null,
            Model = stt != null ? stt.Model : "whisper-1",
            Language = stt != null && !string.IsNullOrEmpty(stt.Language) ? stt.Language : "en"
        };

        var gpt = Chatdoll != null ? Chatdoll.GetComponent<ChatGPTService>() : null;
        llm = new LlmClient
        {
            Url = gpt != null ? (string.IsNullOrEmpty(gpt.ChatCompletionUrl) ? "https://api.openai.com/v1/chat/completions" : gpt.ChatCompletionUrl) : null,
            ApiKey = gpt != null ? gpt.ApiKey : null,
            Model = gpt != null ? gpt.Model : null,
            TimeoutSec = LlmTimeoutSec
        };

        if (Chatdoll != null && StartOnWakeWord)
        {
            // Replace ChatdollKit's "start a chat" reaction to the wake word with the play.
            Chatdoll.OnWakeAsync = (wakeWord) =>
            {
                if (!IsRunning) StartSession();
                return UniTask.CompletedTask;
            };
        }

        // Stopping the wake word listener mutes the microphone as it winds down, which
        // would cut into a turn being recorded. Keep it from muting while a session runs.
        if (Dialog != null && Dialog.WakeWordListener is VoiceRecorderBase wakeRecorder)
        {
            wakeRecorder.MuteOnListeningStop = () => !IsRunning;
        }

        // The avatar must not hear itself: with speakers, its voice would be recorded as the visitor's.
        if (Capture != null)
        {
            Capture.IsOutputPlaying = () => (Performer != null && Performer.IsVoicePlaying)
                                            || (Stage != null && Stage.IsCuePlaying)
                                            || (Recorder != null && Recorder.IsPlaying);
        }

        // Nods and brow flashes follow the same voice detector the turns use.
        reactions = FindObjectOfType<SocialReactions>();
        if (reactions != null && Capture != null) reactions.VoiceActive = () => Capture.UserVoiceActive;

        UI?.ShowStartPrompt(scenario.Line("attract"));
        Performer?.SetFace("Smile");

        // Synthesize every scripted line, voice and lip-sync, while the stage idles. A line
        // synthesized when it is due costs a TTS round trip plus SAiD's generation before
        // the first word - seconds of dead air after every answer. Waits a frame for
        // AvatarPerformer.Start to create its TTS loaders.
        UniTask.Void(async () =>
        {
            var token = this.GetCancellationTokenOnDestroy();
            await UniTask.Yield(token);
            if (Performer != null) await Performer.WarmUpAsync(ScriptedLines(), token);
        });
        CheckLlmAsync(this.GetCancellationTokenOnDestroy()).Forget();

        if (AutoStartOnPlay)
        {
            // Give the other components their Start first (TTS loaders, wake word listener).
            UniTask.Void(async () =>
            {
                await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: this.GetCancellationTokenOnDestroy());
                StartSession();
            });
        }
    }

    private void Update()
    {
        if (!IsRunning && Input.GetKeyDown(StartKey) && !OperatorTyping()) StartSession();
        if (IsRunning && Input.GetKeyDown(AbortKey)) sessionCts?.Cancel();
        if (IsRunning && Input.GetKeyDown(EndTurnKey)) Capture?.ForceEnd();
        if (Input.GetKeyDown(KeyCode.F1)) ShowOperatorPanel = !ShowOperatorPanel;

        if (readingActive && UI != null && Capture != null)
        {
            UI.SetScriptState(Capture.IsVoiceActive ? HexeStageUI.ScriptState.Listening : HexeStageUI.ScriptState.YourTurn);
        }
    }

    private void OnDestroy()
    {
        sessionCts?.Cancel();
        ArkitFaceAnimator.ExternalMode = null;
    }

    public void StartSession()
    {
        if (IsRunning || scenario == null) return;
        sessionCts = new CancellationTokenSource();
        RunSessionAsync(sessionCts.Token).Forget();
    }

    public void AbortSession() => sessionCts?.Cancel();

    private async UniTask RunSessionAsync(CancellationToken token)
    {
        IsRunning = true;
        takes.Clear();
        Recorder?.Clear();
        recordingAllowed = false;
        UI?.ShowStartPrompt(null);

        try
        {
            // Take the microphone and the floor from ChatdollKit's own dialog loop for the duration.
            if (Dialog != null)
            {
                if (Dialog.WakeWordListener != null && Dialog.WakeWordListener.IsListening) Dialog.WakeWordListener.StopListening();
                Dialog.enabled = false;
            }
            if (Microphone != null) Microphone.IsMuted = false;
            Stage?.SetFraming(StageEffects.Framing.Coach);

            await IntroAsync(token);

            ReadingFeedback.TakeSummary previous = null;
            var rounds = Mathf.Clamp(scenario.maxRounds, 1, 3);
            for (var round = 1; round <= rounds; round++)
            {
                if (round == 1) await SayKey("enterRole", token);
                await EnterSceneAsync(token);
                var take = await PlaySceneAsync(round, token);
                takes.Add(take);
                await ExitSceneAsync(token);

                if (previous == null)
                {
                    await FirstFeedbackAsync(take, token);
                }
                else
                {
                    await CompareFeedbackAsync(previous, take, token);
                }
                previous = take;

                if (round < rounds) await SayKey("enterRoleAgain", token);
            }

            UI?.HideCriteria();
            await DebriefAsync(token);
            await PlaybackAsync(token);
            await SayKey("goodbye", token);
        }
        catch (StopRequested)
        {
            await SafeSay("earlyGoodbye");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("HexeDirector: session aborted.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"HexeDirector: session failed: {ex}");
            await SafeSay("technicalProblem");
        }
        finally
        {
            await EndSessionAsync();
        }
    }

    private async UniTask SafeSay(string key)
    {
        try
        {
            if (Performer != null && Performer.IsWitch) await Performer.TransformAsync(false, false, CancellationToken.None);
            UI?.SetSceneFrame(false, "");
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await SayKey(key, cts.Token);
            }
        }
        catch (Exception ex) when (!(ex is StopRequested))
        {
            Debug.LogWarning($"HexeDirector: could not say '{key}': {ex.Message}");
        }
    }

    private async UniTask EndSessionAsync()
    {
        // Leaving Play mode mid-session: the UI is already gone.
        if (this == null) return;
        readingActive = false;
        Performer?.StopSpeaking();
        UI?.SetSceneFrame(false, "");
        UI?.HideCriteria();
        UI?.SetListening(false);
        UI?.SetRecording(false);
        UI?.SetPlayback(null, 0f);
        UI?.SetSubtitle("");
        Stage?.SetSceneMode(false);
        Stage?.SetFraming(StageEffects.Framing.Original);
        if (Performer != null)
        {
            Performer.SetWitch(false);
            Performer.SetFace("Smile");
            if (Stage != null) await Stage.SetHatAsync(false, CancellationToken.None);
        }
        Recorder?.Clear();
        ArkitFaceAnimator.ExternalMode = null;
        if (Dialog != null) Dialog.enabled = true;
        Phase = "Idle";
        IsRunning = false;
        UI?.ShowStartPrompt(scenario.Line("attract"));
    }

    // Every line the performer may speak from the script, the opening first and the scene
    // next, so a visitor arriving mid warm-up still finds the start ready.
    private IEnumerable<(string, AvatarPerformer.Voice)> ScriptedLines()
    {
        var coach = AvatarPerformer.Voice.Coach;
        var witch = AvatarPerformer.Voice.Witch;
        var first = new[] { "greet", "whoami" };
        foreach (var key in first.Where(scenario.Has)) foreach (var v in scenario.lines[key]) yield return (scenario.Fill(v), coach);
        foreach (var l in scenario.scene.Where(l => !l.IsStudent)) yield return (l.text, witch);
        foreach (var kv in scenario.lines)
        {
            if (first.Contains(kv.Key) || kv.Key == "attract") continue;
            var voice = kv.Key.StartsWith("witch") ? witch : coach;
            foreach (var v in kv.Value) yield return (scenario.Fill(v), voice);
        }
        foreach (var l in scenario.scene.Where(l => !string.IsNullOrEmpty(l.modelText))) yield return (l.modelText, coach);
    }

    // A missing model fails every request quietly into the scripted fallbacks, which looks
    // exactly like the model being ignored. Say so loudly, once, and load it while at it.
    private async UniTask CheckLlmAsync(CancellationToken token)
    {
        if (!UseLlm || !llm.IsConfigured)
        {
            llmStatus = "off";
            return;
        }
        var started = Time.realtimeSinceStartup;
        // Its own client: a cold model takes up to a minute to load, far past a turn's timeout.
        var ping = new LlmClient { Url = llm.Url, ApiKey = llm.ApiKey, Model = llm.Model, TimeoutSec = 120f };
        string reply;
        try { reply = await ping.CompleteAsync("Reply with OK.", "ping", 0f, 2, false, token); }
        catch (OperationCanceledException) { return; }
        if (reply == null)
        {
            llmStatus = $"FAILED ({llm.Model})";
            Debug.LogError($"HexeDirector: the language model '{llm.Model}' at {llm.Url} does not answer. Free answers and the witch's improvising fall back to scripted lines. Is Ollama running, and is the model pulled (ollama pull {llm.Model})?");
            return;
        }
        llmStatus = $"ok, {Time.realtimeSinceStartup - started:F1}s cold";
        Debug.Log($"HexeDirector: language model '{llm.Model}' answers ({llmStatus}).");
    }

    // ================================================================== phases

    private async UniTask IntroAsync(CancellationToken token)
    {
        Phase = "Intro";
        await SayKey("greet", token);
        await SayKey("whoami", token);

        var fairy = await AskAsync("askFairy", token);
        await React(fairy, "fairyYes", "fairyNo", "fairyOther", token);

        var story = await AskAsync("askKnowStory", token);
        await SayReply(story, token);
        if (story.Intent == Intent.Yes) await SayKey("storyYes", token);
        else await SayKey("storyNo", token);

        await SayKey("roles", token);
        UI?.SetScript("", scenario.scene.FirstOrDefault(l => l.IsStudent)?.text, scenario.studentRole, 1, StudentLines().Count, HexeStageUI.ScriptState.Done);
        UI?.SetSceneFrame(true, scenario.sceneTitle);
        await SayKey("scriptPanel", token);
        UI?.SetSceneFrame(false, scenario.sceneTitle);

        var record = await AskAsync("askRecord", token);
        recordingAllowed = record.Intent == Intent.Yes;
        await SayReply(record, token);
        await SayKey(recordingAllowed ? "recordYes" : "recordNo", token);

        await SayKey("situation", token);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ready = await AskAsync("askReady", token);
            await SayReply(ready, token);
            if (ready.Intent != Intent.No) break;
            await SayKey("readyNo", token);
        }
    }

    private async UniTask EnterSceneAsync(CancellationToken token)
    {
        Phase = "Entering scene";
        Performer?.SetFace("Smile");
        Stage?.SetFraming(StageEffects.Framing.Scene);
        if (Performer != null) await Performer.TransformAsync(true, true, token);
        UI?.SetRecording(recordingAllowed);
        UI?.SetSceneFrame(true, scenario.sceneTitle);
        await UniTask.Delay(TimeSpan.FromSeconds(0.4f), cancellationToken: token);
    }

    private async UniTask ExitSceneAsync(CancellationToken token)
    {
        Phase = "Cut";
        UI?.SetSceneFrame(false, scenario.sceneTitle);
        UI?.SetRecording(false);
        Stage?.PlayCue(StageEffects.Cue.Cut);
        await SayKey("cut", token);
        Stage?.SetFraming(StageEffects.Framing.Coach);
        if (Performer != null) await Performer.TransformAsync(false, false, token);
    }

    private async UniTask<ReadingFeedback.TakeSummary> PlaySceneAsync(int round, CancellationToken token)
    {
        Phase = $"Scene, take {round}";
        if (recordingAllowed) Recorder?.BeginTake();

        var studentLines = StudentLines();
        var metrics = new List<ReadingAnalyzer.LineMetrics>();
        var lastWitch = string.Empty;
        var studentIndex = 0;

        for (var i = 0; i < scenario.scene.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var line = scenario.scene[i];

            if (!line.IsStudent)
            {
                var upcoming = scenario.scene.Skip(i + 1).FirstOrDefault(l => l.IsStudent);
                UI?.SetScript(line.text, upcoming?.text ?? "", scenario.studentRole, Mathf.Min(studentIndex + 1, studentLines.Count), studentLines.Count, HexeStageUI.ScriptState.WitchSpeaking);
                Performer?.SetFace(line.face);
                Performer?.PlayGesture(line.gesture);
                var clips = await Performer.SayAsync(line.text, AvatarPerformer.Voice.Witch, token);
                if (recordingAllowed) foreach (var c in clips) Recorder?.AddClip(c);
                lastWitch = line.text;
                continue;
            }

            studentIndex++;
            UI?.SetScript(lastWitch, line.text, scenario.studentRole, studentIndex, studentLines.Count, HexeStageUI.ScriptState.YourTurn);
            Stage?.PlayCue(StageEffects.Cue.YourTurn);
            Performer?.SetFace("WitchListening");

            // Anything that is not the line - a joke, a question, nonsense - gets an answer from
            // the witch, in character, before she waits for the line again. A scene partner
            // who ignores what you just said is the clearest sign nobody is really there.
            LineResult result;
            for (var offScript = 0; ; offScript++)
            {
                result = await ReadLineAsync(line, token);
                if (offScript >= MaxOffScriptReplies) break;
                var improv = await WitchReactionAsync(line, lastWitch, result, token);
                if (improv == null) break;
                UI?.SetScriptState(HexeStageUI.ScriptState.WitchSpeaking);
                await Performer.SayAsync(improv, AvatarPerformer.Voice.Witch, token, s => UI?.SetSubtitle(s), cache: false);
                UI?.SetSubtitle("");
                Performer?.SetFace("WitchListening");
                UI?.SetScriptState(HexeStageUI.ScriptState.YourTurn);
            }
            if (recordingAllowed && result.Capture.HeardSpeech) Recorder?.AddVoice(result.Capture.Samples, result.Capture.SampleRate);
            metrics.Add(result.Metrics);
            UI?.SetScriptState(HexeStageUI.ScriptState.Done);
        }

        var summary = ReadingFeedback.Summarize(metrics, studentLines, Thresholds);
        LogTake(round, summary);
        return summary;
    }

    private class LineResult
    {
        public SpeechCapture.Result Capture;
        public ReadingAnalyzer.LineMetrics Metrics;
    }

    private async UniTask<LineResult> ReadLineAsync(HexeScenario.SceneLine line, CancellationToken token)
    {
        SpeechCapture.Result capture = null;
        UniTask<WhisperClient.Result>? check = null;   // latest completion check, maybe still running
        var checkLength = -1;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(ListenDelay), cancellationToken: token);
            ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Listening;
            readingActive = true;
            try
            {
                capture = await Capture.CaptureAsync(new SpeechCapture.Options
                {
                    NoSpeechTimeout = ReadingNoSpeechTimeout,
                    EndSilence = ReadingPauseCheck,
                    HardEndSilence = ReadingHardEndSilence,
                    MaxDuration = ReadingMaxDuration,
                    IsComplete = async (samples, rate, t) =>
                    {
                        // With word timings, so this transcript can stand as the final one.
                        checkLength = samples.Length;
                        check = whisper.TranscribeAsync(samples, rate, true, null, t).Preserve();
                        var r = await check.Value;
                        if (r.Failed) return true;   // cannot judge without a transcript; trust the pause
                        var coverage = ReadingAnalyzer.Coverage(line.text, r.Text);
                        return coverage >= ReadingCompleteCoverage || (coverage >= 0.45f && ReadingAnalyzer.ReachedEnd(line.text, r.Text));
                    }
                }, token);
            }
            finally
            {
                readingActive = false;
            }

            if (capture.HeardSpeech || !string.IsNullOrEmpty(capture.TypedText)) break;
            if (attempt == 2) break;

            // Nobody read. The witch prods, in character - stepping out of the scene for a
            // reminder would break the frame this whole design protects.
            UI?.SetScriptState(HexeStageUI.ScriptState.WitchSpeaking);
            await Performer.SayAsync(scenario.Line("witchNudge"), AvatarPerformer.Voice.Witch, token);
            UI?.SetScriptState(HexeStageUI.ScriptState.YourTurn);
        }

        ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Thinking;
        WhisperClient.Result stt = null;
        if (!string.IsNullOrEmpty(capture.TypedText))
        {
            stt = new WhisperClient.Result { Text = capture.TypedText };
        }
        else if (capture.HeardSpeech)
        {
            // The completion check transcribed (or is transcribing) every word that was said
            // if only silence came after it; that transcript stands.
            if (check.HasValue && checkLength >= capture.LastVoiceSample) stt = await check.Value;
            if (stt == null || stt.Failed)
            {
                stt = await whisper.TranscribeAsync(capture.Samples, capture.SampleRate, true, null, token);
                if ((stt.Failed || string.IsNullOrEmpty(stt.Text)) && check.HasValue)
                {
                    var earlier = await check.Value;
                    if (!earlier.Failed) stt = earlier;
                }
            }
        }
        else
        {
            stt = new WhisperClient.Result();
        }

        lastHeard = stt.Text;
        var noise = Capture.NoiseFloorDb;
        var samplesCopy = capture.Samples;
        var rateCopy = capture.SampleRate;
        var metrics = await UniTask.RunOnThreadPool(() => ReadingAnalyzer.Analyze(line.text, stt, samplesCopy, rateCopy, noise), cancellationToken: token);
        metrics.Typed = !string.IsNullOrEmpty(capture.TypedText);
        ArkitFaceAnimator.ExternalMode = null;
        return new LineResult { Capture = capture, Metrics = metrics };
    }

    // The witch's in-character answer when the visitor said something other than their line,
    // or null when it was the line (however roughly read). Only a reading far from the
    // script is put to the model, and the model decides: a struggling reader is not
    // "off-script", and must not be answered as if they were.
    private async UniTask<string> WitchReactionAsync(HexeScenario.SceneLine line, string witchLine, LineResult result, CancellationToken token)
    {
        var heard = result.Metrics.RecognizedText ?? "";
        if (!UseLlm || !llm.IsConfigured || HexeScenario.Tokenize(heard).Count < 2) return null;
        if (ReadingAnalyzer.Coverage(line.text, heard) >= 0.5f) return null;

        var system =
            $"You play the witch in Hansel and Gretel in a reading role-play. The visitor, often a child of 11 to 13 learning English, reads {scenario.studentRole}'s lines from a script." +
            " Decide whether what they said was an attempt to read their line (even with many mistakes) or something else: a comment, a joke, a question, a remark to you." +
            " If it was something else, answer it as the witch, staying in character: sly, theatrical, a little creepy but child-friendly." +
            " React to exactly what they said and play with it, then steer them back into the story, in the story's own terms." +
            " Never mention lines, scripts, reading or acting - that breaks the scene. One or two very short sentences, at most 18 words, simple English, no emojis." +
            " Reply with JSON only: {\"offscript\": true|false, \"reply\": \"...\"}.";
        var user = $"You just said: \"{witchLine}\"\nTheir script line is: \"{line.text}\"\nThey said: \"{heard}\"";

        var json = LlmClient.ExtractJson(await WithAckAsync(CompleteAsync(system, user, 0.7f, 90, true, token), "witchAck", AvatarPerformer.Voice.Witch, token));
        if (json == null || (bool?)json["offscript"] != true) return null;
        var reply = CleanReply((string)json["reply"], 22, allowQuestion: true);
        if (reply != null) Debug.Log($"HexeDirector: off-script \"{heard}\" -> witch: \"{reply}\"");
        return reply;
    }

    private async UniTask<string> CompleteAsync(string system, string user, float temperature, int maxTokens, bool json, CancellationToken token)
    {
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(LlmTimeoutSec));
            var started = Time.realtimeSinceStartup;
            try
            {
                var raw = await llm.CompleteAsync(system, user, temperature, maxTokens, json, cts.Token);
                Debug.Log($"HexeDirector: model answered in {Time.realtimeSinceStartup - started:F1}s: {raw}");
                return raw;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Debug.LogWarning($"HexeDirector: model took over {LlmTimeoutSec:F0}s, using the scripted fallback.");
                return null;
            }
        }
    }

    // Covers the model's thinking time the way people do: if the answer is not there almost at
    // once, an acknowledgement ("Hmm!", "Oh?") goes out while it is worked out. Silence of a
    // second or more after someone finishes reads as a machine processing; a quick
    // acknowledgement reads as someone who heard you and is thinking.
    private async UniTask<T> WithAckAsync<T>(UniTask<T> work, string ackKey, AvatarPerformer.Voice voice, CancellationToken token)
    {
        var task = work.Preserve();
        var waited = 0f;
        while (!task.Status.IsCompleted() && waited < AckAfterSeconds)
        {
            await UniTask.Yield(token);
            waited += Time.unscaledDeltaTime;
        }
        if (!task.Status.IsCompleted() && scenario.Has(ackKey) && Performer != null)
        {
            ArkitFaceAnimator.ExternalMode = null;
            await Performer.SayAsync(scenario.Line(ackKey), voice, token);
            ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Thinking;
        }
        return await task;
    }

    private async UniTask FirstFeedbackAsync(ReadingFeedback.TakeSummary take, CancellationToken token)
    {
        Phase = "Feedback";
        if (ShowCriteriaCard && take.Usable) UI?.ShowCriteria("Your reading · take 1", take);

        var enjoy = await AskAsync("askEnjoy", token, "They just finished acting the scene for the first time.");
        await React(enjoy, "enjoyYes", "enjoyNo", "enjoyOther", token);

        var plan = ReadingFeedback.PlanFirstFeedback(take, Thresholds);
        Debug.Log($"HexeDirector: feedback focus {plan.Focus} (obs={plan.ObservationKey}, q={plan.QuestionKey})");

        if (!string.IsNullOrEmpty(plan.PositiveKey) && plan.Focus != ReadingFeedback.Focus.Deepen) await SayKey(plan.PositiveKey, token);

        switch (plan.Focus)
        {
            case ReadingFeedback.Focus.Microphone:
                await SayKey(plan.ObservationKey, token);
                await SayKey(plan.TipKey, token);
                break;

            case ReadingFeedback.Focus.Expression:
                await CharacterReflectionAsync(plan.ObservationKey, token);
                break;

            case ReadingFeedback.Focus.Deepen:
                await DeepenAsync(take, plan, token);
                break;

            default:
                await SayKey(plan.ObservationKey, token);
                var answer = await AskAsync(plan.QuestionKey, token, "You are giving reading feedback. Next you will give a short tip.");
                if (plan.Focus == ReadingFeedback.Focus.Hesitant && answer.Intent == Intent.Yes) await SayKey("fluencyYes", token);
                else if (!string.IsNullOrEmpty(answer.Reply)) await SayText(answer.Reply, token, false);
                await SayKey(plan.TipKey, token);
                break;
        }
        UI?.HideCriteria();
    }

    // The heart of the feedback: not "read it scared", but "how does Hansel feel?" - the
    // visitor works out the character's state, and the voice follows from that.
    private async UniTask CharacterReflectionAsync(string observationKey, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(observationKey)) await SayKey(observationKey, token);

        var question = scenario.Line("q_emotion_yesno") + " " + scenario.Line("reason");
        var answer = await AskTextAsync(question, token, "You asked whether Hansel feels " + scenario.focusEmotion + ".");
        var focusNamed = answer.Emotions.Any(scenario.IsFocusEmotion);
        var otherNamed = answer.Emotions.FirstOrDefault(e => !scenario.IsFocusEmotion(e));
        var needsModel = false;

        if (answer.Intent == Intent.Yes || focusNamed)
        {
            await SayKey("emotionYes", token);
            await SayKey("tip_emotion", token);
        }
        else if (answer.Intent == Intent.No && otherNamed == null)
        {
            var open = await AskAsync("emotionNo", token, "They disagreed that Hansel is " + scenario.focusEmotion + ". You asked how he feels instead.");
            needsModel = !await ResolveEmotionAnswerAsync(open, token);
        }
        else if (otherNamed != null)
        {
            // Their own reading of the character. Accept it - interpreting a role is the
            // point - and ask them to make it audible.
            await SayKey("emotionAlternative", token);
        }
        else
        {
            await SayKey("dontKnowHint", token);
            var again = await AskAsync("q_emotion_again", token, "You are helping them imagine how Hansel feels.");
            needsModel = !await ResolveEmotionAnswerAsync(again, token);
        }

        if (needsModel && scenario.offerModelReading) await ModelReadingAsync(token);
    }

    // Returns true if the visitor arrived at an interpretation of their own.
    private async UniTask<bool> ResolveEmotionAnswerAsync(Answer answer, CancellationToken token)
    {
        if (answer.Emotions.Any(scenario.IsFocusEmotion))
        {
            await SayKey("emotionFits", token);
            await SayKey("tip_emotion", token);
            return true;
        }
        if (answer.Emotions.Count > 0)
        {
            await SayKey("emotionAlternative", token);
            return true;
        }
        await SayKey("giveAnswer", token);
        await SayKey("tip_emotion", token);
        return false;
    }

    // Model reading - one of the three pillars of fluency training next to repeated and
    // assisted reading. The coach reads one line the way the character might say it.
    private async UniTask ModelReadingAsync(CancellationToken token)
    {
        var line = scenario.scene.FirstOrDefault(l => l.IsStudent && !string.IsNullOrEmpty(l.modelText));
        if (line == null) return;
        await SayKey("modelIntro", token);
        Performer?.SetFace("Concerned");
        await Performer.SayAsync(line.modelText, AvatarPerformer.Voice.Coach, token, s => UI?.SetSubtitle("“" + s + "”"));
        await SayKey("modelOutro", token);
    }

    private async UniTask DeepenAsync(ReadingFeedback.TakeSummary take, ReadingFeedback.Plan plan, CancellationToken token)
    {
        await SayKey("deepen_praise", token);
        var students = StudentLines();
        var line = plan.LineIndex >= 0 && plan.LineIndex < students.Count ? students[plan.LineIndex] : students.FirstOrDefault();
        if (line == null) return;

        await SayText($"[f:Curious] What about this line? “{Shorten(line.text)}”", token);
        var answer = await AskAsync("q_emotion_open", token, $"The line is: \"{line.text}\". In the story, {line.reason} The intended feeling is {line.emotion}.");
        var matches = answer.Emotions.Contains(line.emotion) || answer.Emotions.Any(scenario.IsFocusEmotion);
        if (matches) await SayKey("deepen_tip", token);
        else if (answer.Emotions.Count > 0) await SayKey("emotionAlternative", token);
        else
        {
            await SayText("[f:Concerned] " + line.reason, token);
            await SayKey("deepen_tip", token);
        }
    }

    private async UniTask CompareFeedbackAsync(ReadingFeedback.TakeSummary first, ReadingFeedback.TakeSummary second, CancellationToken token)
    {
        Phase = "Comparison";
        if (ShowCriteriaCard && second.Usable) UI?.ShowCriteria("Your reading · take 2", second);

        var cmp = ReadingFeedback.Compare(first, second);
        Debug.Log($"HexeDirector: comparison measurable={cmp.Measurable} better={cmp.Better} score={cmp.Score:F2} changes={string.Join(",", cmp.ChangeKeys)}");

        if (cmp.Measurable && cmp.Better)
        {
            await SayKey("compare_better", token);
            foreach (var key in cmp.ChangeKeys.Take(2)) await SayKey(key, token);
        }
        else
        {
            if (cmp.ChangeKeys.Count > 0) await SayKey(cmp.ChangeKeys[0], token);
            var answer = await AskAsync("compare_same", token, "They just read the scene a second time. You are asking whether it felt different.");
            if (!string.IsNullOrEmpty(answer.Reply)) await SayText(answer.Reply, token, false);
            else await SayKey("compareReply", token);
        }
        UI?.HideCriteria();
    }

    private async UniTask DebriefAsync(CancellationToken token)
    {
        Phase = "Debrief";
        var rhetorical = await AskAsync("debrief_rhetorical", token);
        await SayKey(rhetorical.Intent == Intent.Yes ? "debriefYes" : "debriefOther", token);
        await SayKey("debrief_points", token);

        var harder = await AskAsync("askHarder", token, "You asked what was harder when reading Hansel's part: the words or the feelings.");
        var words = Regex.IsMatch(harder.Text, @"\b(word|words|read|reading|text|pronounc)", RegexOptions.IgnoreCase);
        var feelings = Regex.IsMatch(harder.Text, @"\b(feel|feeling|feelings|emotion|emotions|voice|acting|act)", RegexOptions.IgnoreCase);
        if (words && !feelings) await SayKey("harderWords", token);
        else if (feelings && !words) await SayKey("harderFeelings", token);
        else if (!string.IsNullOrEmpty(harder.Reply)) await SayText(harder.Reply, token, false);
        else await SayKey("harderOther", token);

        await SayKey("debrief_close", token);
    }

    private async UniTask PlaybackAsync(CancellationToken token)
    {
        if (!recordingAllowed || Recorder == null || Recorder.TakeCount == 0) return;
        Phase = "Playback";

        var listen = await AskAsync("askListen", token);
        if (listen.Intent == Intent.No)
        {
            await SayKey("noListen", token);
            return;
        }

        var indices = PlaybackBothTakes && Recorder.TakeCount >= 2
            ? new[] { 0, Recorder.TakeCount - 1 }
            : new[] { Recorder.TakeCount - 1 };

        foreach (var index in indices)
        {
            if (indices.Length > 1) await SayKey(index == 0 ? "take1" : "take2", token);
            var clip = Recorder.BuildTake(index);
            if (clip == null) continue;
            var label = $"Take {index + 1}";
            ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Listening;
            Performer?.SetFace("Smile");
            await Recorder.PlayAsync(clip, p => UI?.SetPlayback(label, p), token);
            UI?.SetPlayback(null, 0f);
            ArkitFaceAnimator.ExternalMode = null;
            await UniTask.Delay(TimeSpan.FromSeconds(0.4f), cancellationToken: token);
        }

        var sound = await AskAsync("askSound", token, "They just listened to recordings of their own reading.");
        if (!string.IsNullOrEmpty(sound.Reply)) await SayText(sound.Reply, token, false);
        else await SayKey("soundReply", token);
    }

    // ================================================================== speaking

    private UniTask SayKey(string key, CancellationToken token) => SayText(scenario.Line(key), token);

    // cache: false for the model's improvised lines, which will not be said again.
    private async UniTask SayText(string text, CancellationToken token, bool cache = true)
    {
        if (string.IsNullOrWhiteSpace(text) || Performer == null) return;
        ArkitFaceAnimator.ExternalMode = null;
        await Performer.SayAsync(text, AvatarPerformer.Voice.Coach, token, sentence => UI?.SetSubtitle(sentence), cache);
        UI?.SetSubtitle("");
    }

    // The model's reaction answers what they actually said, so it wins over the generic
    // scripted "Great!" whenever there is one.
    private async UniTask React(Answer answer, string yesKey, string noKey, string otherKey, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(answer.Reply)) await SayText(answer.Reply, token, false);
        else if (answer.Intent == Intent.Yes) await SayKey(yesKey, token);
        else if (answer.Intent == Intent.No) await SayKey(noKey, token);
        else await SayKey(otherKey, token);
    }

    // For branches whose scripted line carries content of its own (telling the story, the
    // recording consent): the model's reaction to their words comes first, then the line.
    private UniTask SayReply(Answer answer, CancellationToken token)
        => string.IsNullOrEmpty(answer.Reply) ? UniTask.CompletedTask : SayText(answer.Reply, token, false);

    // ================================================================== listening

    private UniTask<Answer> AskAsync(string key, CancellationToken token, string context = null)
        => AskTextAsync(scenario.Line(key), token, context);

    // Asks, then waits for the answer - always, even after a rhetorical question, because
    // the pause is what makes it a conversation rather than a lecture.
    private async UniTask<Answer> AskTextAsync(string question, CancellationToken token, string context = null)
    {
        await SayText(question, token);
        var plainQuestion = Regex.Replace(question, @"\[[gf]:[A-Za-z]+\]", "").Trim();
        var detours = 0;

        const string sttPrompt = "Hansel, Gretel, witch, scared, yes, no.";
        for (var attempt = 0; attempt < 2; )
        {
            await UniTask.Delay(TimeSpan.FromSeconds(ListenDelay), cancellationToken: token);
            ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Listening;
            UI?.SetListening(true);
            var face = Performer != null && Performer.Face != null ? Performer.Face.Current : null;
            Performer?.SetFace("Listening");
            if (reactions != null) reactions.ExpectingSpeech = true;
            SpeechCapture.Result capture;
            UniTask<WhisperClient.Result>? check = null;   // latest pause transcription, maybe still running
            var checkLength = -1;
            var turnEndedAt = 0f;
            try
            {
                capture = await Capture.CaptureAsync(new SpeechCapture.Options
                {
                    NoSpeechTimeout = AnswerTimeout,
                    EndSilence = AnswerPauseCheck,
                    HardEndSilence = AnswerHardEndSilence,
                    MaxDuration = AnswerMaxDuration,
                    IsComplete = async (samples, rate, t) =>
                    {
                        checkLength = samples.Length;
                        check = whisper.TranscribeAsync(samples, rate, false, sttPrompt, t).Preserve();
                        var r = await check.Value;
                        if (r.Failed) return false;   // no transcript to judge by; let the long pause decide
                        return !string.IsNullOrWhiteSpace(r.Text) && !SoundsUnfinished(r.Text);
                    }
                }, token);
                turnEndedAt = Time.realtimeSinceStartup;
            }
            finally
            {
                UI?.SetListening(false);
                if (reactions != null) reactions.ExpectingSpeech = false;
                Performer?.SetFace(face);
            }

            ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Thinking;
            var text = capture.TypedText;
            var covered = check.HasValue && checkLength >= capture.LastVoiceSample;
            if (string.IsNullOrEmpty(text) && covered)
            {
                // Transcribed during the pause (or still being) - nothing but silence came
                // after, so the same transcript stands; never start a second one.
                var r = await check.Value;
                if (!r.Failed) text = r.Text;
                Debug.Log($"HexeDirector: heard \"{text}\" ({capture.Seconds:F1}s of audio, from the pause transcription, {Time.realtimeSinceStartup - turnEndedAt:F2}s after the turn ended)");
            }
            else if (string.IsNullOrEmpty(text) && capture.HeardSpeech)
            {
                var stt = await whisper.TranscribeAsync(capture.Samples, capture.SampleRate, false, sttPrompt, token);
                text = stt.Text;
                Debug.Log($"HexeDirector: heard \"{text}\" ({capture.Seconds:F1}s of audio, {Time.realtimeSinceStartup - turnEndedAt:F2}s to transcribe)");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                attempt++;
                if (attempt >= 2) break;
                await SayKey("noAnswer", token);
                continue;
            }

            lastHeard = text;
            UI?.ShowVisitorText(text);
            var answer = Classify(text);

            if (answer.Intent == Intent.Stop) throw new StopRequested();

            if (answer.Intent == Intent.Question && detours < 2)
            {
                detours++;
                var reply = await AnswerQuestionAsync(plainQuestion, text, token);
                await SayText(reply, token, false);
                await SayText(question, token);
                continue;
            }

            // A short, clear yes/no/don't-know is handled by the script at once; anything
            // longer goes to the model, which interprets it and reacts to what was said.
            if (answer.Intent == Intent.Other)
            {
                await ConsultModelAsync(answer, plainQuestion, context, token);
            }
            Debug.Log($"HexeDirector: answer \"{text}\" -> {answer.Intent}{(answer.Reply != null ? $", reply \"{answer.Reply}\"" : "")}; {Time.realtimeSinceStartup - turnEndedAt:F2}s after the turn ended");
            ArkitFaceAnimator.ExternalMode = null;
            return answer;
        }

        ArkitFaceAnimator.ExternalMode = null;
        return new Answer();
    }

    // Whether a transcript stops mid-thought - on a conjunction, an article, a filler -
    // in which case the pause is someone thinking, not someone done.
    private static readonly HashSet<string> Continuations = new HashSet<string>
    {
        "and", "but", "or", "so", "because", "cause", "um", "uh", "uhm", "erm", "hmm", "like", "the", "a", "an",
        "to", "of", "with", "my", "i", "if", "then", "when", "that", "maybe", "well", "is", "was", "think", "und", "aber", "also"
    };

    private static bool SoundsUnfinished(string text)
    {
        var t = text.Trim();
        if (t.EndsWith(",") || t.EndsWith("...") || t.EndsWith("…") || t.EndsWith("-")) return true;
        var words = Regex.Matches(t.ToLowerInvariant(), @"[\p{L}']+");
        return words.Count > 0 && Continuations.Contains(words[words.Count - 1].Value);
    }

    private Answer Classify(string text)
    {
        var answer = new Answer { Text = text, Intent = Intent.Other };
        var lower = text.ToLowerInvariant().Trim();
        var words = HexeScenario.Tokenize(lower);
        answer.Emotions = scenario.FindEmotions(text);

        bool Any(params string[] set) => set.Any(w => w.Contains(' ') ? lower.Contains(w) : words.Contains(w));

        if (words.Count <= 6 && Any("stop", "quit", "bye", "goodbye", "tschüss", "aufhören", "i want to stop"))
        {
            answer.Intent = Intent.Stop;
            return answer;
        }
        if (Any("don't know", "dont know", "do not know", "dunno", "no idea", "not sure", "weiß nicht", "weiss nicht", "keine ahnung"))
        {
            answer.Intent = Intent.DontKnow;
            return answer;
        }
        var yes = Any("yes", "yeah", "yep", "yup", "sure", "ok", "okay", "right", "correct", "true", "probably", "definitely",
                      "absolutely", "of course", "i think so", "i do", "i did", "ja", "jo", "klar", "genau", "stimmt", "uh-huh", "yea");
        var no = Any("no", "nope", "nah", "not", "never", "nein", "don't", "didn't", "doesn't", "isn't", "wasn't");
        var question = lower.EndsWith("?") || Regex.IsMatch(lower, @"^(what|why|how|who|where|when|which|can you|could you|are you|do you|is it|what's|whats)\b");

        if (question && !yes && !no && words.Count > 2) answer.Intent = Intent.Question;
        else if (yes && !no) answer.Intent = Intent.Yes;
        else if (no && !yes) answer.Intent = Intent.No;
        else if (yes && no) answer.Intent = lower.StartsWith("no") || lower.StartsWith("not") ? Intent.No : Intent.Yes;

        // "No, but I saw a fox on the way here!" holds a no, and much more than a no. Long
        // answers go to the model; the keyword verdict stays as the fallback.
        answer.RuleIntent = answer.Intent;
        if (words.Count > RuleMaxWords && answer.Intent != Intent.Question && UseLlm && llm.IsConfigured) answer.Intent = Intent.Other;
        return answer;
    }

    // Lets the model interpret an answer the rules could not place, and suggest a short
    // reaction. The reaction is validated; anything too long or odd is dropped.
    private async UniTask ConsultModelAsync(Answer answer, string question, string context, CancellationToken token)
    {
        if (!UseLlm || !llm.IsConfigured) return;
        var system = scenario.Fill(scenario.coachPersona) +
            " Reply with JSON only: {\"intent\": \"yes|no|unsure|question|other\", \"emotions\": [words naming feelings in the answer], \"reply\": \"...\"}." +
            " intent: yes = agrees or says yes; no = disagrees or says no; unsure = does not know; question = the visitor asks you something; other = anything else." +
            " 'reply' is your spoken reaction to what they actually said - pick up a detail of it, the way a person would; if they joke or say something silly, play along." +
            " One or two very short sentences, at most 15 words in total, simple English, warm." +
            " Do not ask a question in 'reply' - the app asks the next question itself. Do not comment on how they read unless the context says so. No emojis.";
        var user = $"You asked: \"{question}\"\nThe visitor answered: \"{answer.Text}\"" + (string.IsNullOrEmpty(context) ? "" : $"\nContext: {context}");

        var json = LlmClient.ExtractJson(await WithAckAsync(CompleteAsync(system, user, 0.5f, 90, true, token), "ack", AvatarPerformer.Voice.Coach, token));
        if (json == null)
        {
            answer.Intent = answer.RuleIntent;
            return;
        }

        switch (((string)json["intent"] ?? "").ToLowerInvariant())
        {
            case "yes": answer.Intent = Intent.Yes; break;
            case "no": answer.Intent = Intent.No; break;
            case "unsure": answer.Intent = Intent.DontKnow; break;
            default: answer.Intent = answer.RuleIntent; break;
        }
        if (answer.Emotions.Count == 0 && json["emotions"] is Newtonsoft.Json.Linq.JArray emotions)
        {
            answer.Emotions = scenario.FindEmotions(string.Join(" ", emotions.Select(e => (string)e)));
        }
        answer.Reply = CleanReply((string)json["reply"], 18, allowQuestion: false);
    }

    private async UniTask<string> AnswerQuestionAsync(string question, string visitorQuestion, CancellationToken token)
    {
        var fallback = "[f:Smile] Good question! Let's talk about it after the scene.";
        if (!UseLlm || !llm.IsConfigured) return fallback;
        var system = scenario.Fill(scenario.coachPersona) +
            " The visitor asked you something. Answer in at most two very short sentences (25 words at most), simple English." +
            " Stay friendly and keep it about the scene, the story or reading aloud. Do not end with a question. Plain text only.";
        var user = $"You had just asked: \"{question}\"\nThe visitor asked: \"{visitorQuestion}\"";

        ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Thinking;
        var raw = await WithAckAsync(CompleteAsync(system, user, 0.5f, 90, false, token), "ack", AvatarPerformer.Voice.Coach, token);
        var reply = CleanReply(raw, 28, allowQuestion: true);
        return string.IsNullOrEmpty(reply) ? fallback : "[f:Smile] " + reply;
    }

    private static string CleanReply(string reply, int maxWords, bool allowQuestion)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        reply = Regex.Replace(reply, @"[\*_#`]|\[[^\]]*\]", "").Trim().Trim('"');
        var sentences = Regex.Split(reply, @"(?<=[.!?])\s+").Where(s => s.Length > 0).ToList();
        if (!allowQuestion) sentences = sentences.Where(s => !s.TrimEnd().EndsWith("?")).ToList();
        // Two sentences if they fit, else the first alone: a reply one word over the limit is
        // still better said in part than dropped for the generic scripted line.
        foreach (var kept in new[] { string.Join(" ", sentences.Take(2)), sentences.FirstOrDefault() ?? "" })
        {
            var words = kept.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 0 && words <= maxWords) return kept;
        }
        return null;
    }

    // ================================================================== helpers

    private List<HexeScenario.SceneLine> StudentLines() => scenario.scene.Where(l => l.IsStudent).ToList();

    private static string Shorten(string text)
    {
        var words = text.Split(' ');
        return words.Length <= 7 ? text : string.Join(" ", words.Take(6)) + "...";
    }

    private void LogTake(int round, ReadingFeedback.TakeSummary s)
    {
        var lines = string.Join("\n", s.Lines.Select((l, i) =>
            $"  {i + 1}: acc={l.Accuracy:P0} wpm={l.Wpm:F0} hes={l.Hesitations} loud={l.LoudnessDb:F1}dB pitch={l.PitchSt:F1}st range={l.PitchRangeSt:F1}st fit={(i < s.LineFit.Length ? s.LineFit[i] : 0f):F2} heard=\"{l.RecognizedText}\""));
        Debug.Log($"HexeDirector: take {round} - volume {s.Volume}, accuracy {s.AccuracyLevel}, expression {s.Expression}, speed {s.Speed}, " +
                  $"variation {s.Variation:F2}, fit {s.Fit:F2}\n{lines}");
    }

    private bool OperatorTyping() => ShowOperatorPanel && GUIUtility.keyboardControl != 0;

    // ================================================================== operator panel

    private void OnGUI()
    {
        if (!ShowOperatorPanel) return;
        GUILayout.BeginArea(new Rect(Screen.width - 440, 10, 430, 400), GUI.skin.box);
        GUILayout.Label($"<b>Hexe role-play</b>  -  {Phase}", new GUIStyle(GUI.skin.label) { richText = true });
        if (Capture != null)
        {
            GUILayout.Label($"Mic: {Capture.DeviceInUse}");
            GUILayout.Label($"Level {Capture.LevelDb:F0} dB   floor {Capture.NoiseFloorDb:F0}   threshold {Capture.CurrentThresholdDb:F0}   {(Capture.IsCapturing ? (Capture.IsVoiceActive ? "HEARING" : "listening") : "")}");
            // Level meter from -70 to -10 dB with the threshold marked: speech should reach past the line.
            var bar = GUILayoutUtility.GetRect(410, 10);
            float Norm(float db) => Mathf.Clamp01((db + 70f) / 60f);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width, bar.height), Texture2D.grayTexture);
            var prev = GUI.color;
            GUI.color = Capture.LevelDb > Capture.CurrentThresholdDb ? Color.green : new Color(0.4f, 0.6f, 1f);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Norm(Capture.LevelDb), bar.height), Texture2D.whiteTexture);
            GUI.color = Color.red;
            GUI.DrawTexture(new Rect(bar.x + bar.width * Norm(Capture.CurrentThresholdDb) - 1, bar.y - 2, 2, bar.height + 4), Texture2D.whiteTexture);
            GUI.color = prev;
        }
        GUILayout.Label($"Language model: {llmStatus}");
        GUILayout.Label($"Heard: {lastHeard}");

        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("HexeOperatorInput");
        operatorText = GUILayout.TextField(operatorText, GUILayout.Width(300));
        var send = GUILayout.Button("Send", GUILayout.Width(60));
        GUILayout.EndHorizontal();
        var e = Event.current;
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "HexeOperatorInput") send = true;
        if (send && !string.IsNullOrWhiteSpace(operatorText))
        {
            Capture?.SubmitText(operatorText.Trim());
            operatorText = "";
        }

        GUILayout.BeginHorizontal();
        if (!IsRunning && GUILayout.Button("Start")) StartSession();
        if (IsRunning && GUILayout.Button("End turn (Tab)")) Capture?.ForceEnd();
        if (IsRunning && GUILayout.Button("Abort (Esc)")) AbortSession();
        GUILayout.EndHorizontal();

        if (takes.Count > 0)
        {
            var s = takes[takes.Count - 1];
            GUILayout.Label($"Take {takes.Count}: volume {ReadingFeedback.Label(s.Volume)}, accuracy {s.Accuracy:P0}, speed {s.Wpm:F0} wpm, expression {ReadingFeedback.Label(s.Expression)}");
            GUILayout.Label($"variation {s.Variation:F2}   fit {s.Fit:F2}   pitch range {s.PitchRangeSt:F1} st   hesitations/line {s.HesitationsPerLine:F1}");
        }
        GUILayout.Label("F1 hides this panel. Typed text answers questions or stands in for a read line.");
        GUILayout.EndArea();
    }
}
