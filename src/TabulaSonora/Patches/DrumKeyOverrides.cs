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
/// <b>The pitch unit is not the kit plane's unit.</b> The kit's coarse-pitch plane runs at 50 cents
/// per step — see <see cref="DrumKitTable.CoarsePitchRatio"/> — but the NRPN is a whole semitone per
/// step, so the offset is doubled on the way in. Measured against the DLL by sweeping the NRPN over
/// a tonal key (41, Low Floor Tom, whose fundamental autocorrelates cleanly) and reading the
/// resulting pitch: entry 70 (+6) moved it +5.77 semitones and entry 76 (+12) moved it +11.48, both
/// a semitone per step rather than the half-step the plane alone would give.
/// </para>
/// <para>
/// The engine floors the playback rate, but <em>not</em> at any fixed offset: key 41 stops moving
/// below entry 16 (−48 semitones) while key 55 is still distinct at entry 0 (−64), so the limit is
/// an absolute rate rather than a pitch offset. That floor is not reproduced here — it was measured
/// but not located, and no General MIDI kit reaches it through a value a file can send.
/// </para>
/// </remarks>
public sealed class DrumKeyOverrides
{
    /// <summary>Data-entry value meaning "no change" for both parameters.</summary>
    public const int Centre = 0x40;

    private const int KeyCount = DrumKitTable.KeyCount;

    private readonly int[] _pitch = new int[KeyCount];
    private readonly int[] _pan = new int[KeyCount];
    private uint _panState;
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
        _panState = 0;
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
    /// <remarks>
    /// <para>
    /// <b>The spread is an invention, isolated here so it can be replaced.</b> The engine's own
    /// generator was not traced, so the sequence cannot match; what is reproduced is that successive
    /// strikes land in different places rather than stacking in one. It is driven by a counter and a
    /// fixed multiplier rather than <see cref="Random"/>, so a render stays reproducible — an
    /// unseeded generator would make every render of the same file different and every fixture
    /// comparison meaningless.
    /// </para>
    /// <para>
    /// The range is 1 to 127, skipping 0 so a resolved value never re-enters this branch.
    /// </para>
    /// </remarks>
    public int? PanForHit(int note)
    {
        if (Pan(note) is not { } pan)
        {
            return null;
        }

        if (pan != RandomPan)
        {
            return pan;
        }

        // A 32-bit LCG step, taking the high bits, which are the well-distributed ones.
        _panState = (_panState * 1664525) + 1013904223;
        return 1 + (int)((_panState >> 16) % 127);
    }

    /// <summary>Layers this part's overrides over a key read from the kit record, for one strike.</summary>
    /// <param name="key">The key as the kit stores it.</param>
    /// <param name="note">MIDI note number.</param>
    /// <returns>The key as the part should sound this strike.</returns>
    /// <remarks>This advances the random-pan spread, so call it once per strike.</remarks>
    public DrumKey Apply(DrumKey key, int note) => Apply(key, PitchOffset(note), PanForHit(note));

    /// <summary>Layers a latched pair of overrides over a key read from the kit record.</summary>
    /// <param name="key">The key as the kit stores it.</param>
    /// <param name="pitchOffset">Coarse-pitch offset in NRPN steps, zero for none.</param>
    /// <param name="pan">Panpot, or <see langword="null"/> for none.</param>
    /// <returns>The key as the part should sound it.</returns>
    /// <remarks>
    /// The plane is deliberately left unclamped. The engine does not stop at zero — key 55 sits at
    /// plane 60 and still changes pitch at entry 0, which drives the sum to −68 — so clamping here
    /// would silently flatten the bottom of the range a file can actually use.
    /// </remarks>
    public static DrumKey Apply(DrumKey key, int pitchOffset, int? pan) => key with
    {
        Pitch = key.Pitch + (2 * pitchOffset),
        Pan = pan ?? key.Pan,
    };
}
