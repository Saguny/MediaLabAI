using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

// Writes the replay to an MP4 while it plays, so the film exists the moment it ends.
//
// Frames come off the GPU with AsyncGPUReadback (no stall) and are piped raw into ffmpeg on a
// background thread; the soundtrack is the replay's own audio, written as a WAV beforehand,
// so picture and sound share one clock and cannot drift. The film's clock decides which
// frame number each rendered frame is: a slow frame is repeated, a fast one skipped, and the
// file stays at a steady frame rate whatever the editor's frame rate was.
public class MovieExporter
{
    public string OutputPath { get; private set; }
    public bool IsRecording => process != null;

    private readonly int width, height, fps;
    private Process process;
    private Stream stdin;
    private Thread writer;
    private readonly BlockingCollection<(byte[] Data, int Repeat)> queue = new BlockingCollection<(byte[], int)>(90);
    private int framesQueued;
    private string wavPath;
    private string log = "";

    public MovieExporter(int width, int height, int fps)
    {
        this.width = width;
        this.height = height;
        this.fps = fps;
    }

    // Looks for ffmpeg on PATH, then where winget puts it (Unity started before the install
    // does not see the updated PATH).
    public static string FindFfmpeg(string configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            foreach (var pkg in Directory.GetDirectories(winget, "Gyan.FFmpeg*"))
            {
                var found = Directory.GetFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories);
                if (found.Length > 0) return found[0];
            }
        }
        return null;
    }

    public bool Start(string ffmpeg, string outputPath, float[] audio, int audioRate)
    {
        OutputPath = outputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        wavPath = Path.ChangeExtension(outputPath, ".wav");
        File.WriteAllBytes(wavPath, WhisperClient.ToWav(audio, audioRate));

        // Frames arrive bottom-up from the GPU, hence vflip.
        var args = $"-y -hide_banner -loglevel warning " +
                   $"-f rawvideo -pix_fmt rgba -s {width}x{height} -r {fps} -i - " +
                   $"-i \"{wavPath}\" -map 0:v -map 1:a " +
                   $"-vf vflip -c:v libx264 -preset veryfast -crf 19 -pix_fmt yuv420p " +
                   $"-c:a aac -b:a 160k -shortest -movflags +faststart \"{outputPath}\"";
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                },
            };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log += e.Data + "\n"; };
            process.Start();
            process.BeginErrorReadLine();
            stdin = process.StandardInput.BaseStream;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"MovieExporter: could not start ffmpeg ({ex.Message}) - the replay plays but is not saved.");
            process = null;
            return false;
        }

        writer = new Thread(WriteLoop) { IsBackground = true, Name = "MovieExporter" };
        writer.Start();
        return true;
    }

    // Captures the target's current contents as the frame(s) up to film time t.
    public void Capture(RenderTexture target, float t)
    {
        if (process == null) return;
        var due = Mathf.FloorToInt(t * fps) + 1;
        var repeat = due - framesQueued;
        if (repeat <= 0) return;
        framesQueued = due;
        AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
        {
            if (request.hasError || queue.IsAddingCompleted) return;
            var data = request.GetData<byte>().ToArray();
            try { queue.Add((data, repeat)); } catch (InvalidOperationException) { }
        });
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var (data, repeat) in queue.GetConsumingEnumerable())
            {
                for (var i = 0; i < repeat; i++) stdin.Write(data, 0, data.Length);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"MovieExporter: writing to ffmpeg failed: {ex.Message}");
        }
    }

    // Waits for the last readbacks, closes the pipe and lets ffmpeg finish the file, off the
    // main thread. Returns the finished file's path, or null if it failed.
    public async UniTask<string> FinishAsync()
    {
        if (process == null) return null;
        AsyncGPUReadback.WaitAllRequests();
        await UniTask.Yield();   // completion callbacks run on the main thread
        queue.CompleteAdding();
        var p = process;
        process = null;
        var ok = await UniTask.RunOnThreadPool(() =>
        {
            writer?.Join(10000);
            try { stdin.Close(); } catch (IOException) { }
            var finished = p.WaitForExit(60000);
            var success = finished && p.ExitCode == 0 && File.Exists(OutputPath);
            if (!success) Debug.LogWarning($"MovieExporter: ffmpeg failed (exit {(finished ? p.ExitCode.ToString() : "timeout")}): {log}");
            p.Dispose();
            try { File.Delete(wavPath); } catch (IOException) { }
            return success;
        });
        return ok ? OutputPath : null;
    }

    public void Abort()
    {
        if (process == null) return;
        queue.CompleteAdding();
        try { stdin.Close(); } catch (IOException) { }
        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        process.Dispose();
        process = null;
        try { File.Delete(wavPath); } catch (IOException) { }
        try { File.Delete(OutputPath); } catch (IOException) { }
    }
}
