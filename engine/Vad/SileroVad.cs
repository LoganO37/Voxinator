using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Ducker.Vad;

/// <summary>
/// Thin wrapper over the Silero VAD v5 ONNX model.
/// Verified signature (printed from the model graph):
///   inputs : input[1,512] float, state[2,1,128] float, sr int64 scalar
///   outputs: output[1,1] float (speech prob), stateN[2,1,128] float
/// The model is stateful: it must be fed contiguous 512-sample (32 ms @ 16 kHz) chunks,
/// carrying state between calls. Reset() clears state between independent files.
/// </summary>
public sealed class SileroVad : IDisposable
{
    public const int SampleRate = 16000;
    public const int ChunkSamples = 512;            // new samples per step (the hop)
    public const int ContextSamples = 64;           // v5 prepends 64 samples of context
    public const int InputSamples = ContextSamples + ChunkSamples; // 576 fed to the model
    public const double ChunkSeconds = (double)ChunkSamples / SampleRate;

    private readonly InferenceSession _session;
    private float[] _state = new float[2 * 1 * 128];
    private readonly float[] _context = new float[ContextSamples];
    private readonly float[] _input = new float[InputSamples];
    private readonly long[] _sr = { SampleRate };

    public float LastProbability { get; private set; }

    public SileroVad(string modelPath)
    {
        var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        _session = new InferenceSession(modelPath, opts);
    }

    public void Reset()
    {
        Array.Clear(_state, 0, _state.Length);
        Array.Clear(_context, 0, _context.Length);
    }

    /// <summary>Score one 512-sample chunk (16 kHz mono float, range ~[-1,1]).</summary>
    public float Process(float[] chunk)
    {
        if (chunk.Length != ChunkSamples)
            throw new ArgumentException($"chunk must be {ChunkSamples} samples, got {chunk.Length}");

        // v5 input = [64 carried context samples] + [512 new samples] = 576.
        Array.Copy(_context, 0, _input, 0, ContextSamples);
        Array.Copy(chunk, 0, _input, ContextSamples, ChunkSamples);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, new[] { 1, InputSamples })),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, new[] { 2, 1, 128 })),
            NamedOnnxValue.CreateFromTensor("sr",    new DenseTensor<long>(_sr, Array.Empty<int>())),
        };

        using var results = _session.Run(inputs);
        float prob = 0;
        foreach (var r in results)
        {
            if (r.Name == "output") prob = r.AsTensor<float>().GetValue(0);
            else if (r.Name == "stateN") _state = r.AsTensor<float>().ToArray();
        }
        // Carry the last 64 samples of this chunk as context for the next call.
        Array.Copy(chunk, ChunkSamples - ContextSamples, _context, 0, ContextSamples);
        LastProbability = prob;
        return prob;
    }

    /// <summary>Stream (timestampSeconds, probability) over a WAV file, resampling to
    /// 16 kHz mono as needed. Resets state at the start.</summary>
    public IEnumerable<(double t, float p)> ScoreWavFile(string path)
    {
        Reset();
        using var reader = new AudioFileReader(path);
        ISampleProvider sp = reader;
        if (reader.WaveFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (reader.WaveFormat.Channels != 1)
            throw new NotSupportedException($"{reader.WaveFormat.Channels}-channel audio is not supported; use mono or stereo.");

        if (sp.WaveFormat.SampleRate != SampleRate)
            sp = new WdlResamplingSampleProvider(sp, SampleRate);

        var buf = new float[ChunkSamples];
        int idx = 0;
        double t = 0;
        var tmp = new float[ChunkSamples];
        int read;
        while ((read = sp.Read(tmp, 0, ChunkSamples - idx)) > 0)
        {
            Array.Copy(tmp, 0, buf, idx, read);
            idx += read;
            if (idx < ChunkSamples) continue;
            yield return (t, Process(buf));
            t += ChunkSeconds;
            idx = 0;
        }
    }

    public void Dispose() => _session.Dispose();
}
