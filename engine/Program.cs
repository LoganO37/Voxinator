using Ducker.Capture;
using Ducker.Config;
using Ducker.Interop;
using Ducker.Ipc;
using Ducker.Tray;
using Ducker.Vad;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Ducker;

/// <summary>
/// Phase 0 spike harness. One console app, several subcommands, each mapping to a spike
/// in PLAN.md:
///   list      -> pick a process (feeds Spike 1)
///   capture   -> Spike 1: isolated process-loopback capture to WAV
///   vad       -> Spike 2: run Silero VAD over a WAV, print probabilities/segments
///   accuracy  -> Spike 3: run VAD over a folder of labeled clips, print confusion matrix
///   live      -> Spike 4: capture -> VAD -> debouncer, print DUCK/UNDUCK in real time
///   serve     -> Spike 5: same as live, plus a local WebSocket server for the extension
/// </summary>
internal static class Program
{
    [STAThread] // required for WinForms (tray); capture uses its own MTA thread internally
    private static int Main(string[] args)
    {
        if (args.Length == 0) { PrintHelp(); return 1; }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" or "list-processes" => CmdList(),
                "capture" => CmdCapture(args),
                "vad" => CmdVad(args),
                "accuracy" => CmdAccuracy(args),
                "live" => CmdLive(args, serve: false),
                "serve" => CmdLive(args, serve: true),
                "simlive" => CmdSimLive(args),
                "service" => CmdService(args),
                "tray" => CmdTray(args),
                "help" or "-h" or "--help" => PrintHelp(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    // ---- list --------------------------------------------------------------
    private static int CmdList()
    {
        Console.WriteLine("Processes currently producing audio (default render device):\n");
        Console.WriteLine($"  {"PID",-8} {"STATE",-10} {"PROCESS",-28} DISPLAY NAME");
        Console.WriteLine(new string('-', 78));
        foreach (var s in ProcessList.AudioSessions())
            Console.WriteLine($"  {s.Pid,-8} {s.State,-10} {s.ProcessName,-28} {s.DisplayName}");
        Console.WriteLine("\nPick the PID of your game, then run:");
        Console.WriteLine("  voxinator capture --pid <PID> --out game.wav --seconds 20");
        return 0;
    }

    // ---- capture (Spike 1) -------------------------------------------------
    private static int CmdCapture(string[] args)
    {
        uint pid = (uint)IntOpt(args, "--pid", -1);
        string outPath = Opt(args, "--out", "capture.wav");
        int seconds = IntOpt(args, "--seconds", 20);
        bool exclude = Flag(args, "--exclude");
        if (pid == unchecked((uint)-1)) { Console.Error.WriteLine("--pid is required (see `voxinator list`)."); return 1; }

        var mode = exclude ? ProcessLoopbackMode.ExcludeTargetProcessTree : ProcessLoopbackMode.IncludeTargetProcessTree;
        Console.WriteLine($"Capturing PID {pid} ({(exclude ? "EXCLUDE tree" : "INCLUDE tree")}) for {seconds}s -> {outPath}");

        using var capture = new ProcessLoopbackCapture(pid, mode);
        using var writer = new WaveFileWriter(outPath, capture.WaveFormat);
        long bytes = 0;
        float peak = 0;
        capture.DataAvailable += (_, e) =>
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            bytes += e.BytesRecorded;
            peak = Math.Max(peak, PeakOf16BitPcm(e.Buffer, e.BytesRecorded));
        };

        capture.Start();
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(500);
            Console.Write($"\r  {bytes / 1024,8:N0} KiB written | peak {peak:F3}   ");
        }
        capture.Stop();
        Console.WriteLine($"\nDone. {writer.Length / 1024:N0} KiB. Peak level {peak:F3} " +
                          (peak < 0.0005 ? "<-- WARNING: near silence. Was the app actually playing audio?" : ""));
        return 0;
    }

    // ---- vad (Spike 2) -----------------------------------------------------
    private static int CmdVad(string[] args)
    {
        string inPath = Opt(args, "--in", null);
        if (inPath == null) { Console.Error.WriteLine("--in <file.wav> is required."); return 1; }
        float threshold = (float)DblOpt(args, "--threshold", 0.5);
        string model = ModelPath(args);

        using var vad = new SileroVad(model);
        var probs = new List<(double t, float p)>();
        foreach (var (t, p) in vad.ScoreWavFile(inPath))
            probs.Add((t, p));

        if (probs.Count == 0) { Console.WriteLine("No audio frames read."); return 0; }
        double dur = probs[^1].t + SileroVad.ChunkSeconds;
        int over = probs.Count(x => x.p >= threshold);
        Console.WriteLine($"File: {inPath}");
        Console.WriteLine($"Duration ~{dur:F1}s, {probs.Count} chunks of {SileroVad.ChunkSeconds * 1000:F0}ms");
        Console.WriteLine($"Mean prob {probs.Average(x => x.p):F3} | max {probs.Max(x => x.p):F3} | " +
                          $"frames >= {threshold:F2}: {over} ({100.0 * over / probs.Count:F1}%)\n");

        // Speech segments from raw threshold (no hangover) for a quick visual.
        Console.WriteLine("Speech segments (raw threshold):");
        bool inSeg = false; double segStart = 0; int segs = 0;
        for (int i = 0; i < probs.Count; i++)
        {
            bool sp = probs[i].p >= threshold;
            if (sp && !inSeg) { inSeg = true; segStart = probs[i].t; }
            else if (!sp && inSeg) { inSeg = false; segs++; Console.WriteLine($"  [{segStart,6:F2}s - {probs[i].t,6:F2}s]"); }
        }
        if (inSeg) { segs++; Console.WriteLine($"  [{segStart,6:F2}s - {dur,6:F2}s]"); }
        if (segs == 0) Console.WriteLine("  (none)");
        return 0;
    }

    // ---- accuracy (Spike 3) ------------------------------------------------
    private static int CmdAccuracy(string[] args)
    {
        string dir = Opt(args, "--dir", null);
        if (dir == null || !Directory.Exists(dir)) { Console.Error.WriteLine("--dir <folder of labeled wavs> is required."); return 1; }
        float threshold = (float)DblOpt(args, "--threshold", 0.5);
        var dp = DebounceParamsFrom(args);
        string model = ModelPath(args);

        Console.WriteLine($"Accuracy harness | threshold={threshold:F2} attack={dp.MinSpeechMs}ms endBuffer={dp.EndBufferMs}ms\n");
        Console.WriteLine("Label clips by filename prefix: dialog_  music_  sfx_  ambient_  vocalmusic_  (others = unlabeled)\n");
        Console.WriteLine($"  {"FILE",-34} {"LABEL",-11} {"max",-6} {"mean",-6} {"%>=th",-7} FIRED?");
        Console.WriteLine(new string('-', 78));

        using var vad = new SileroVad(model);
        var rows = new List<(string label, bool expected, bool fired)>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.wav").OrderBy(x => x))
        {
            vad.Reset();
            var deb = new Debouncer(dp);
            bool fired = false;
            deb.DialogStart += (_, __) => fired = true;
            var probs = new List<float>();
            foreach (var (_, p) in vad.ScoreWavFile(f)) { probs.Add(p); deb.Push(p >= threshold, p); }
            string name = Path.GetFileNameWithoutExtension(f);
            string label = LabelOf(name);
            bool? expected = ExpectedSpeech(label);
            rows.Add((label, expected ?? false, fired));
            float max = probs.Count > 0 ? probs.Max() : 0;
            float mean = probs.Count > 0 ? probs.Average() : 0;
            float pct = probs.Count > 0 ? 100f * probs.Count(x => x >= threshold) / probs.Count : 0;
            Console.WriteLine($"  {Trunc(name, 34),-34} {label,-11} {max,-6:F3} {mean,-6:F3} {pct,-7:F1} {(fired ? "YES" : "no")}");
        }

        // Confusion matrix over clips that have a definite expectation.
        var labeled = rows.Where(r => ExpectedSpeech(r.label) != null).ToList();
        int tp = labeled.Count(r => r.expected && r.fired);
        int fn = labeled.Count(r => r.expected && !r.fired);
        int fp = labeled.Count(r => !r.expected && r.fired);
        int tn = labeled.Count(r => !r.expected && !r.fired);
        Console.WriteLine($"\nConfusion (per clip, 'fired' = sustained dialog detected):");
        Console.WriteLine($"  True Positives : {tp,3}   False Negatives: {fn,3}");
        Console.WriteLine($"  False Positives: {fp,3}   True Negatives : {tn,3}");
        if (tp + fn > 0) Console.WriteLine($"  Recall (dialog caught)      : {100.0 * tp / (tp + fn):F1}%");
        if (fp + tn > 0) Console.WriteLine($"  False-positive rate (non-dlg): {100.0 * fp / (fp + tn):F1}%");

        var vm = rows.Where(r => r.label == "vocalmusic").ToList();
        if (vm.Count > 0)
            Console.WriteLine($"\n  Hard case 'vocalmusic' false-trigger: {vm.Count(r => r.fired)}/{vm.Count} clips fired.");
        return 0;
    }

    // ---- simlive: drive the LIVE pipeline from a WAV (offline regression test) ----
    private static int CmdSimLive(string[] args)
    {
        string inPath = Opt(args, "--in", null);
        if (inPath == null) { Console.Error.WriteLine("--in <file.wav> is required."); return 1; }
        float threshold = (float)DblOpt(args, "--threshold", 0.5);
        var dp = DebounceParamsFrom(args);
        string model = ModelPath(args);

        using var reader = new WaveFileReader(inPath);
        var chunker = new Mono16kChunker(reader.WaveFormat); // exact same class the live path uses
        using var vad = new SileroVad(model);
        var deb = new Debouncer(dp);
        int fires = 0; float maxP = 0;
        deb.DialogStart += (_, e) => { fires++; Console.WriteLine($"  >>> DUCK   (p={e.Probability:F2})"); };
        deb.DialogEnd += (_, __) => Console.WriteLine("  <<< UNDUCK");
        chunker.ChunkReady += chunk => { float p = vad.Process(chunk); maxP = Math.Max(maxP, p); deb.Push(p >= threshold, p); };

        // Feed the file through the chunker in ~100 ms byte packets, mimicking capture.
        int packet = Math.Max(2, reader.WaveFormat.AverageBytesPerSecond / 10);
        var buf = new byte[packet];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0) chunker.Add(buf, n);

        Console.WriteLine($"\nsimlive: max prob {maxP:F3}, DUCK fired {fires} time(s) -> " +
                          (fires > 0 ? "LIVE PATH OK" : "did NOT fire"));
        return 0;
    }

    // ---- live (Spike 4) / serve (Spike 5) ----------------------------------
    private static int CmdLive(string[] args, bool serve)
    {
        uint pid = (uint)IntOpt(args, "--pid", -1);
        if (pid == unchecked((uint)-1)) { Console.Error.WriteLine("--pid is required (see `voxinator list`)."); return 1; }
        bool exclude = Flag(args, "--exclude");
        float threshold = (float)DblOpt(args, "--threshold", 0.5);
        var dp = DebounceParamsFrom(args);
        string model = ModelPath(args);
        var mode = exclude ? ProcessLoopbackMode.ExcludeTargetProcessTree : ProcessLoopbackMode.IncludeTargetProcessTree;

        DialogWebSocketServer ws = null;
        if (serve)
        {
            int port = IntOpt(args, "--port", 8730);
            string token = Opt(args, "--token", "changeme");
            ws = new DialogWebSocketServer(port, token);
            ws.Start();
            Console.WriteLine($"WebSocket server: ws://127.0.0.1:{port}/?token={token}");
        }

        using var vad = new SileroVad(model);
        var deb = new Debouncer(dp);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        deb.DialogStart += (_, e) =>
        {
            Console.WriteLine($"[{sw.Elapsed:mm\\:ss\\.f}] >>> DUCK   (dialog start, p={e.Probability:F2})");
            ws?.Broadcast($"{{\"type\":\"DIALOG_START\",\"ts\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"confidence\":{e.Probability:F3}}}");
        };
        deb.DialogEnd += (_, e) =>
        {
            Console.WriteLine($"[{sw.Elapsed:mm\\:ss\\.f}] <<< UNDUCK (dialog end)");
            ws?.Broadcast($"{{\"type\":\"DIALOG_END\",\"ts\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
        };

        using var capture = new ProcessLoopbackCapture(pid, mode);
        var chunker = new Mono16kChunker(capture.WaveFormat);
        chunker.ChunkReady += chunk => deb.Push(vad.Process(chunk) >= threshold, vad.LastProbability);
        capture.DataAvailable += (_, e) => chunker.Add(e.Buffer, e.BytesRecorded);

        Console.WriteLine($"Listening to PID {pid}. threshold={threshold:F2} attack={dp.MinSpeechMs}ms endBuffer={dp.EndBufferMs}ms");
        Console.WriteLine("Play game dialog to see DUCK; press Ctrl+C to stop.\n");
        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        capture.Start();
        stop.Wait();
        capture.Stop();
        ws?.Dispose();
        return 0;
    }

    // ---- service: headless DetectionEngine using saved settings (Phase 1) ----
    private static int CmdService(string[] args)
    {
        var s = EngineSettings.Load();
        s.Port = IntOpt(args, "--port", s.Port);
        if (Opt(args, "--token", null) != null) s.Token = Opt(args, "--token", s.Token);
        s.Threshold = (float)DblOpt(args, "--threshold", s.Threshold);
        s.Enabled = true;

        // CLI override of monitored sources: --pids 1234,5678  (or single --pid 1234)
        var pidArg = Opt(args, "--pids", Opt(args, "--pid", null));
        if (pidArg != null)
        {
            s.Sources = new();
            foreach (var part in pidArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (uint.TryParse(part, out var pid))
                    s.Sources.Add(new GameSource { ProcessName = ProcNameOf(pid), Pid = pid });
        }

        string model = ModelPath(args);
        using var engine = new DetectionEngine(model, s) { Log = m => Console.WriteLine($"[engine] {m}") };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        engine.DuckChanged += on => Console.WriteLine($"[{sw.Elapsed:mm\\:ss\\.f}] {(on ? ">>> DUCK" : "<<< UNDUCK")}");
        Console.WriteLine($"Service running. WS ws://127.0.0.1:{s.Port}/?token={s.Token}");
        Console.WriteLine($"Sources: {(s.Sources.Count == 0 ? "none - set via tray or --pids" : string.Join(", ", s.Sources.Select(x => $"{x.ProcessName}({x.Pid})")))}");
        Console.WriteLine($"Threshold {s.Threshold:F2}, attack {s.MinSpeechMs}ms, end buffer {s.EndBufferMs}ms");
        Console.WriteLine("Ctrl+C to stop.\n");
        engine.Start();

        System.Threading.Timer dbg = null;
        if (Flag(args, "--debug"))
        {
            Console.WriteLine("(debug: max VAD probability per source each second; * = speaking detected)\n");
            dbg = new System.Threading.Timer(_ =>
            {
                var probe = engine.Probe();
                if (probe.Count == 0) return;
                Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] " + string.Join("   ",
                    probe.Select(x => $"{x.name}={x.maxProb:F2}{(x.active ? "*" : "")}{(x.capturing ? "" : " (NOT capturing)")}")));
            }, null, 1000, 1000);
        }

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();
        dbg?.Dispose();
        return 0;
    }

    private static string ProcNameOf(uint pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return ""; }
    }

    // ---- tray: system-tray front end (Phase 1) -----------------------------
    private static int CmdTray(string[] args)
    {
        string model;
        try { model = ModelPath(args); }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show(ex.Message, "Voxinator",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
        ConsoleWindow.Hide();
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            System.Windows.Forms.Application.Run(new TrayApp(model));
        }
        catch (Exception ex)
        {
            TrayLogger.Log("tray crashed: " + ex);
            System.Windows.Forms.MessageBox.Show(ex.ToString(), "Voxinator — error",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 2;
        }
        return 0;
    }

    // ---- helpers -----------------------------------------------------------
    private static DebounceParams DebounceParamsFrom(string[] args) => new()
    {
        ChunkMs = SileroVad.ChunkSeconds * 1000,
        MinSpeechMs = IntOpt(args, "--min-speech-ms", 250),
        EndBufferMs = IntOpt(args, "--end-buffer-ms", IntOpt(args, "--hang-ms", 2000)),
    };

    private static string ModelPath(string[] args)
    {
        string m = Opt(args, "--model", Path.Combine(AppContext.BaseDirectory, "models", "silero_vad.onnx"));
        if (!File.Exists(m)) throw new FileNotFoundException($"Silero model not found at {m}. Pass --model <path>.");
        return m;
    }

    private static string LabelOf(string name)
    {
        name = name.ToLowerInvariant();
        foreach (var l in new[] { "vocalmusic", "dialog", "music", "sfx", "ambient" })
            if (name.StartsWith(l)) return l;
        return "unlabeled";
    }

    private static bool? ExpectedSpeech(string label) => label switch
    {
        "dialog" => true,
        "music" or "sfx" or "ambient" or "vocalmusic" => false,
        _ => null,
    };

    private static float PeakOf16BitPcm(byte[] buf, int count)
    {
        float peak = 0;
        for (int i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(buf[i] | (buf[i + 1] << 8));
            peak = Math.Max(peak, Math.Abs(s / 32768f));
        }
        return peak;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "~";
    private static string Opt(string[] a, string name, string def) { for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1]; return def; }
    private static int IntOpt(string[] a, string name, int def) { var v = Opt(a, name, null); return v != null && int.TryParse(v, out var r) ? r : def; }
    private static double DblOpt(string[] a, string name, double def) { var v = Opt(a, name, null); return v != null && double.TryParse(v, out var r) ? r : def; }
    private static bool Flag(string[] a, string name) => a.Contains(name);

    private static int Unknown(string cmd) { Console.Error.WriteLine($"Unknown command '{cmd}'."); PrintHelp(); return 1; }

    private static int PrintHelp()
    {
        Console.WriteLine(@"voxinator - Phase 0 spike harness

  voxinator list
      List processes currently producing audio (find your game's PID).

  voxinator capture --pid <PID> --out game.wav [--seconds 20] [--exclude]   (Spike 1)
      Capture one process's audio in isolation to a WAV.
      --exclude captures everything EXCEPT that process tree (fallback mode).

  voxinator vad --in game.wav [--threshold 0.5] [--model path]              (Spike 2)
      Run Silero VAD over a WAV; print probabilities and speech segments.

  voxinator accuracy --dir clips\ [--threshold 0.5] [--min-speech-ms 250]   (Spike 3)
                  [--end-buffer-ms 2000]
      Run VAD over labeled clips (filename prefixes dialog_/music_/sfx_/
      ambient_/vocalmusic_) and print a confusion matrix.

  voxinator live --pid <PID> [--threshold 0.5] [--min-speech-ms 250]        (Spike 4)
              [--end-buffer-ms 2000] [--exclude]
      Live capture -> VAD -> debouncer; prints DUCK/UNDUCK as dialog comes and goes.

  voxinator serve --pid <PID> [--port 8730] [--token changeme] [...]        (Spike 5)
      Same as live, plus a local WebSocket server the browser extension connects to.

  voxinator service [--pids 1234,5678] [--port 8730] [--token ...]          (Phase 1)
      Headless background engine using saved settings (%APPDATA%\Voxinator).
      --pids overrides the monitored sources for this run (speech in ANY ducks media).

  voxinator tray                                                            (Phase 1)
      System-tray app: pick one or more sources (game, Discord, ...), toggle, settings.");
        return 0;
    }
}
