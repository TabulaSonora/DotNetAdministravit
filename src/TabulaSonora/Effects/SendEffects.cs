using System.Runtime.CompilerServices;

namespace TabulaSonora.Effects;

/// <summary>A stereo send effect fed from one mono bus.</summary>
public interface IEffect
{
    /// <summary>Processes a block.</summary>
    /// <param name="input">The mono send bus.</param>
    /// <param name="left">Receives the wet left channel.</param>
    /// <param name="right">Receives the wet right channel.</param>
    void Process(ReadOnlySpan<float> input, Span<float> left, Span<float> right);

    /// <summary>Clears all internal state.</summary>
    void Reset();
}

/// <summary>
/// The engine's DC blocker: one design, instantiated once on each effect input.
/// </summary>
/// <remarks>
/// A 20 Hz one-pole highpass. The coefficient 0.99804 is the only float literal between 0.85 and 1.0
/// anywhere in the binary, which is how it was established that no second design exists. It is needed
/// because the sample codec's predictor is a pure integrator that drifts on every loop pass.
/// </remarks>
public struct DcBlocker
{
    /// <summary>Input coefficient.</summary>
    public const double InputCoefficient = 0.99804;

    /// <summary>State feedback coefficient.</summary>
    public const double StateCoefficient = -0.003919;

    private double _state;

    /// <summary>Clears the filter.</summary>
    public void Reset() => _state = 0.0;

    /// <summary>Filters one sample.</summary>
    /// <param name="input">Input sample.</param>
    /// <returns>The filtered sample.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Process(double input)
    {
        var output = (input * InputCoefficient) + _state;
        _state += output * StateCoefficient;
        return output;
    }
}

/// <summary>
/// The GS reverb: a Schroeder/Dattorro tank transcribed from the engine.
/// </summary>
/// <remarks>
/// Input conditioner, then four series allpass diffusers, then two parallel tanks each with a damping
/// lowpass and two nested allpasses, then eight output taps. All eight GS types share this topology —
/// Delay and PanDelay simply zero the diffusers and collapse to a single long tap.
/// The write cursor <em>decrements</em>, so every tap offset is added to it.
/// </remarks>
public sealed class Reverb : IEffect
{
    /// <summary>Size of the shared delay ring.</summary>
    public const int RingSize = 65536;

    private const int RingMask = RingSize - 1;
    private const int InputTap = 0x1000;

    private readonly ReverbPreset _preset;
    private readonly TankTaps _tapsA;
    private readonly TankTaps _tapsB;
    private readonly double[] _ring = new double[RingSize];

    private double _dcState;
    private double _damp;
    private double _tankStateA;
    private double _tankStateB;
    private int _writeCursor;

    /// <summary>Creates a reverb.</summary>
    /// <param name="preset">Coefficients for the GS type.</param>
    /// <exception cref="KeyNotFoundException">A tank is missing one of the eight named taps.</exception>
    public Reverb(ReverbPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        _preset = preset;

        // Resolved here rather than per sample. See TankTaps.
        _tapsA = TankTaps.From(preset.TankA);
        _tapsB = TankTaps.From(preset.TankB);
    }

    /// <summary>
    /// One tank's eight ring offsets, resolved from their names once.
    /// </summary>
    /// <remarks>
    /// <see cref="ReverbTank.Taps"/> is a <see cref="Dictionary{TKey,TValue}"/> because that is the
    /// shape the harvester dumps and <c>presets.json</c> carries. Reading it is not: indexing it by
    /// name inside the sample loop cost sixteen string hashes per sample — four per tank in
    /// <see cref="RunTank"/> and eight more in the output taps — which measured 44.8 ms per ten
    /// seconds of audio against the chorus's 1.1 ms for a comparable amount of arithmetic.
    /// </remarks>
    private readonly record struct TankTaps(
        int Tap10, int Tap14, int Tap18, int Tap1C, int Tap20, int Tap24, int Tap28, int Tap2C)
    {
        public static TankTaps From(ReverbTank tank) => new(
            tank.Taps["tap10"], tank.Taps["tap14"], tank.Taps["tap18"], tank.Taps["tap1C"],
            tank.Taps["tap20"], tank.Taps["tap24"], tank.Taps["tap28"], tank.Taps["tap2C"]);
    }

    /// <summary>Creates a reverb for a GS type.</summary>
    /// <param name="type">GS reverb type 0–7, or <see langword="null"/> for the default.</param>
    /// <returns>The reverb.</returns>
    public static Reverb ForType(int? type)
    {
        var presets = EffectPresets.Default.Reverb;
        return new Reverb(type is null ? presets.Default : presets.Types[type.Value]);
    }

    /// <summary>The send-bus gain for a controller value.</summary>
    /// <param name="controller">CC#91.</param>
    /// <returns>The gain.</returns>
    /// <remarks>Linear in the controller, unlike the volume law, which is squared.</remarks>
    public static double SendGain(int controller) =>
        ReverbPresets.SendAtFullScale * (controller / 127.0);

    /// <inheritdoc/>
    public void Reset()
    {
        Array.Clear(_ring);
        _dcState = _damp = _tankStateA = _tankStateB = 0.0;
        _writeCursor = 0;
    }

    /// <inheritdoc/>
    public void Process(ReadOnlySpan<float> input, Span<float> left, Span<float> right) =>
        Process(input, default, default, left, right);

    /// <summary>Processes a block, optionally with the chorus feeding into it.</summary>
    /// <param name="input">The mono reverb send bus.</param>
    /// <param name="chorusLeft">Chorus-to-reverb send, left; may be empty.</param>
    /// <param name="chorusRight">Chorus-to-reverb send, right; may be empty.</param>
    /// <param name="left">Receives the wet left channel.</param>
    /// <param name="right">Receives the wet right channel.</param>
    public void Process(
        ReadOnlySpan<float> input,
        ReadOnlySpan<float> chorusLeft,
        ReadOnlySpan<float> chorusRight,
        Span<float> left,
        Span<float> right)
    {
        var p = _preset;
        var diffusers = p.Diffusers;
        var tankA = _tapsA;
        var tankB = _tapsB;
        var nested = p.TankAllpasses;

        for (var i = 0; i < input.Length; i++)
        {
            var wet = input[i]
                + (i < chorusLeft.Length ? chorusLeft[i] : 0f)
                + (i < chorusRight.Length ? chorusRight[i] : 0f);

            // Input conditioner: the DC blocker, then a one-pole damping filter.
            var x = (wet * DcBlocker.InputCoefficient) + _dcState;
            _dcState += x * DcBlocker.StateCoefficient;
            _damp = (_damp * p.DampFeedback) + (p.DampInput * x);
            _ring[(InputTap + _writeCursor) & RingMask] = _damp * p.GainInput;

            // Four series allpass diffusers.
            var injection = _ring[(p.InjectionTap + _writeCursor) & RingMask] * p.GainInjection;
            var previousWrite = 0.0;
            var previousRead = 0.0;
            for (var k = 0; k < diffusers.Length; k++)
            {
                var stage = diffusers[k];
                var read = _ring[(stage.ReadTap + _writeCursor) & RingMask];
                var write = k == 0
                    ? (read * stage.CoefA) + injection
                    : (read * stage.CoefA) + (previousWrite * diffusers[k - 1].CoefB) + previousRead;

                _ring[(stage.WriteTap + _writeCursor) & RingMask] = write;
                previousWrite = write;
                previousRead = read;
            }

            var carry = (previousWrite * diffusers[^1].CoefB) + previousRead;

            RunTank(tankA, p.TankA, nested.A0, nested.A1, carry, p.GainFeedback, ref _tankStateA);
            RunTank(tankB, p.TankB, nested.B0, nested.B1, carry, p.GainFeedback, ref _tankStateB);

            // Output taps are latched before the cursor moves.
            left[i] = (float)((_ring[(tankA.Tap28 + _writeCursor) & RingMask]
                             + _ring[(tankA.Tap20 + _writeCursor) & RingMask]
                             + _ring[(tankB.Tap28 + _writeCursor) & RingMask]
                             + _ring[(tankB.Tap20 + _writeCursor) & RingMask]) * p.GainOutput);

            right[i] = (float)((_ring[(tankA.Tap2C + _writeCursor) & RingMask]
                              + _ring[(tankA.Tap24 + _writeCursor) & RingMask]
                              + _ring[(tankB.Tap2C + _writeCursor) & RingMask]
                              + _ring[(tankB.Tap24 + _writeCursor) & RingMask]) * p.GainOutput);

            _writeCursor = (_writeCursor - 1) & RingMask;
        }
    }

    private void RunTank(
        in TankTaps taps,
        ReverbTank tank,
        AllpassStage first,
        AllpassStage second,
        double carry,
        double feedback,
        ref double state)
    {
        var lowpass = (_ring[(taps.Tap1C + _writeCursor) & RingMask] * tank.CoefB) + (tank.CoefA * state);
        state = lowpass;

        var a = _ring[(first.ReadTap + _writeCursor) & RingMask];
        var p = (lowpass * feedback) + carry + (a * first.CoefA);
        _ring[(first.WriteTap + _writeCursor) & RingMask] = p;
        _ring[(taps.Tap10 + _writeCursor) & RingMask] = (p * first.CoefB) + a;

        var b = _ring[(second.ReadTap + _writeCursor) & RingMask];
        var q = (b * second.CoefA) + _ring[(taps.Tap14 + _writeCursor) & RingMask];
        _ring[(second.WriteTap + _writeCursor) & RingMask] = q;
        _ring[(taps.Tap18 + _writeCursor) & RingMask] = (q * second.CoefB) + b;
    }
}

/// <summary>
/// The GS chorus: a modulated delay line with two anti-phase taps.
/// </summary>
/// <remarks>
/// A 24-bit signed sawtooth drives a triangle from its magnitude; the two taps read 180° apart, which
/// is what makes the output stereo from a mono send. The engine has a companion right-hand stage, but
/// it is gated off for every GS type, so the left stage alone is correct.
/// </remarks>
public sealed class Chorus : IEffect
{
    /// <summary>Size of the delay ring.</summary>
    public const int RingSize = 65536;

    private const int RingMask = RingSize - 1;
    private const int PhaseSign = 1 << 24;
    private const int PhaseHalf = 1 << 23;

    private readonly ChorusPreset _preset;
    private readonly double[] _ring = new double[RingSize];

    private int _phase;
    private double _dcState;
    private double _lowpass;
    private double _feedback;
    private int _writeCursor;

    /// <summary>Creates a chorus.</summary>
    /// <param name="preset">Coefficients for the GS type.</param>
    public Chorus(ChorusPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        _preset = preset;
    }

    /// <summary>Creates a chorus for a GS type.</summary>
    /// <param name="type">GS chorus type 0–7, or <see langword="null"/> for the default.</param>
    /// <returns>The chorus.</returns>
    public static Chorus ForType(int? type)
    {
        var presets = EffectPresets.Default.Chorus;
        return new Chorus(type is null ? presets.Default : presets.Types[type.Value]);
    }

    /// <summary>The send-bus gain for a controller value.</summary>
    /// <param name="controller">CC#93.</param>
    /// <returns>The gain.</returns>
    public static double SendGain(int controller) =>
        ChorusPresets.SendAtFullScale * (controller / 127.0);

    /// <inheritdoc/>
    public void Reset()
    {
        Array.Clear(_ring);
        _phase = 0;
        _dcState = _lowpass = _feedback = 0.0;
        _writeCursor = 0;
    }

    /// <inheritdoc/>
    public void Process(ReadOnlySpan<float> input, Span<float> left, Span<float> right)
    {
        var p = _preset;

        for (var i = 0; i < input.Length; i++)
        {
            _phase = (_phase + p.LfoIncrement) & (PhaseSign - 1);
            var signed = _phase >= PhaseHalf ? _phase - PhaseSign : _phase;

            var x = (input[i] * DcBlocker.InputCoefficient) + _dcState;
            _dcState += x * DcBlocker.StateCoefficient;
            _lowpass = (_lowpass * p.LpfA) + (p.LpfB * x);

            _ring[_writeCursor & RingMask] = ((_feedback * p.Feedback) + _lowpass) * p.GainWrite;

            var wet1 = ReadTap(p.Tap1Depth, p.Tap1Base, Math.Abs(signed));

            // The second tap runs 180 degrees out of phase with the first.
            var opposite = (_phase - PhaseHalf) & (PhaseSign - 1);
            var signed2 = opposite >= PhaseHalf ? opposite - PhaseSign : opposite;
            var wet2 = ReadTap(p.Tap2Depth, p.Tap2Base, Math.Abs(signed2));

            _writeCursor = (_writeCursor - 1) & RingMask;
            _feedback = wet1;

            left[i] = (float)(wet1 * p.GainTap);
            right[i] = (float)(wet2 * p.GainTap);
        }
    }

    private double ReadTap(int depth, int baseDelay, int triangle)
    {
        // The delay offset is 12.12 fixed point, linearly interpolated between adjacent samples.
        // The product must be widened: the triangle reaches 2^23 and the depth several thousand, so
        // a 32-bit multiply wraps and lands the tap at the wrong delay entirely.
        var offset = (int)((((long)depth * triangle) >> 14) + baseDelay);
        var index = (offset >> 12) + _writeCursor;
        var fraction = (0x1000 - (offset & 0xFFF)) / 4096.0;

        return ((1.0 - fraction) * _ring[(index + 1) & RingMask]) + (fraction * _ring[index & RingMask]);
    }
}
