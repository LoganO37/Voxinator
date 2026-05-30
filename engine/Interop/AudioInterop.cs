using System.Runtime.InteropServices;

namespace Ducker.Interop;

// ---- enums / constants -----------------------------------------------------

public enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,
    ExcludeTargetProcessTree = 1,
}

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal static class AudioConstants
{
    // mmdeviceapi.h
    public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";

    // AudioClientShareMode
    public const int AUDCLNT_SHAREMODE_SHARED = 0;

    // Stream flags (audioclient.h)
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;

    // GetBuffer flags
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    // PROPVARIANT
    public const ushort VT_BLOB = 65;

    // Wait
    public const uint WAIT_OBJECT_0 = 0;
    public const uint EVENT_ALL_ACCESS = 0x1F0003;

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
}

// ---- structs ---------------------------------------------------------------

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AUDIOCLIENT_ACTIVATION_PARAMS
{
    public AudioClientActivationType ActivationType;
    public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
}

// PROPVARIANT laid out for the VT_BLOB case on x64 (24 bytes: 8-byte header,
// cbSize at offset 8, pointer at offset 16).
[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public uint cbSize;     // BLOB.cbSize
    public IntPtr pBlobData; // BLOB.pBlobData
}

// ---- COM interfaces --------------------------------------------------------

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult([MarshalAs(UnmanagedType.Error)] out int activateResult,
                          [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity,
                   [In] ref WAVEFORMATEX format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, [In] ref WAVEFORMATEX format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags,
                  out long devicePosition, out long qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

// ---- native methods --------------------------------------------------------

internal static class NativeMethods
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventEx(IntPtr lpEventAttributes, IntPtr lpName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);
}
