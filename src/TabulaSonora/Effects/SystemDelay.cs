namespace TabulaSonora.Effects;

/// <summary>Compiled delay coefficients: tap positions in samples, gains, and feedback.</summary>
/// <param name="CentreSamples">Centre tap delay.</param>
/// <param name="LeftSamples">Left tap delay.</param>
/// <param name="RightSamples">Right tap delay.</param>
/// <param name="CentreGain">Centre tap output gain, including the return level.</param>
/// <param name="LeftGain">Left tap output gain.</param>
/// <param name="RightGain">Right tap output gain.</param>
/// <param name="Feedback">Feedback coefficient, −1 to about +1.</param>
/// <param name="PreLowPass">Pre-lowpass index; 0 bypasses.</param>
/// <param name="SendToReverb">How much of the delay output feeds the reverb.</param>
public readonly record struct DelayParameters(
    int CentreSamples,
    int LeftSamples,
    int RightSamples,
    double CentreGain,
    double LeftGain,
    double RightGain,
    double Feedback,
    int PreLowPass,
    double SendToReverb);

/// <summary>
/// The GS system delay — the third send effect.
/// </summary>
/// <remarks>
/// <para>
/// A single mono feedback ring tapped at three points. Unlike the reverb and chorus this is not a
/// standalone function in the engine: it is woven into the effects matrix and its delay lines, so
/// this is a structural model rather than a line-for-line transcription.
/// </para>
/// <para>
/// The fixed 60 ms input pre-delay is not in the preset table and has to be applied separately.
/// Without it the envelope correlation against the engine sits around 0.6; with it, 0.99. Because the
/// feedback line is time-invariant, delaying the input is equivalent to shifting the output, which is
/// how it is applied here.
/// </para>
/// </remarks>
public sealed class SystemDelay : IEffect
{
    /// <summary>Size of the delay ring; large enough for the longest delay plus headroom.</summary>
    public const int RingSize = 1 << 17;

    private const int RingMask = RingSize - 1;

    private readonly DelayParameters _parameters;
    private readonly double[] _ring = new double[RingSize];
    private readonly int _preDelay;
    private readonly double _preLowPassCoefficient;

    private readonly float[] _pendingLeft;
    private readonly float[] _pendingRight;
    private int _pending;

    private double _lowpass;
    private int _writeCursor;

    /// <summary>Creates a delay.</summary>
    /// <param name="parameters">Compiled coefficients.</param>
    public SystemDelay(DelayParameters parameters)
    {
        _parameters = parameters;
        _preDelay = DelayPresets.PreDelaySamples;
        _preLowPassCoefficient = parameters.PreLowPass is >= 0 and < 8
            ? DelayPresets.PreLowPassCoefficients[parameters.PreLowPass]
            : 0.0;

        _pendingLeft = new float[_preDelay];
        _pendingRight = new float[_preDelay];
    }

    /// <summary>Creates a delay for a GS type.</summary>
    /// <param name="type">GS delay type 0–9.</param>
    /// <returns>The delay.</returns>
    public static SystemDelay ForType(int type) => new(Compile(EffectPresets.Default.Delay.RawPresets[type]));

    /// <summary>The send-bus gain for a part's delay send.</summary>
    /// <param name="send">Part delay send, 0–127.</param>
    /// <returns>The gain.</returns>
    /// <remarks>There is no Control Change for this: the delay send is reachable only over SysEx.</remarks>
    public static double SendGain(int send) =>
        DelayPresets.SendAtFullScale * (send / 127.0);

    /// <summary>Converts a raw time value to milliseconds.</summary>
    /// <param name="raw">Raw value, 1–115.</param>
    /// <returns>The time in milliseconds.</returns>
    public static double TimeMilliseconds(int raw) =>
        EffectPresets.Default.Delay.TimeMilliseconds[Math.Clamp(raw, 1, 115) - 1];

    /// <summary>Converts a raw ratio value to a percentage.</summary>
    /// <param name="raw">Raw value, 1–120.</param>
    /// <returns>The ratio in percent.</returns>
    public static double RatioPercent(int raw) =>
        EffectPresets.Default.Delay.RatioPercent[Math.Clamp(raw, 1, 120) - 1];

    /// <summary>
    /// Compiles ten raw GS parameters into DSP coefficients.
    /// </summary>
    /// <param name="raw">The raw parameter row.</param>
    /// <param name="sampleRate">Internal sample rate.</param>
    /// <returns>The compiled parameters.</returns>
    public static DelayParameters Compile(int[] raw, int sampleRate = 32000)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var centre = (int)Math.Round(TimeMilliseconds(raw[1]) * sampleRate / 1000.0);
        var left = (int)Math.Round(centre * RatioPercent(raw[2]) / 100.0);
        var right = (int)Math.Round(centre * RatioPercent(raw[3]) / 100.0);

        // The return level scales the whole wet output and is kept separate from the send.
        var returnLevel = raw[7] / 127.0;

        return new DelayParameters(
            CentreSamples: Math.Max(1, centre),
            LeftSamples: Math.Max(1, left),
            RightSamples: Math.Max(1, right),
            CentreGain: raw[4] / 127.0 * returnLevel,
            LeftGain: raw[5] / 127.0 * returnLevel,
            RightGain: raw[6] / 127.0 * returnLevel,
            // Raw 0-127 maps to -1..+1; the display range is -64..+63.
            Feedback: (raw[8] - 64) / 64.0,
            PreLowPass: raw[0],
            SendToReverb: raw[9] / 127.0);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        Array.Clear(_ring);
        Array.Clear(_pendingLeft);
        Array.Clear(_pendingRight);
        _pending = 0;
        _lowpass = 0.0;
        _writeCursor = 0;
    }

    /// <inheritdoc/>
    public void Process(ReadOnlySpan<float> input, Span<float> left, Span<float> right)
    {
        var p = _parameters;

        for (var i = 0; i < input.Length; i++)
        {
            var centre = _ring[(_writeCursor - p.CentreSamples) & RingMask];

            var feedbackSource = _preLowPassCoefficient == 0.0
                ? centre
                : _lowpass = (_lowpass * _preLowPassCoefficient) + ((1.0 - _preLowPassCoefficient) * centre);

            _ring[_writeCursor] = input[i] + (p.Feedback * feedbackSource);

            var l = _ring[(_writeCursor - p.LeftSamples) & RingMask];
            var r = _ring[(_writeCursor - p.RightSamples) & RingMask];

            var wetLeft = (float)((p.LeftGain * l) + (p.CentreGain * centre));
            var wetRight = (float)((p.RightGain * r) + (p.CentreGain * centre));

            _writeCursor = (_writeCursor + 1) & RingMask;

            // Apply the fixed input pre-delay as an equivalent output shift.
            if (_pending < _preDelay)
            {
                _pendingLeft[_pending] = wetLeft;
                _pendingRight[_pending] = wetRight;
                _pending++;
                left[i] = 0f;
                right[i] = 0f;
            }
            else
            {
                var slot = (_pending - _preDelay) % Math.Max(1, _preDelay);
                left[i] = _pendingLeft[slot];
                right[i] = _pendingRight[slot];
                _pendingLeft[slot] = wetLeft;
                _pendingRight[slot] = wetRight;
                _pending++;
            }
        }
    }
}
