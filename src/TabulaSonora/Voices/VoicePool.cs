namespace TabulaSonora.Voices;

/// <summary>What a voice slot is currently doing.</summary>
public enum VoiceState
{
    /// <summary>Unused and available.</summary>
    Free,

    /// <summary>Sounding with the key still down.</summary>
    Held,

    /// <summary>Sounding through its release after note-off.</summary>
    Releasing,
}

/// <summary>
/// A handle to one voice slot: an index into the pool's parallel arrays plus the control-rate
/// operations that act on it.
/// </summary>
/// <remarks>
/// Deliberately a handle rather than an object holding the render state. The per-voice DSP state
/// lives in struct-of-arrays form on the pool, which is the shape the engine itself uses — it renders
/// voices in groups of four. Objects appear at the control-rate seams, where a virtual call costs
/// nothing; the per-sample loop underneath stays flat.
/// </remarks>
public readonly record struct Voice(VoicePool Pool, int Index)
{
    /// <summary>What this slot is doing.</summary>
    public VoiceState State => Pool.StateOf(Index);

    /// <summary>MIDI channel that owns the voice.</summary>
    public int Channel => Pool.ChannelOf(Index);

    /// <summary>MIDI note that started the voice.</summary>
    public int Note => Pool.NoteOf(Index);

    /// <summary>MIDI velocity the voice started with.</summary>
    public int Velocity => Pool.VelocityOf(Index);

    /// <summary>The note group this voice belongs to; partials of one note share it.</summary>
    public int NoteGroup => Pool.NoteGroupOf(Index);

    /// <summary>Whether the voice is producing sound.</summary>
    public bool IsActive => State != VoiceState.Free;
}

/// <summary>
/// The 64-voice allocator.
/// </summary>
/// <remarks>
/// <para>
/// Polyphony is 64, and a note consumes one voice per sounding partial — so a two-partial patch
/// halves the effective note count. Partials of one note share a <em>note group</em> so that stealing
/// removes a whole note rather than leaving a half-sounding remnant, which is what the engine's
/// pair-aware stealing achieves.
/// </para>
/// <para>
/// The stealing <em>policy</em> here — prefer a free slot, then the oldest releasing note, then the
/// oldest held note — is a documented approximation. The engine's own allocator
/// (<c>note_group_alloc</c>, <c>voice_steal_run</c>, <c>voice_steal_oldest</c>) was located and named
/// during the reverse engineering but its selection rules were never traced, so this is not claimed
/// to be bit-faithful. It is isolated here so it can be replaced without touching the DSP.
/// </para>
/// </remarks>
public sealed class VoicePool
{
    /// <summary>Total voice slots.</summary>
    public const int MaxVoices = 64;

    /// <summary>Voices are rendered in groups of this size; the engine's layout is SIMD-shaped.</summary>
    public const int GroupSize = 4;

    private readonly VoiceState[] _state = new VoiceState[MaxVoices];
    private readonly int[] _channel = new int[MaxVoices];
    private readonly int[] _note = new int[MaxVoices];
    private readonly int[] _velocity = new int[MaxVoices];
    private readonly int[] _noteGroup = new int[MaxVoices];
    private readonly long[] _sequence = new long[MaxVoices];

    private long _counter;
    private int _nextNoteGroup;

    /// <summary>
    /// Called with the index of each slot taken from a sounding note, before it is reassigned.
    /// </summary>
    /// <remarks>
    /// A host that renders voices needs this: the slot is about to be overwritten, so whatever was
    /// sounding in it has to be moved somewhere it can fade out. Cutting it dead instead clicks.
    /// </remarks>
    public Action<int>? Stealing { get; set; }

    /// <summary>Number of slots currently sounding.</summary>
    public int ActiveCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < MaxVoices; i++)
            {
                if (_state[i] != VoiceState.Free)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Starts a new note group, to be shared by that note's partials.</summary>
    /// <returns>The group identifier.</returns>
    public int BeginNoteGroup() => ++_nextNoteGroup;

    /// <summary>
    /// Allocates one voice, stealing if necessary.
    /// </summary>
    /// <param name="channel">MIDI channel.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="noteGroup">Group from <see cref="BeginNoteGroup"/>.</param>
    /// <returns>The allocated voice.</returns>
    public Voice Allocate(int channel, int note, int velocity, int noteGroup)
    {
        var index = FindFree();
        if (index < 0)
        {
            index = FindOldest(VoiceState.Releasing);
        }

        if (index < 0)
        {
            index = FindOldest(VoiceState.Held);
        }

        if (index < 0)
        {
            index = 0;
        }

        if (_state[index] != VoiceState.Free)
        {
            // Take the whole note, not half of it: a surviving partial of a stolen note would keep
            // sounding on its own.
            StealGroup(_noteGroup[index], index);
            Stealing?.Invoke(index);
        }

        _state[index] = VoiceState.Held;
        _channel[index] = channel;
        _note[index] = note;
        _velocity[index] = velocity;
        _noteGroup[index] = noteGroup;
        _sequence[index] = ++_counter;

        return new Voice(this, index);
    }

    /// <summary>Moves every held voice for a note into its release.</summary>
    /// <param name="channel">MIDI channel.</param>
    /// <param name="note">MIDI note.</param>
    /// <returns>How many voices were released.</returns>
    public int Release(int channel, int note)
    {
        var released = 0;
        for (var i = 0; i < MaxVoices; i++)
        {
            if (_state[i] == VoiceState.Held && _channel[i] == channel && _note[i] == note)
            {
                _state[i] = VoiceState.Releasing;
                released++;
            }
        }

        return released;
    }

    /// <summary>Frees a voice whose release has finished.</summary>
    /// <param name="index">Slot index.</param>
    public void Free(int index) => _state[index] = VoiceState.Free;

    /// <summary>Frees every voice immediately.</summary>
    public void Reset()
    {
        Array.Fill(_state, VoiceState.Free);
        _counter = 0;
        _nextNoteGroup = 0;
    }

    /// <summary>Enumerates the sounding voices.</summary>
    /// <returns>Each active voice.</returns>
    public IEnumerable<Voice> Active()
    {
        for (var i = 0; i < MaxVoices; i++)
        {
            if (_state[i] != VoiceState.Free)
            {
                yield return new Voice(this, i);
            }
        }
    }

    /// <summary>The state of a slot.</summary>
    /// <param name="index">Slot index.</param>
    /// <returns>The state.</returns>
    public VoiceState StateOf(int index) => _state[index];

    /// <summary>The channel of a slot.</summary>
    /// <param name="index">Slot index.</param>
    /// <returns>The channel.</returns>
    public int ChannelOf(int index) => _channel[index];

    /// <summary>The note of a slot.</summary>
    /// <param name="index">Slot index.</param>
    /// <returns>The note.</returns>
    public int NoteOf(int index) => _note[index];

    /// <summary>The velocity of a slot.</summary>
    /// <param name="index">Slot index.</param>
    /// <returns>The velocity.</returns>
    public int VelocityOf(int index) => _velocity[index];

    /// <summary>The note group of a slot.</summary>
    /// <param name="index">Slot index.</param>
    /// <returns>The group identifier.</returns>
    public int NoteGroupOf(int index) => _noteGroup[index];

    private int FindFree()
    {
        for (var i = 0; i < MaxVoices; i++)
        {
            if (_state[i] == VoiceState.Free)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindOldest(VoiceState state)
    {
        var best = -1;
        var oldest = long.MaxValue;
        for (var i = 0; i < MaxVoices; i++)
        {
            if (_state[i] == state && _sequence[i] < oldest)
            {
                oldest = _sequence[i];
                best = i;
            }
        }

        return best;
    }

    private void StealGroup(int noteGroup, int except)
    {
        if (noteGroup == 0)
        {
            return;
        }

        for (var i = 0; i < MaxVoices; i++)
        {
            if (i != except && _noteGroup[i] == noteGroup && _state[i] != VoiceState.Free)
            {
                _state[i] = VoiceState.Free;
                Stealing?.Invoke(i);
            }
        }
    }
}
