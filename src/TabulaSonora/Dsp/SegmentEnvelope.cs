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
    private readonly double _releaseSpan;
    private readonly long _releaseSamples;
    private readonly double _afterRelease;

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
    /// <exception cref="ArgumentException">A parameter array is not four long.</exception>
    public SegmentEnvelope(
        EnvelopeMachine machine,
        ReadOnlySpan<double> targets,
        ReadOnlySpan<double> segmentSamples,
        ReadOnlySpan<bool> linear,
        double releaseTarget,
        double releaseSamples,
        bool releaseLinear,
        double afterRelease)
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
    /// The release departs from the value the envelope had reached, not from a segment target, which
    /// is what makes a note released mid-attack decay from where it actually was.
    /// </remarks>
    public void NoteOff(long sample)
    {
        if (_noteOff >= 0)
        {
            return;
        }

        _atNoteOff = Held(Math.Max(0, sample - 1));
        _noteOff = sample;
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
