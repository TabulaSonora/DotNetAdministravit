using System.Runtime.CompilerServices;

namespace TabulaSonora.Dsp;

/// <summary>Which output of the state-variable filter a partial takes.</summary>
public enum FilterTap
{
    /// <summary>The partial bypasses the filter entirely.</summary>
    Bypass,

    /// <summary>Lowpass — the <c>low</c> integrator.</summary>
    LowPass,

    /// <summary>Bandpass — the <c>band</c> integrator.</summary>
    BandPass,

    /// <summary>Highpass — <c>in − q·band − low</c>.</summary>
    HighPass,

    /// <summary>Notch — <c>low − high</c>.</summary>
    Notch,
}

/// <summary>
/// The engine's filter: a Chamberlin state-variable filter, with all four responses taken as
/// different taps of the same two-integrator loop.
/// </summary>
/// <remarks>
/// <para>
/// Read from the disassembly rather than inferred: there is no prescale, no oversampling and no
/// reordering. Per sample,
/// </para>
/// <code>
/// low  += f * band;
/// high  = input - (q * band + low);
/// band += f * high;
/// </code>
/// <para>
/// Both coefficients come from tables, so no fitted constant appears anywhere in this path. Note that
/// <c>q</c> is Chamberlin's reciprocal-Q: <em>smaller</em> means more resonant, and the neutral
/// resonance byte gives exactly 1.0.
/// </para>
/// </remarks>
public struct StateVariableFilter
{
    private double _low;
    private double _band;

    /// <summary>The lowpass integrator's current value.</summary>
    public readonly double Low => _low;

    /// <summary>The bandpass integrator's current value.</summary>
    public readonly double Band => _band;

    /// <summary>Clears the filter state.</summary>
    public void Reset()
    {
        _low = 0.0;
        _band = 0.0;
    }

    /// <summary>Advances the filter by one sample and returns the selected tap.</summary>
    /// <param name="input">Input sample.</param>
    /// <param name="f">Frequency coefficient, <c>2·sin(π·fc/fs)</c>.</param>
    /// <param name="q">Reciprocal-Q damping coefficient.</param>
    /// <param name="tap">Which response to return.</param>
    /// <returns>The filtered sample.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Process(double input, double f, double q, FilterTap tap)
    {
        _low += f * _band;
        var high = input - ((q * _band) + _low);
        _band += f * high;

        return tap switch
        {
            FilterTap.LowPass => _low,
            FilterTap.HighPass => high,
            FilterTap.BandPass => _band,
            FilterTap.Notch => _low - high,
            _ => input,
        };
    }
}
