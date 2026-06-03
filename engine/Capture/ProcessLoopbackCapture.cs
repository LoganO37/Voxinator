using System.ComponentModel;
using System.Runtime.InteropServices;
using Ducker.Interop;
using NAudio.Wave;
using static Ducker.Interop.AudioConstants;

namespace Ducker.Capture;

/// <summary>
/// Captures the audio of a single process tree in isolation using the Windows 10 2004+
/// WASAPI process-loopback path (ActivateAudioInterfaceAsync on the VAD\Process_Loopback
/// virtual device). Output is exposed NAudio-style via <see cref="DataAvailable"/>.
///
/// This is the gate-critical Spike 1: if include-mode capture contains only the target's
/// audio (and not other apps'), the whole architecture is viable.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    // Fixed capture format. The process-loopback device accepts a PCM format and converts
    // internally. 16-bit / 48 kHz / stereo keeps downstream code simple.
    private static readonly WAVEFORMATEX Fmt = new()
    {
        wFormatTag = 1, // WAVE_FORMAT_PCM
        nChannels = 2,
        nSamplesPerSec = 48000,
        wBitsPerSample = 16,
        nBlockAlign = 4,            // channels * bits/8
        nAvgBytesPerSec = 48000 * 4,
        cbSize = 0,
    };

    // HRESULTs returned by IAudioClient.Initialize on the process-loopback device.
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);             // target not rendering yet
    private const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);

    public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 2);
    public event EventHandler<WaveInEventArgs> DataAvailable;

    private readonly uint _pid;
    private readonly ProcessLoopbackMode _mode;
    private Thread _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _initDone = new(false);
    private Exception _initError;

    public ProcessLoopbackCapture(uint pid, ProcessLoopbackMode mode)
    {
        _pid = pid;
        _mode = mode;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ProcessLoopbackCapture" };
        _thread.SetApartmentState(ApartmentState.MTA); // ActivateAudioInterfaceAsync requires MTA
        _thread.Start();
        _initDone.Wait();
        if (_initError != null) throw _initError;
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
    }

    public void Dispose()
    {
        Stop();
        _initDone.Dispose();
    }

    private void CaptureLoop()
    {
        IntPtr pParams = IntPtr.Zero, pPropVariant = IntPtr.Zero, hEvent = IntPtr.Zero;
        IAudioClient audioClient = null;
        IAudioCaptureClient captureClient = null;
        try
        {
            // --- build activation params (PROPVARIANT(VT_BLOB) -> AUDIOCLIENT_ACTIVATION_PARAMS)
            var p = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                {
                    TargetProcessId = _pid,
                    ProcessLoopbackMode = _mode,
                },
            };
            int paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
            pParams = Marshal.AllocCoTaskMem(paramsSize);
            Marshal.StructureToPtr(p, pParams, false);

            var pv = new PROPVARIANT { vt = VT_BLOB, cbSize = (uint)paramsSize, pBlobData = pParams };
            pPropVariant = Marshal.AllocCoTaskMem(Marshal.SizeOf<PROPVARIANT>());
            Marshal.StructureToPtr(pv, pPropVariant, false);

            // --- activate (async; wait for completion)
            var handler = new ActivationHandler();
            int hr = NativeMethods.ActivateAudioInterfaceAsync(
                VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, IID_IAudioClient, pPropVariant, handler, out var op);
            Marshal.ThrowExceptionForHR(hr);

            if (!handler.Wait(5000)) throw new TimeoutException("ActivateAudioInterfaceAsync did not complete in 5s.");
            op.GetActivateResult(out int activateHr, out object clientObj);
            Marshal.ThrowExceptionForHR(activateHr);
            audioClient = (IAudioClient)clientObj;

            // --- initialize event-driven loopback capture
            var fmt = Fmt;
            const long hns100ms = 1_000_000; // REFERENCE_TIME, 100 ns units
            hr = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                hns100ms, 0, ref fmt, IntPtr.Zero);
            if (hr != 0)
            {
                if (hr == E_UNEXPECTED)
                    // The process-loopback device returns E_UNEXPECTED from Initialize when the
                    // target process has no active render stream yet — e.g. Discord is open but
                    // nobody is talking. Nothing to fix here; the caller retries until the app
                    // actually plays audio, at which point the same format initializes fine.
                    throw new ProcessNotRenderingException(_pid);

                string hint = hr == AUDCLNT_E_UNSUPPORTED_FORMAT
                    ? " — the loopback device rejected 16-bit PCM 48k stereo on this system"
                    : "";
                throw new InvalidOperationException(
                    $"IAudioClient.Initialize failed (0x{hr:X8}){hint}.", Marshal.GetExceptionForHR(hr));
            }

            hEvent = NativeMethods.CreateEventEx(IntPtr.Zero, IntPtr.Zero, 0, EVENT_ALL_ACCESS);
            if (hEvent == IntPtr.Zero) throw new Win32Exception();
            Marshal.ThrowExceptionForHR(audioClient.SetEventHandle(hEvent));

            var iidCapture = IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref iidCapture, out object capObj));
            captureClient = (IAudioCaptureClient)capObj;

            Marshal.ThrowExceptionForHR(audioClient.Start());
            _initDone.Set(); // initialization succeeded; unblock Start()

            int blockAlign = fmt.nBlockAlign;
            while (_running)
            {
                NativeMethods.WaitForSingleObject(hEvent, 200);
                while (true)
                {
                    Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out uint packetFrames));
                    if (packetFrames == 0) break;
                    Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                        out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int bytes = (int)frames * blockAlign;
                    var buffer = new byte[bytes];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero)
                        Marshal.Copy(data, buffer, 0, bytes);
                    Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
            }

            audioClient.Stop();
        }
        catch (Exception ex)
        {
            _initError = ex;
            _initDone.Set(); // unblock Start() so it can rethrow
        }
        finally
        {
            if (captureClient != null) Marshal.ReleaseComObject(captureClient);
            if (audioClient != null) Marshal.ReleaseComObject(audioClient);
            if (hEvent != IntPtr.Zero) NativeMethods.CloseHandle(hEvent);
            if (pParams != IntPtr.Zero) Marshal.FreeCoTaskMem(pParams);
            if (pPropVariant != IntPtr.Zero) Marshal.FreeCoTaskMem(pPropVariant);
        }
    }

    /// <summary>Completion handler for ActivateAudioInterfaceAsync; just signals an event.</summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _done = new(false);
        public bool Wait(int ms) => _done.Wait(ms);
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            _done.Set();
            return 0; // S_OK
        }
    }
}

/// <summary>
/// Thrown when process-loopback capture can't initialize because the target process has no active
/// audio render stream yet (HRESULT E_UNEXPECTED) — e.g. the app is open but not playing anything.
/// This is expected and transient: retry once the app produces audio. Kept distinct from real
/// device/format failures so callers can treat "just waiting for audio" quietly.
/// </summary>
public sealed class ProcessNotRenderingException : Exception
{
    public ProcessNotRenderingException(uint pid)
        : base($"Target process {pid} has no active audio stream yet; will retry when it plays audio.") { }
}
