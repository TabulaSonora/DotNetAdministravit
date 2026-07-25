using TabulaSonora.Dsp;
using TabulaSonora.Midi;
using TabulaSonora.Patches;

namespace TabulaSonora.Realtime;

/// <summary>
/// One of the sixteen parts: the channel state a running engine keeps between events.
/// </summary>
/// <remarks>
/// The engine has no MIDI output, so nothing can be read back from it — every piece of channel state
/// has to be tracked here. This is the live counterpart of <see cref="PartTimelines"/>, which records
/// the same values as breakpoints for the offline renderer.
/// </remarks>
public sealed class Part
{
    private int _volume = SequenceBuilder.DefaultVolume;
    private int _expression = SequenceBuilder.DefaultExpression;
    private int _master = SequenceBuilder.DefaultMaster;

    /// <summary>Creates a part in its power-on state.</summary>
    public Part() => Recompute();

    /// <summary>Program in force, from the last program change.</summary>
    public int Program { get; set; }

    /// <summary>Bank select MSB, which carries the variation.</summary>
    public int Bank { get; set; }

    /// <summary>CC#7 volume.</summary>
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            Recompute();
        }
    }

    /// <summary>CC#11 expression.</summary>
    public int Expression
    {
        get => _expression;
        set
        {
            _expression = value;
            Recompute();
        }
    }

    /// <summary>Master volume, which is global but folds into the same law.</summary>
    public int Master
    {
        get => _master;
        set
        {
            _master = value;
            Recompute();
        }
    }

    /// <summary>CC#10 pan.</summary>
    public int Pan { get; set; } = SequenceBuilder.DefaultPan;

    /// <summary>CC#1 modulation.</summary>
    public int Modulation { get; set; }

    /// <summary>CC#64 damper.</summary>
    public int Damper { get; set; }

    /// <summary>Whether the damper is holding notes on.</summary>
    public bool DamperDown => Damper >= 0x40;

    /// <summary>CC#91 reverb send.</summary>
    public int ReverbSend { get; set; } = SequenceBuilder.DefaultReverbSend;

    /// <summary>CC#93 chorus send.</summary>
    public int ChorusSend { get; set; } = SequenceBuilder.DefaultChorusSend;

    /// <summary>Part delay send, which has no Control Change and arrives only over SysEx.</summary>
    public int DelaySend { get; set; }

    /// <summary>Pitch bend, as a 14-bit value with 8192 centred.</summary>
    public int Bend { get; set; } = 8192;

    /// <summary>Bend range in semitones, from RPN 00/00.</summary>
    public int BendRange { get; set; } = 2;

    /// <summary>The selected RPN's MSB.</summary>
    public int RpnMsb { get; set; } = 0x7F;

    /// <summary>The selected RPN's LSB.</summary>
    public int RpnLsb { get; set; } = 0x7F;

    /// <summary>The selected NRPN's MSB.</summary>
    public int NrpnMsb { get; set; } = 0x7F;

    /// <summary>The selected NRPN's LSB, which for the drum parameters is the key number.</summary>
    public int NrpnLsb { get; set; } = 0x7F;

    /// <summary>
    /// Whether a CC#6 data entry commits to the selected NRPN rather than the selected RPN.
    /// </summary>
    /// <remarks>
    /// The two share data entry, so the last selection made decides. Tracking this is what keeps a
    /// file's drum NRPNs out of the bend range.
    /// </remarks>
    public bool DataEntryIsNrpn { get; set; }

    /// <summary>Per-drum-key overrides this part has taken from NRPN.</summary>
    public DrumKeyOverrides DrumKeys { get; } = new();

    /// <summary>
    /// Notes whose release is waiting for the damper to lift.
    /// </summary>
    /// <remarks>
    /// Insertion-ordered rather than a set: notes are released oldest first when the pedal comes up,
    /// which is the order the offline renderer closes them in too.
    /// </remarks>
    public List<int> Sustained { get; } = [];

    /// <summary>The combined volume multiplier, 1.0 with everything at 127.</summary>
    public double VolumeScale { get; private set; }

    /// <summary>The mod wheel's contribution to LFO1 pitch depth, in milli-semitones.</summary>
    public double ModWheelDepth => LfoEngine.ModWheelDepth(Modulation);

    /// <summary>The bend offset in milli-semitones.</summary>
    public double BendMilliSemitones => PitchChain.BendOffsetMilliSemitones(Bend, BendRange);

    /// <summary>Returns the part to its power-on state.</summary>
    public void Reset()
    {
        Program = 0;
        Bank = 0;
        Pan = SequenceBuilder.DefaultPan;
        Modulation = 0;
        Damper = 0;
        ReverbSend = SequenceBuilder.DefaultReverbSend;
        ChorusSend = SequenceBuilder.DefaultChorusSend;
        DelaySend = 0;
        Bend = 8192;
        BendRange = 2;
        RpnMsb = 0x7F;
        RpnLsb = 0x7F;
        NrpnMsb = 0x7F;
        NrpnLsb = 0x7F;
        DataEntryIsNrpn = false;
        DrumKeys.Reset();
        Sustained.Clear();

        _volume = SequenceBuilder.DefaultVolume;
        _expression = SequenceBuilder.DefaultExpression;
        _master = SequenceBuilder.DefaultMaster;
        Recompute();
    }

    /// <summary>Applies CC#121, which resets the controllers a reset message covers.</summary>
    public void ResetControllers()
    {
        Expression = SequenceBuilder.DefaultExpression;
        Bend = 8192;
        Damper = 0;
        Modulation = 0;
    }

    private void Recompute() => VolumeScale = TvaChain.PartVolumeScale(_volume, _expression, _master);
}
