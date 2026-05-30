using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Ducker.Capture;

/// <summary>
/// Converts arbitrary captured PCM (e.g. 48 kHz / 16-bit / stereo) into the exact stream
/// Silero expects: 16 kHz mono float, delivered in fixed 512-sample chunks. Leftover
/// samples are carried across calls so no audio is dropped between packets.
/// The emitted buffer is reused — consumers must read it synchronously and not retain it.
/// </summary>
public sealed class Mono16kChunker
{
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _pipeline;
    private readonly float[] _chunk = new float[512];
    private readonly float[] _scratch = new float[512];
    private int _filled;

    public event Action<float[]> ChunkReady;

    public Mono16kChunker(WaveFormat captureFormat)
    {
        _buffer = new BufferedWaveProvider(captureFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5),
            // CRITICAL: default is true, which makes Read() pad with silence and always
            // return the full count. That turns the read loop in Add() into an infinite
            // loop feeding the VAD silence, so live detection never fires. Must be false
            // so Read() returns only the buffered audio (0 when empty).
            ReadFully = false,
        };
        ISampleProvider sp = _buffer.ToSampleProvider();
        if (captureFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (captureFormat.Channels != 1)
            throw new NotSupportedException($"{captureFormat.Channels}-channel capture not supported.");
        if (sp.WaveFormat.SampleRate != 16000)
            sp = new WdlResamplingSampleProvider(sp, 16000);
        _pipeline = sp;
    }

    public void Add(byte[] data, int count)
    {
        _buffer.AddSamples(data, 0, count);
        int read;
        while ((read = _pipeline.Read(_scratch, 0, _chunk.Length - _filled)) > 0)
        {
            Array.Copy(_scratch, 0, _chunk, _filled, read);
            _filled += read;
            if (_filled == _chunk.Length)
            {
                ChunkReady?.Invoke(_chunk);
                _filled = 0;
            }
        }
    }
}
