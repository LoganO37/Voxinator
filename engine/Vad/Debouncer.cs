namespace Ducker.Vad;

public sealed class DebounceParams
{
    /// <summary>Duration represented by one VAD chunk (ms).</summary>
    public double ChunkMs { get; set; } = 32;
    /// <summary>Sustained speech required before we declare dialog (attack).</summary>
    public double MinSpeechMs { get; set; } = 250;
    /// <summary>How long to keep media ducked/paused after dialog stops, before
    /// restoring it (the "end buffer"). Also bridges the natural pauses between lines,
    /// so the media doesn't snap back and forth during a conversation.</summary>
    public double EndBufferMs { get; set; } = 2000;
}

public sealed class DialogEventArgs : EventArgs
{
    public float Probability { get; init; }
}

/// <summary>
/// Turns a noisy per-chunk speech/no-speech stream into stable DialogStart / DialogEnd
/// events. This is the "through its duration" logic from the spec:
///   - require MinSpeechMs of (near-)contiguous speech before triggering (ignores blips),
///   - keep dialog "on" through natural gaps and for EndBufferMs after the last speech
///     (the configurable "end buffer"; prevents flapping and abrupt snap-back).
/// </summary>
public sealed class Debouncer
{
    private readonly DebounceParams _p;
    private bool _active;
    private double _speechMs;
    private double _silenceMs;

    public Debouncer(DebounceParams p) => _p = p;

    /// <summary>Update timing in place (no need to recreate the debouncer/capture).</summary>
    public void Configure(double minSpeechMs, double endBufferMs)
    {
        _p.MinSpeechMs = minSpeechMs;
        _p.EndBufferMs = endBufferMs;
    }

    public bool Active => _active;
    public event EventHandler<DialogEventArgs> DialogStart;
    public event EventHandler<DialogEventArgs> DialogEnd;

    public void Push(bool isSpeech, float probability)
    {
        if (isSpeech)
        {
            _silenceMs = 0;
            _speechMs += _p.ChunkMs;
            if (!_active && _speechMs >= _p.MinSpeechMs)
            {
                _active = true;
                DialogStart?.Invoke(this, new DialogEventArgs { Probability = probability });
            }
        }
        else
        {
            _silenceMs += _p.ChunkMs;
            if (_active)
            {
                if (_silenceMs >= _p.EndBufferMs)
                {
                    _active = false;
                    _speechMs = 0;
                    DialogEnd?.Invoke(this, new DialogEventArgs { Probability = probability });
                }
            }
            else
            {
                // Not yet triggered: a gap resets the attack accumulator so only
                // sustained speech counts.
                _speechMs = 0;
            }
        }
    }
}
