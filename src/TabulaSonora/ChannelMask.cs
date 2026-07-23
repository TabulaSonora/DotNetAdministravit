using System.Threading;

namespace TabulaSonora;

/// <summary>
/// Per-channel mute and solo state.
/// </summary>
/// <remarks>
/// <para>
/// Both flags are held as bitmasks over the 16 MIDI channels and read and written with
/// <see cref="Volatile"/>, so a user interface can toggle them from its own thread while a render or
/// audio callback reads them, without locking and without tearing. A UI should hold one instance and
/// mutate it; the engine only ever reads.
/// </para>
/// <para>
/// Solo takes precedence over nothing: the rule is that a channel sounds when it is not muted
/// <em>and</em> either nothing is soloed or it is one of the soloed channels. That means muting a
/// soloed channel still silences it, which is what a mixer does and what surprises people least.
/// </para>
/// </remarks>
public sealed class ChannelMask
{
    /// <summary>Number of MIDI channels.</summary>
    public const int ChannelCount = 16;

    private const int AllChannels = (1 << ChannelCount) - 1;

    private int _muted;
    private int _soloed;

    /// <summary>Creates a mask with every channel audible.</summary>
    public ChannelMask()
    {
    }

    private ChannelMask(int muted, int soloed)
    {
        _muted = muted;
        _soloed = soloed;
    }

    /// <summary>Raw mute bitmask, bit <c>n</c> for channel <c>n</c>.</summary>
    public int MutedMask => Volatile.Read(ref _muted);

    /// <summary>Raw solo bitmask, bit <c>n</c> for channel <c>n</c>.</summary>
    public int SoloedMask => Volatile.Read(ref _soloed);

    /// <summary>Whether any channel is soloed, which puts the mask into solo mode.</summary>
    public bool AnySoloed => SoloedMask != 0;

    /// <summary>Whether every channel is audible.</summary>
    public bool IsDefault => MutedMask == 0 && SoloedMask == 0;

    /// <summary>Whether a channel is muted.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <returns>Whether it is muted.</returns>
    public bool IsMuted(int channel) => (MutedMask & Bit(channel)) != 0;

    /// <summary>Whether a channel is soloed.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <returns>Whether it is soloed.</returns>
    public bool IsSoloed(int channel) => (SoloedMask & Bit(channel)) != 0;

    /// <summary>Whether a channel currently sounds.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <returns>Whether it is audible.</returns>
    public bool IsAudible(int channel)
    {
        var bit = Bit(channel);
        if ((MutedMask & bit) != 0)
        {
            return false;
        }

        var soloed = SoloedMask;
        return soloed == 0 || (soloed & bit) != 0;
    }

    /// <summary>Mutes or unmutes a channel.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <param name="muted">Whether it should be muted.</param>
    public void SetMuted(int channel, bool muted) => Update(ref _muted, Bit(channel), muted);

    /// <summary>Solos or unsolos a channel.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <param name="soloed">Whether it should be soloed.</param>
    public void SetSoloed(int channel, bool soloed) => Update(ref _soloed, Bit(channel), soloed);

    /// <summary>Flips a channel's mute state.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <returns>The new state.</returns>
    public bool ToggleMuted(int channel)
    {
        var next = !IsMuted(channel);
        SetMuted(channel, next);
        return next;
    }

    /// <summary>Flips a channel's solo state.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <returns>The new state.</returns>
    public bool ToggleSoloed(int channel)
    {
        var next = !IsSoloed(channel);
        SetSoloed(channel, next);
        return next;
    }

    /// <summary>Solos exactly one channel, clearing any other solo.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    public void SoloOnly(int channel)
    {
        ValidateChannel(channel);
        Volatile.Write(ref _soloed, Bit(channel));
    }

    /// <summary>Clears every solo, leaving mutes alone.</summary>
    public void ClearSolo() => Volatile.Write(ref _soloed, 0);

    /// <summary>Clears every mute, leaving solos alone.</summary>
    public void ClearMutes() => Volatile.Write(ref _muted, 0);

    /// <summary>Makes every channel audible again.</summary>
    public void Reset()
    {
        Volatile.Write(ref _muted, 0);
        Volatile.Write(ref _soloed, 0);
    }

    /// <summary>Mutes every channel.</summary>
    public void MuteAll() => Volatile.Write(ref _muted, AllChannels);

    /// <summary>
    /// Takes a point-in-time copy, so a render sees consistent state even if the UI keeps toggling.
    /// </summary>
    /// <returns>An independent copy.</returns>
    public ChannelMask Snapshot() => new(MutedMask, SoloedMask);

    /// <summary>Lists the channels that currently sound.</summary>
    /// <returns>The audible channel numbers, ascending.</returns>
    public IEnumerable<int> AudibleChannels()
    {
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            if (IsAudible(channel))
            {
                yield return channel;
            }
        }
    }

    private static void Update(ref int field, int bit, bool set)
    {
        // Compare-and-swap so two toggles from different threads cannot lose each other.
        int current, updated;
        do
        {
            current = Volatile.Read(ref field);
            updated = set ? current | bit : current & ~bit;
        }
        while (Interlocked.CompareExchange(ref field, updated, current) != current);
    }

    private static int Bit(int channel)
    {
        ValidateChannel(channel);
        return 1 << channel;
    }

    private static void ValidateChannel(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, ChannelCount);
    }
}
