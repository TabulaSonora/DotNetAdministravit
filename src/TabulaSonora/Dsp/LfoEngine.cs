using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Dsp;

/// <summary>Which parameter an LFO's depth applies to.</summary>
public enum LfoDestination
{
    /// <summary>Pitch, in milli-semitones.</summary>
    Pitch,

    /// <summary>Filter cutoff, in the same units as the runtime cutoff.</summary>
    Tvf,

    /// <summary>Amplitude, as a fraction of 0x7f00 applied multiplicatively.</summary>
    Tva,
}

/// <summary>One LFO's static configuration.</summary>
/// <param name="Waveform">Waveform index, after the selector nibble is mapped.</param>
/// <param name="InitialPhase">Starting phase, from the top two bits of the waveform byte.</param>
/// <param name="Increment">Phase increment per control tick.</param>
/// <param name="DelayRate">Per-tick increment of the delay accumulator; zero means never start.</param>
/// <param name="FadeRate">Per-tick increment of the fade-in accumulator.</param>
/// <param name="PitchDepth">Depth applied to pitch, in milli-semitones.</param>
/// <param name="TvfDepth">Depth applied to the filter cutoff.</param>
/// <param name="TvaDepth">Depth applied to amplitude.</param>
public readonly record struct LfoConfig(
    int Waveform,
    int InitialPhase,
    int Increment,
    int DelayRate,
    int FadeRate,
    int PitchDepth,
    int TvfDepth,
    int TvaDepth)
{
    /// <summary>The depth for one destination.</summary>
    /// <param name="destination">Which destination.</param>
    /// <returns>The depth.</returns>
    public int Depth(LfoDestination destination) => destination switch
    {
        LfoDestination.Pitch => PitchDepth,
        LfoDestination.Tvf => TvfDepth,
        _ => TvaDepth,
    };
}

/// <summary>
/// The two LFO engines that modulate every partial.
/// </summary>
/// <remarks>
/// <para>
/// A partial is modulated by both. LFO1 is tone-common, with its parameters in the tone header and
/// its per-partial depths in the partial block; LFO2 is per-partial throughout. The asymmetry that
/// matters is in how their rate and delay fields are read: LFO1's are <em>indices</em> into lookup
/// tables, while LFO2's are used raw as per-tick increments. Verified live — LFO1 rate 43 gives
/// increment 2817, LFO2 rate 917 gives 917.
/// </para>
/// <para>
/// Pitch depth is in <em>milli-semitones</em>, not cents. The pitch accumulator clamps at 0x1f018,
/// which is 127 × 1000, and that is what fixes the unit.
/// </para>
/// <para>
/// Waveform selectors 1, 2 and 3 are the random shapes, driven by a Galois LFSR. They need the
/// engine's RNG state and are not modelled: they return zero, as in the reference.
/// </para>
/// </remarks>
public sealed class LfoEngine
{
    /// <summary>Scale that maps a raw waveform value to [-1, 1].</summary>
    public const double WaveformScale = 32640.0;

    private readonly byte[] _halfSine;
    private readonly ushort[] _rateTable;
    private readonly ushort[] _centsTable;
    private readonly ushort[] _delayTable;
    private readonly byte[] _waveMap;
    private readonly short[] _waveBank;
    private readonly byte[] _tone;

    /// <summary>Creates the engine over a loaded table set.</summary>
    /// <param name="tables">The static tables.</param>
    public LfoEngine(TableSet tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _halfSine = tables.LfoWave;
        _rateTable = tables.LfoRate;
        _centsTable = tables.LfoCents;
        _delayTable = tables.LfoDelay;
        _waveMap = tables.LfoWaveMap;
        _waveBank = tables.LfoWaveBank;
        _tone = tables.Tone;
    }

    /// <summary>Depth clamp and rounding constant for a destination.</summary>
    /// <param name="destination">Which destination.</param>
    /// <returns>The clamp and the rounding constant.</returns>
    /// <remarks>
    /// Amplitude rounds by ceiling while pitch and filter round to nearest. Only the constant
    /// differs; the sign-magnitude multiply is otherwise identical.
    /// </remarks>
    public static (int Clamp, int Rounding) DestinationLimits(LfoDestination destination) => destination switch
    {
        LfoDestination.Tva => (0x7F00, 0xFFFF),
        LfoDestination.Tvf => (0x1800, 0x8000),
        _ => (6000, 0x8000),
    };

    /// <summary>Evaluates a waveform at a 16-bit phase.</summary>
    /// <param name="phase">Phase, masked to 16 bits.</param>
    /// <param name="waveform">Waveform index.</param>
    /// <returns>The signed sample.</returns>
    public int Waveform(int phase, int waveform)
    {
        var p = phase & 0xFFFF;

        switch (waveform)
        {
            case 0:
            {
                // A half-sine table mirrored by sign for the second half.
                int value = _halfSine[(p >> 8) & 0x7F];
                return p > 0x8000 ? value * -0x80 : value * 0x80;
            }

            case 4:
                return (short)(((((p >= 0x8000 ? p - 0x10000 : p) >> 15) & 2) + 0x7FFF));

            case 5:
                return (short)(p - 0x8000);

            case 6:
            {
                var quadrant = p >> 14;
                return quadrant switch
                {
                    1 => (short)~(p * 2),
                    3 => (short)((p * 2) + 1),
                    _ => (short)((quadrant == 2 ? -p : p) * 2),
                };
            }

            case 7:
            {
                var quadrant = p >> 14;
                return quadrant switch
                {
                    1 => (short)(~(p * 2) + 0x8001),
                    3 => (short)((p * 2) - 0x7FFE),
                    _ => (short)(((quadrant == 2 ? -p : p) * 2) - 0x7FFF),
                };
            }

            default:
                if (waveform >= 8 && waveform < 0x20)
                {
                    // Linear-interpolated wavetable, 0x81 entries per waveform.
                    var row = (waveform - 8) * 0x81;
                    var index = p >> 9;
                    var fraction = (p >> 1) & 0xFF;
                    int a = _waveBank[row + index];
                    int b = _waveBank[row + index + 1];
                    return (short)((((b - a) * fraction) >> 8) + a);
                }

                return 0;
        }
    }

    /// <summary>Reads both LFO configurations for one partial.</summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <param name="partial">The partial's parameter block.</param>
    /// <returns>The tone-common LFO1 and the per-partial LFO2.</returns>
    public (LfoConfig Lfo1, LfoConfig Lfo2) Configure(int toneNumber, PartialParameters partial)
    {
        var header = _tone.AsSpan(toneNumber * Tone.Stride, Tone.HeaderSize);
        var block = partial.Raw;

        var delayIndex = ReadInt16(header, 0x12);

        // The pitch depth is a signed index into the cents table, negated for negative indices.
        int pitchIndex = (sbyte)block[0x15];
        var pitchCents = pitchIndex < 0
            ? -_centsTable[pitchIndex & 0x7F]
            : _centsTable[Math.Min(0x7F, pitchIndex)];

        var lfo1 = new LfoConfig(
            Waveform: _waveMap[header[0x0E] & 0x1F],
            InitialPhase: (header[0x0E] & 0xC0) << 8,
            Increment: _rateTable[Math.Clamp(ReadUInt16(header, 0x10), 0, 0x7F)],
            DelayRate: _delayTable[Math.Clamp(delayIndex, 0, 0x7F)],
            FadeRate: ReadUInt16(header, 0x14),
            PitchDepth: pitchCents,
            // C truncates toward zero here; flooring gives -90 where the engine gives -89.
            TvfDepth: ReadInt16(block, 0x34) / 2,
            TvaDepth: ReadInt16(block, 0x56));

        // LFO2's delay is a raw increment, and a non-positive value means INSTANT rather than never.
        // A stored -1 yields 0xffff. Treating it as zero silently disables LFO2 on every such patch.
        var lfo2Delay = ReadInt16(block, 0x0A);

        var lfo2 = new LfoConfig(
            Waveform: _waveMap[block[0x06] & 0x1F],
            InitialPhase: (block[0x06] & 0xC0) << 8,
            Increment: ReadUInt16(block, 0x08),
            DelayRate: lfo2Delay > 0 ? lfo2Delay : 0xFFFF,
            FadeRate: ReadUInt16(block, 0x0C),
            PitchDepth: ReadInt16(block, 0x16),
            TvfDepth: ReadInt16(block, 0x36) / 2,
            TvaDepth: ReadInt16(block, 0x58));

        return (lfo1, lfo2);
    }

    /// <summary>Creates a runner that steps one LFO a control tick at a time.</summary>
    /// <param name="config">The LFO's configuration.</param>
    /// <returns>The runner.</returns>
    public LfoRunner CreateRunner(LfoConfig config) => new(this, config);

    /// <summary>Creates runners for both of a partial's LFOs.</summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <param name="partial">The partial's parameter block.</param>
    /// <returns>The tone-common LFO1 and the per-partial LFO2.</returns>
    public (LfoRunner Lfo1, LfoRunner Lfo2) CreateRunners(int toneNumber, PartialParameters partial)
    {
        var (lfo1, lfo2) = Configure(toneNumber, partial);
        return (CreateRunner(lfo1), CreateRunner(lfo2));
    }

    /// <summary>Runs one LFO for a number of control ticks.</summary>
    /// <param name="config">The LFO's configuration.</param>
    /// <param name="tickCount">How many 100 Hz control ticks to produce.</param>
    /// <param name="destination">Which destination's depth to apply.</param>
    /// <returns>The modulation at each tick.</returns>
    public double[] Run(LfoConfig config, int tickCount, LfoDestination destination)
    {
        var output = new double[Math.Max(0, tickCount)];
        var runner = CreateRunner(config);

        for (var i = 0; i < output.Length; i++)
        {
            runner.Tick();
            output[i] = runner.Value(destination);
        }

        return output;
    }

    /// <summary>
    /// The combined LFO1 and LFO2 modulation for a partial, per control tick.
    /// </summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="tickCount">How many control ticks to produce.</param>
    /// <param name="destination">Which destination.</param>
    /// <returns>The modulation at each tick.</returns>
    /// <remarks>
    /// The first tick is always zero: the LFO object is created after that tick's control update, so
    /// nothing has been applied yet. Aligning to that is what makes the comparison against the
    /// engine's own trace line up.
    /// </remarks>
    public double[] Modulation(int toneNumber, PartialParameters partial, int tickCount, LfoDestination destination)
    {
        if (tickCount <= 0)
        {
            return [];
        }

        var (lfo1, lfo2) = Configure(toneNumber, partial);
        var first = Run(lfo1, tickCount - 1, destination);
        var second = Run(lfo2, tickCount - 1, destination);

        var output = new double[tickCount];
        for (var i = 0; i < tickCount - 1; i++)
        {
            output[i + 1] = first[i] + second[i];
        }

        return output;
    }

    /// <summary>
    /// Combined pitch modulation with a per-tick mod-wheel depth folded into LFO1.
    /// </summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="tickCount">How many control ticks to produce.</param>
    /// <param name="modDepthPerTick">Mod-wheel depth per tick, in milli-semitones.</param>
    /// <returns>The modulation at each tick.</returns>
    /// <remarks>
    /// The wheel's contribution is added <em>after</em> the fade-in, then the total is clamped to
    /// ±6000 — six semitones, the GS full-scale figure. Only LFO1 takes it; LFO2 is unaffected.
    /// </remarks>
    public double[] PitchModulationWithWheel(
        int toneNumber, PartialParameters partial, int tickCount, double[] modDepthPerTick)
    {
        ArgumentNullException.ThrowIfNull(modDepthPerTick);

        if (tickCount <= 0)
        {
            return [];
        }

        var (lfo1, lfo2) = Configure(toneNumber, partial);
        var first = RunPitchWithWheel(lfo1, tickCount - 1, modDepthPerTick);
        var second = Run(lfo2, tickCount - 1, LfoDestination.Pitch);

        var output = new double[tickCount];
        for (var i = 0; i < tickCount - 1; i++)
        {
            output[i + 1] = first[i] + second[i];
        }

        return output;
    }

    private double[] RunPitchWithWheel(LfoConfig config, int tickCount, double[] modDepthPerTick)
    {
        var output = new double[Math.Max(0, tickCount)];
        var runner = CreateRunner(config);
        var last = modDepthPerTick.Length - 1;

        for (var i = 0; i < output.Length; i++)
        {
            runner.Tick();
            output[i] = runner.PitchValue(last < 0 ? 0 : modDepthPerTick[Math.Min(i, last)]);
        }

        return output;
    }

    /// <summary>
    /// The mod wheel's contribution to LFO1 pitch depth, in milli-semitones.
    /// </summary>
    /// <param name="controller">CC#1 value.</param>
    /// <param name="depth">GS mod LFO1 pitch depth; the power-on default is 0x0a.</param>
    /// <param name="offset">Part-level vibrato depth offset.</param>
    /// <returns>The added depth.</returns>
    /// <remarks>
    /// Summed with the tone's own pitch depth after the cents table, and the total is then clamped to
    /// ±6000 — six semitones, which is the GS full-scale figure.
    /// </remarks>
    public static int ModWheelDepth(int controller, int depth = 0x0A, int offset = 0)
    {
        // The shift truncates toward zero; both operands are non-negative here in practice.
        var raw = ((depth * controller) / 4) + offset;
        raw = Math.Clamp(raw, -4032, 4032);

        var magnitude = (Math.Abs(raw) * 2 * 48762) >> 16;
        return raw >= 0 ? magnitude : -magnitude;
    }

    private static int ReadUInt16(ReadOnlySpan<byte> source, int offset) =>
        source[offset] | (source[offset + 1] << 8);

    private static int ReadInt16(ReadOnlySpan<byte> source, int offset) =>
        (short)(source[offset] | (source[offset + 1] << 8));
}

/// <summary>
/// One LFO's running state: phase, delay and fade-in accumulators, stepped a control tick at a time.
/// </summary>
/// <remarks>
/// <para>
/// All three destinations share one runner. The accumulators do not depend on which depth is being
/// applied, so stepping once and reading three values is the same trajectory as running the LFO three
/// times — and it is what the engine does, since a partial has one LFO object, not one per
/// destination.
/// </para>
/// <para>
/// <see cref="Tick"/> advances; the value is read afterwards, so the first tick already carries a
/// phase increment.
/// </para>
/// </remarks>
public sealed class LfoRunner
{
    private readonly LfoEngine _engine;
    private readonly LfoConfig _config;

    private int _phase;
    private int _delay;
    private int _fade;
    private bool _applied;

    internal LfoRunner(LfoEngine engine, LfoConfig config)
    {
        _engine = engine;
        _config = config;
        _phase = config.InitialPhase & 0xFFFF;
    }

    /// <summary>The configuration this runner steps.</summary>
    public LfoConfig Config => _config;

    /// <summary>Whether the delay has elapsed, so the LFO is being applied.</summary>
    public bool IsApplied => _applied;

    /// <summary>Advances one control tick.</summary>
    public void Tick()
    {
        _applied = false;
        if (_config.Increment == 0)
        {
            return;
        }

        _phase = (_phase + _config.Increment) & 0xFFFF;

        if (_delay < 0xFFFF)
        {
            // The LFO runs during the delay but is not applied. A zero rate never completes, which is
            // how a patch switches an LFO off entirely.
            if (_config.DelayRate == 0)
            {
                return;
            }

            _delay = Math.Min(0xFFFF, _delay + _config.DelayRate);
            if (_delay < 0xFFFF)
            {
                return;
            }
        }

        if (_fade < 0xFFFF)
        {
            _fade = Math.Min(0xFFFF, _fade + _config.FadeRate);
        }

        _applied = true;
    }

    /// <summary>The modulation for one destination, after the last tick.</summary>
    /// <param name="destination">Which destination's depth to apply.</param>
    /// <returns>The modulation.</returns>
    public double Value(LfoDestination destination)
    {
        var (clamp, rounding) = LfoEngine.DestinationLimits(destination);
        var depth = Math.Clamp(_config.Depth(destination), -clamp, clamp);
        if (depth == 0)
        {
            return 0.0;
        }

        return Apply(Faded(depth), rounding);
    }

    /// <summary>
    /// The pitch modulation with a mod-wheel depth folded in.
    /// </summary>
    /// <param name="wheelDepth">The wheel's contribution in milli-semitones.</param>
    /// <returns>The modulation.</returns>
    /// <remarks>
    /// The wheel is summed with the tone's own depth <em>after</em> the fade-in, and the total is then
    /// clamped to ±6000 — six semitones, the GS full-scale figure.
    /// </remarks>
    public double PitchValue(double wheelDepth)
    {
        var (clamp, rounding) = LfoEngine.DestinationLimits(LfoDestination.Pitch);
        var depth = Math.Clamp(_config.PitchDepth, -clamp, clamp);
        var effective = Math.Clamp(Faded(depth) + (int)wheelDepth, -clamp, clamp);
        return Apply(effective, rounding);
    }

    private int Faded(int depth) => _fade == 0xFFFF
        ? depth
        : ((Math.Abs(depth) * _fade) >> 16) * (depth >= 0 ? 1 : -1);

    private double Apply(int effective, int rounding)
    {
        if (!_applied)
        {
            return 0.0;
        }

        var wave = _engine.Waveform(_phase, _config.Waveform);

        // Sign-magnitude fixed-point multiply: magnitudes multiply, signs compose separately.
        var magnitude = ((Math.Abs(wave) * Math.Abs(effective) * 2) + rounding) >> 16;
        return (wave >= 0) == (effective >= 0) ? magnitude : -magnitude;
    }
}
