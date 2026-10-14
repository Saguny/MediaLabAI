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
    public float AnswerEndSilence = 0.9f;
    public float AnswerMaxDuration = 15f;
    [Tooltip("A pause this long while reading triggers a check of whether the line is finished.")]
    public float ReadingPauseCheck = 0.9f;
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
    private string openingGreet, openingWhoAmI;

    private class StopRequested : Exception { }

    private enum Intent { Silent, Yes, No, DontKnow, Question, Stop, Other }

    private class Answer
    {
        public string Text = "";
        public Intent Intent = Intent.Silent;
        public List<string> Emotions = new List<string>();
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

        UI?.ShowStartPrompt(scenario.Line("attract"));
        Performer?.SetFace("Smile");

        // Synthesize the opening and the witch's cue lines while the stage idles, so the
        // greeting plays the moment a session starts. Fetched at session start, the greeting
        // queued behind every witch line in Kokoro and again in SAiD. Waits a frame for
        // AvatarPerformer.Start to create its TTS loaders.
        UniTask.Void(async () =>
        {
            await UniTask.Yield(this.GetCancellationTokenOnDestroy());
            PrepareOpening();
            PrefetchWitchLines(this.GetCancellationTokenOnDestroy());
        });

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

            PrefetchWitchLines(token);
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
        PrepareOpening();
    }

    // Picks the next visitor's opening lines now and starts their synthesis, so IntroAsync
    // finds them cached.
    private void PrepareOpening()
    {
        openingGreet = scenario.Line("greet");
        openingWhoAmI = scenario.Line("whoami");
        Performer?.Prefetch(openingGreet, AvatarPerformer.Voice.Coach, this.GetCancellationTokenOnDestroy());
        Performer?.Prefetch(openingWhoAmI, AvatarPerformer.Voice.Coach, this.GetCancellationTokenOnDestroy());
    }

    // ================================================================== phases

    private async UniTask IntroAsync(CancellationToken token)
    {
        Phase = "Intro";
        await SayText(openingGreet, token);
        await SayText(openingWhoAmI, token);

        var fairy = await AskAsync("askFairy", token);
        await React(fairy, "fairyYes", "fairyNo", "fairyOther", token);

        var story = await AskAsync("askKnowStory", token);
        if (story.Intent == Intent.Yes) await SayKey("storyYes", token);
        else await SayKey("storyNo", token);

        await SayKey("roles", token);
        UI?.SetScript("", scenario.scene.FirstOrDefault(l => l.IsStudent)?.text, scenario.studentRole, 1, StudentLines().Count, HexeStageUI.ScriptState.Done);
        UI?.SetSceneFrame(true, scenario.sceneTitle);
        await SayKey("scriptPanel", token);
        UI?.SetSceneFrame(false, scenario.sceneTitle);

        var record = await AskAsync("askRecord", token);
        recordingAllowed = record.Intent == Intent.Yes;
        await SayKey(recordingAllowed ? "recordYes" : "recordNo", token);

        await SayKey("situation", token);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ready = await AskAsync("askReady", token);
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

            var result = await ReadLineAsync(line, token);
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
        WhisperClient.Result partial = null;
        var partialLength = -1;

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
                        var r = await whisper.TranscribeAsync(samples, rate, false, null, t);
                        if (r.Failed) return true;   // cannot judge without a transcript; trust the pause
                        partial = r;
                        partialLength = samples.Length;
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
        WhisperClient.Result stt;
        if (!string.IsNullOrEmpty(capture.TypedText))
        {
            stt = new WhisperClient.Result { Text = capture.TypedText };
        }
        else if (capture.HeardSpeech)
        {
            stt = await whisper.TranscribeAsync(capture.Samples, capture.SampleRate, true, null, token);
            if ((stt.Failed || string.IsNullOrEmpty(stt.Text)) && partial != null && partialLength > 0) stt = partial;
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
                else if (!string.IsNullOrEmpty(answer.Reply)) await SayText(answer.Reply, token);
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
            if (!string.IsNullOrEmpty(answer.Reply)) await SayText(answer.Reply, token);
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
        else if (!string.IsNullOrEmpty(harder.Reply)) await SayText(harder.Reply, token);
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
        if (!string.IsNullOrEmpty(sound.Reply)) await SayText(sound.Reply, token);
        else await SayKey("soundReply", token);
    }

    // ================================================================== speaking

    private UniTask SayKey(string key, CancellationToken token) => SayText(scenario.Line(key), token);

    private async UniTask SayText(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) || Performer == null) return;
        ArkitFaceAnimator.ExternalMode = null;
        await Performer.SayAsync(text, AvatarPerformer.Voice.Coach, token, sentence => UI?.SetSubtitle(sentence));
        UI?.SetSubtitle("");
    }

    private void PrefetchWitchLines(CancellationToken token)
    {
        if (Performer == null) return;
        foreach (var line in scenario.scene.Where(l => !l.IsStudent)) Performer.Prefetch(line.text, AvatarPerformer.Voice.Witch, token);
    }

    private async UniTask React(Answer answer, string yesKey, string noKey, string otherKey, CancellationToken token)
    {
        if (answer.Intent == Intent.Yes) await SayKey(yesKey, token);
        else if (answer.Intent == Intent.No) await SayKey(noKey, token);
        else if (!string.IsNullOrEmpty(answer.Reply)) await SayText(answer.Reply, token);
        else await SayKey(otherKey, token);
    }

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

        for (var attempt = 0; attempt < 2; )
        {
            await UniTask.Delay(TimeSpan.FromSeconds(ListenDelay), cancellationToken: token);
            ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Listening;
            UI?.SetListening(true);
            SpeechCapture.Result capture;
            try
            {
                capture = await Capture.CaptureAsync(new SpeechCapture.Options
                {
                    NoSpeechTimeout = AnswerTimeout,
                    EndSilence = AnswerEndSilence,
                    MaxDuration = AnswerMaxDuration
                }, token);
            }
            finally
            {
                UI?.SetListening(false);
            }

            ArkitFaceAnimator.ExternalMode = ArkitFaceAnimator.Mode.Thinking;
            var text = capture.TypedText;
            if (string.IsNullOrEmpty(text) && capture.HeardSpeech)
            {
                var stt = await whisper.TranscribeAsync(capture.Samples, capture.SampleRate, false, "Hansel, Gretel, witch, scared, yes, no.", token);
                text = stt.Text;
                Debug.Log($"HexeDirector: heard \"{text}\" ({capture.Seconds:F1}s of audio)");
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
                await SayText(reply, token);
                await SayText(question, token);
                continue;
            }

            // Clear yes/no/don't-know answers are handled by the script; anything else goes
            // to the model for interpretation and a reaction.
            if (answer.Intent == Intent.Other)
            {
                await ConsultModelAsync(answer, plainQuestion, context, token);
            }
            ArkitFaceAnimator.ExternalMode = null;
            return answer;
        }

        ArkitFaceAnimator.ExternalMode = null;
        return new Answer();
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
            " 'reply' is your spoken reaction to the answer: one or two very short sentences, at most 15 words in total, simple English, warm." +
            " Do not ask a question in 'reply' - the app asks the next question itself. Do not comment on how they read unless the context says so. No emojis.";
        var user = $"You asked: \"{question}\"\nThe visitor answered: \"{answer.Text}\"" + (string.IsNullOrEmpty(context) ? "" : $"\nContext: {context}");

        string raw;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(LlmTimeoutSec));
            try { raw = await llm.CompleteAsync(system, user, 0.4f, 120, true, cts.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { raw = null; }
        }

        var json = LlmClient.ExtractJson(raw);
        if (json == null) return;

        if (answer.Intent == Intent.Other)
        {
            switch (((string)json["intent"] ?? "").ToLowerInvariant())
            {
                case "yes": answer.Intent = Intent.Yes; break;
                case "no": answer.Intent = Intent.No; break;
                case "unsure": answer.Intent = Intent.DontKnow; break;
            }
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
        string raw;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(LlmTimeoutSec));
            try { raw = await llm.CompleteAsync(system, user, 0.5f, 90, false, cts.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { raw = null; }
        }
        var reply = CleanReply(raw, 28, allowQuestion: true);
        return string.IsNullOrEmpty(reply) ? fallback : "[f:Smile] " + reply;
    }

    private static string CleanReply(string reply, int maxWords, bool allowQuestion)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        reply = Regex.Replace(reply, @"[\*_#`]|\[[^\]]*\]", "").Trim().Trim('"');
        var sentences = Regex.Split(reply, @"(?<=[.!?])\s+").Where(s => s.Length > 0).ToList();
        if (!allowQuestion) sentences = sentences.Where(s => !s.TrimEnd().EndsWith("?")).ToList();
        var kept = string.Join(" ", sentences.Take(2));
        var words = kept.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > maxWords) return null;
        return kept;
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
