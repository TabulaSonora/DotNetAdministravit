namespace TabulaSonora.Dsp;

/// <summary>
/// A four-segment envelope with a release, evaluated at any sample position.
/// </summary>
/// <remarks>
/// <para>
/// The TVA and TVF envelopes have the same shape — four segments that run from note-on, a hold at the
/// last target, then a release that starts wherever note-off caught it. What differs is only how the
/// targets and durations are decoded, which is the job of <see cref="TvaChain"/> and
/// <see cref="TvfChain"/>.
/// </para>
/// <para>
/// The trajectory is a pure function of the sample index and the note-off position, so the offline
/// renderer and the real-time voice loop read the same envelope by construction rather than by
/// agreement between two transcriptions. Nothing here is advanced by rendering: a block renderer
/// evaluates it per sample, and an offline one fills an array.
/// </para>
/// </remarks>
public sealed class SegmentEnvelope
{
    /// <summary>Segments before the release.</summary>
    public const int SegmentCount = 4;

    private readonly EnvelopeMachine _machine;
    private readonly double[] _targets = new double[SegmentCount];
    private readonly bool[] _linear = new bool[SegmentCount];
    private readonly long[] _from = new long[SegmentCount];
    private readonly long[] _to = new long[SegmentCount];
    private readonly double[] _span = new double[SegmentCount];

    private readonly double _releaseTarget;
    private readonly bool _releaseLinear;
    private double _releaseSpan;
    private long _releaseSamples;
    private readonly double _afterRelease;
    private readonly long _controlTick;

    private long _noteOff = -1;
    private double _atNoteOff;

    /// <summary>Builds an envelope from decoded segment parameters.</summary>
    /// <param name="machine">The shared segment machine, which supplies the interpolation curve.</param>
    /// <param name="targets">The four segment targets.</param>
    /// <param name="segmentSamples">The four segment durations, in samples.</param>
    /// <param name="linear">Whether each segment interpolates linearly.</param>
    /// <param name="releaseTarget">Where the release heads.</param>
    /// <param name="releaseSamples">The release duration, in samples.</param>
    /// <param name="releaseLinear">Whether the release interpolates linearly.</param>
    /// <param name="afterRelease">The value held once the release has finished.</param>
    /// <param name="controlTickSamples">
    /// The control tick in samples — the grid note-off is acted on. A tick of one releases at the note
    /// after note-off, which is as close to releasing immediately as this gets and is <em>not</em> what
    /// the engine does; see <see cref="NoteOff(long, int)"/>.
    /// </param>
    /// <exception cref="ArgumentException">A parameter array is not four long.</exception>
    public SegmentEnvelope(
        EnvelopeMachine machine,
        ReadOnlySpan<double> targets,
        ReadOnlySpan<double> segmentSamples,
        ReadOnlySpan<bool> linear,
        double releaseTarget,
        double releaseSamples,
        bool releaseLinear,
        double afterRelease,
        long controlTickSamples)
    {
        ArgumentNullException.ThrowIfNull(machine);

        if (targets.Length != SegmentCount || segmentSamples.Length != SegmentCount || linear.Length != SegmentCount)
        {
            throw new ArgumentException($"An envelope needs exactly {SegmentCount} segments.", nameof(targets));
        }

        _machine = machine;
        _releaseTarget = releaseTarget;
        _releaseLinear = releaseLinear;
        _releaseSpan = releaseSamples;
        _releaseSamples = Math.Max(1, (long)releaseSamples);
        _afterRelease = afterRelease;
        _controlTick = Math.Max(1, controlTickSamples);

        // Boundaries are accumulated in samples rather than per segment, so a run of short segments
        // cannot drift against the position a sample index falls at.
        var elapsed = 0.0;
        for (var i = 0; i < SegmentCount; i++)
        {
            _targets[i] = targets[i];
            _linear[i] = linear[i];
            _span[i] = segmentSamples[i];
            _from[i] = (long)elapsed;
            elapsed += segmentSamples[i];
            _to[i] = (long)elapsed;
        }
    }

    /// <summary>Where the release starts, or −1 while the note is still held.</summary>
    public long NoteOffSample => _noteOff;

    /// <summary>The release duration in samples, at least one.</summary>
    public long ReleaseSamples => _releaseSamples;

    /// <summary>
    /// Starts the release at a sample position. Later calls are ignored.
    /// </summary>
    /// <param name="sample">Sample index of note-off, relative to note-on.</param>
    /// <remarks>
    /// <para>
    /// The release departs from the value the envelope had reached, not from a segment target, which
    /// is what makes a note released mid-attack decay from where it actually was.
    /// </para>
    /// <para>
    /// Note-off does not take effect at the sample it lands on: the engine sees it at its next control
    /// tick, so the envelope holds for the rest of the current tick first. Measured by sweeping the
    /// hold time past a tick boundary — note-off anywhere in 1000–1008 ms produced the same release,
    /// which then stepped a whole tick later at 1010 ms. Releasing immediately instead runs the tail
    /// 0–10 ms early; that is inaudible on a pad but a large fraction of a short release, and the
    /// Accordion reaches −20 dB in 9 ms against the engine's 23 ms.
    /// </para>
    /// <para>
    /// The grid is the voice's own, since a voice starts its control tick at note-on. The pitch
    /// envelope already behaves this way for free — it is only stepped from the control tick — so
    /// this is what puts the amplitude and cutoff envelopes back on the same grid as it.
    /// </para>
    /// </remarks>
    public void NoteOff(long sample) => NoteOff(sample, 0);

    /// <summary>
    /// Starts the release at a sample position with a half-damper pedal value. Later calls are
    /// ignored.
    /// </summary>
    /// <param name="sample">Sample index of note-off, relative to note-on.</param>
    /// <param name="damper">
    /// CC64 value at release, 1–0x3f for a half-pressed pedal on a half-damper tone, else zero.
    /// </param>
    /// <remarks>
    /// The engine writes the pedal value into the release ramp's rate-scale byte, which multiplies
    /// the rate word by <c>(0x10000 − (v&lt;&lt;9) − 1) / 0x10000</c> — roughly <c>1 − v/128</c> —
    /// so a half-pressed pedal lengthens the release by the reciprocal. Only the 57 piano tones
    /// carry the half-damper capability (tone header byte 0x0d bit 2); every other tone's pedal
    /// value is quantised to 0 or 0x7f before it can get here.
    /// </remarks>
    public void NoteOff(long sample, int damper)
    {
        if (_noteOff >= 0)
        {
            return;
        }

        if (damper > 0)
        {
            var scale = 65536.0 / (0xFFFF - (Math.Min(damper, 0x3F) << 9));
            _releaseSpan *= scale;
            _releaseSamples = Math.Max(1, (long)_releaseSpan);
        }

        var deferred = DeferToControlTick(sample, _controlTick);

        _atNoteOff = Held(Math.Max(0, deferred - 1));
        _noteOff = deferred;
    }

    /// <summary>
    /// The control tick an event landing at a sample is acted on.
    /// </summary>
    /// <param name="sample">Sample the event landed at.</param>
    /// <param name="controlTickSamples">The control tick, in samples.</param>
    /// <returns>The sample the engine would act on it at.</returns>
    /// <remarks>
    /// The <em>following</em> tick, even when the event lands exactly on a boundary: that tick's
    /// update has already run by the time the event is latched. Measured on the DLL — a note-off at
    /// 1010 ms, exactly a tick, released at 1020 ms, while one at 1008 ms released at 1010 ms. So the
    /// deferral spans one full tick and is never zero.
    /// </remarks>
    public static long DeferToControlTick(long sample, long controlTickSamples)
    {
        var tick = Math.Max(1, controlTickSamples);
        return (Math.Max(0, sample) / tick * tick) + tick;
    }

    /// <summary>The envelope's value at a sample position.</summary>
    /// <param name="sample">Sample index relative to note-on.</param>
    /// <returns>The value.</returns>
    public double ValueAt(long sample)
    {
        if (_noteOff < 0 || sample < _noteOff)
        {
            return Held(sample);
        }

        var n = sample - _noteOff;
        if (n >= _releaseSamples)
        {
            return _afterRelease;
        }

        var position = _releaseSpan > 0 ? n / _releaseSpan : 1.0;
        return _machine.SegmentCurve(position, _atNoteOff, _releaseTarget, _releaseLinear);
    }

    /// <summary>Whether the release has run out at a sample position.</summary>
    /// <param name="sample">Sample index relative to note-on.</param>
    /// <returns>Whether the envelope has finished.</returns>
    public bool IsFinished(long sample) => _noteOff >= 0 && sample - _noteOff >= _releaseSamples;

    private double Held(long sample)
    {
        var previous = 0.0;

        for (var i = 0; i < SegmentCount; i++)
        {
            if (_to[i] > _from[i] && sample < _to[i])
            {
                return _machine.SegmentCurve((sample - _from[i]) / _span[i], previous, _targets[i], _linear[i]);
            }

            previous = _targets[i];
        }

        // Every segment has run: hold the last target until note-off.
        return previous;
    }
}
