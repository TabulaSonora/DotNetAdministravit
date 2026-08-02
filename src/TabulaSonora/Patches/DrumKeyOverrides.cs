using TabulaSonora.Dsp;

namespace TabulaSonora.Patches;

/// <summary>
/// Per-drum-key parameter overrides a part has taken from NRPN, layered over the kit record.
/// </summary>
/// <remarks>
/// <para>
/// GS addresses individual drum keys through NRPN, with the key number in the parameter LSB. Only the
/// two the engine's own switch acts on and that real files reach for are carried here — coarse pitch
/// (MSB <c>0x18</c>) and panpot (MSB <c>0x1C</c>). Both replace the corresponding plane of the kit
/// record for that one key, leaving every other key alone.
/// </para>
/// <para>
/// <b>The offset lands on the plane one-for-one, clamped to 0–0x7F.</b> That is the engine's own
/// plane write (<c>nrpn_apply</c> case <c>0x18</c>, confirmed by live plane reads: entry +12 moves
/// a stored plane of 60 to 72 on every map). What a step is then <em>worth</em> is the tone's own
/// pitch key-follow, applied downstream in
/// <see cref="Dsp.PitchChain.DrumPitchMilliSemitones"/> — a whole semitone on a 100%-follow tone,
/// half of one at 50%. The earlier sweep that read "a semitone per step" (key 41, Low Floor Tom:
/// entry +6 moved it +5.77 semitones, +12 moved it +11.48) was measuring a 100%-follow tone, and
/// an earlier revision wrongly folded that into a ×2 on the plane itself — which pushed
/// 100%-follow keys twice as far as the engine does, and unclamped drove them into subsonics.
/// </para>
/// <para>
/// The "absolute rate floor" that was measured but never located — key 41 stops moving below
/// entry 16 while key 55 is still distinct at entry 0 — is this clamp's bottom. Keys with
/// different kit planes floor at different <em>offsets</em>, which is what made it look like a
/// rate limit rather than a pitch one: key 41's plane hits zero at a shallower entry than
/// key 55's plane of 60 does.
/// </para>
/// </remarks>
public sealed class DrumKeyOverrides
{
    /// <summary>Data-entry value meaning "no change" for both parameters.</summary>
    public const int Centre = 0x40;

    private const int KeyCount = DrumKitTable.KeyCount;

    private readonly int[] _pitch = new int[KeyCount];
    private readonly int[] _pan = new int[KeyCount];
    private bool _any;

    /// <summary>Creates an empty set, in which every key follows its kit record.</summary>
    public DrumKeyOverrides() => Reset();

    /// <summary>Whether any key has been overridden, letting callers skip the layering entirely.</summary>
    public bool IsEmpty => !_any;

    /// <summary>Clears every override, so all keys follow the kit record again.</summary>
    /// <remarks>
    /// This is MIDI state rather than configuration — a GS reset returns the kit to its stored
    /// values, the same as it returns the part's controllers.
    /// </remarks>
    public void Reset()
    {
        Array.Clear(_pitch);
        Array.Fill(_pan, -1);
        _any = false;
    }

    /// <summary>Records a coarse-pitch override for one key.</summary>
    /// <param name="note">MIDI note number the parameter LSB selected.</param>
    /// <param name="entry">Data entry, <see cref="Centre"/> being no change.</param>
    public void SetPitch(int note, int entry)
    {
        if ((uint)note >= KeyCount)
        {
            return;
        }

        _pitch[note] = entry - Centre;
        _any = true;
    }

    /// <summary>Records a panpot override for one key.</summary>
    /// <param name="note">MIDI note number the parameter LSB selected.</param>
    /// <param name="entry">Data entry, used directly as the key's pan.</param>
    public void SetPan(int note, int entry)
    {
        if ((uint)note >= KeyCount)
        {
            return;
        }

        _pan[note] = entry;
        _any = true;
    }

    /// <summary>The coarse-pitch offset held for a key, in NRPN steps of one semitone.</summary>
    /// <param name="note">MIDI note number.</param>
    /// <returns>The offset, zero when the key is not overridden.</returns>
    public int PitchOffset(int note) => (uint)note < KeyCount ? _pitch[note] : 0;

    /// <summary>The panpot held for a key, as sent.</summary>
    /// <param name="note">MIDI note number.</param>
    /// <returns>The pan, or <see langword="null"/> when the key is not overridden.</returns>
    /// <remarks>
    /// A value of <see cref="RandomPan"/> is returned unresolved; use <see cref="PanForHit"/> to get
    /// the value a strike should actually sound at.
    /// </remarks>
    public int? Pan(int note) => (uint)note < KeyCount && _pan[note] >= 0 ? _pan[note] : null;

    /// <summary>Data-entry value that pans a key randomly on every strike.</summary>
    /// <remarks>
    /// Confirmed against the DLL: four strikes of key 55 at this value came out at R/L 0.936, 0.536,
    /// 0.198 and 0.115, while the same test at 1 gave a silent right channel every time and at 64
    /// gave dead centre every time. The GS panpot scale therefore starts at 1 for hard left, and 0
    /// is not "further left" but a separate mode.
    /// </remarks>
    public const int RandomPan = 0;

    /// <summary>Resolves the panpot for one strike, spreading a randomly-panned key.</summary>
    /// <param name="note">MIDI note number.</param>
    /// <returns>The pan to sound this strike at, or <see langword="null"/> to keep the kit's own.</returns>
    /// <param name="noise">
    /// The engine's shared generator, which supplies the position for a randomly-panned key.
    /// </param>
    /// <remarks>
    /// The spread is the engine's own: a draw from <see cref="EngineNoise"/>, the same generator the
    /// pitch jitter and the random LFO waveforms take from, reduced to a pan position by its top
    /// seven bits. What still cannot match is the <em>sequence</em> under polyphony, since that
    /// depends on how many voices consume draws and in what order.
    /// </remarks>
    public int? PanForHit(int note, EngineNoise noise)
    {
        ArgumentNullException.ThrowIfNull(noise);

        if (Pan(note) is not { } pan)
        {
            return null;
        }

        return pan != RandomPan ? pan : noise.NextPan();
    }

    /// <summary>Layers this part's overrides over a key read from the kit record, for one strike.</summary>
    /// <param name="key">The key as the kit stores it.</param>
    /// <param name="note">MIDI note number.</param>
    /// <param name="noise">The engine's shared generator, for a randomly-panned key.</param>
    /// <returns>The key as the part should sound this strike.</returns>
    /// <remarks>This consumes a draw for a randomly-panned key, so call it once per strike.</remarks>
    public DrumKey Apply(DrumKey key, int note, EngineNoise noise) =>
        Apply(key, PitchOffset(note), PanForHit(note, noise));

    /// <summary>Layers a latched pair of overrides over a key read from the kit record.</summary>
    /// <param name="key">The key as the kit stores it.</param>
    /// <param name="pitchOffset">Coarse-pitch offset in kit-plane steps, zero for none.</param>
    /// <param name="pan">Panpot, or <see langword="null"/> for none.</param>
    /// <returns>The key as the part should sound it.</returns>
    /// <remarks>
    /// The offset adds one-for-one and the sum clamps to 0–0x7F, exactly as the engine writes the
    /// plane. WATRWLD1.MID is the regression this pins: its crash (key 55, entry 24 = −40 steps)
    /// lands at plane 29 on the SC-55 Standard kit's 100%-follow crash tone — 2.3 octaves down and
    /// clearly audible — where the doubled, unclamped form fell five octaves and vanished.
    /// </remarks>
    public static DrumKey Apply(DrumKey key, int pitchOffset, int? pan) => key with
    {
        Pitch = Math.Clamp(key.Pitch + pitchOffset, 0, 0x7F),
        Pan = pan ?? key.Pan,
    };
}
