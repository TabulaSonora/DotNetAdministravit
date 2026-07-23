using TabulaSonora;
using TabulaSonora.Midi;

namespace TabulaSonora.Tests;

/// <summary>
/// Mute and solo routing, and the interaction between them.
/// </summary>
public class ChannelMaskTests
{
    [Fact]
    public void EverythingIsAudibleByDefault()
    {
        var mask = new ChannelMask();

        Assert.True(mask.IsDefault);
        Assert.False(mask.AnySoloed);
        Assert.Equal(16, mask.AudibleChannels().Count());
    }

    [Fact]
    public void MutingSilencesOnlyThatChannel()
    {
        var mask = new ChannelMask();
        mask.SetMuted(3, true);

        Assert.True(mask.IsMuted(3));
        Assert.False(mask.IsAudible(3));
        Assert.True(mask.IsAudible(2));
        Assert.True(mask.IsAudible(4));
        Assert.False(mask.IsDefault);
    }

    [Fact]
    public void SoloingSilencesEverythingElse()
    {
        var mask = new ChannelMask();
        mask.SetSoloed(5, true);

        Assert.True(mask.AnySoloed);
        Assert.True(mask.IsAudible(5));

        for (var channel = 0; channel < ChannelMask.ChannelCount; channel++)
        {
            Assert.Equal(channel == 5, mask.IsAudible(channel));
        }
    }

    [Fact]
    public void MutingASoloedChannelStillSilencesIt()
    {
        // A mixer behaves this way, and it is the least surprising of the options: mute is a
        // statement about one channel, solo a statement about the rest.
        var mask = new ChannelMask();
        mask.SetSoloed(5, true);
        mask.SetMuted(5, true);

        Assert.False(mask.IsAudible(5));
        Assert.Empty(mask.AudibleChannels());
    }

    [Fact]
    public void ClearingSoloRestoresTheUnmutedChannels()
    {
        var mask = new ChannelMask();
        mask.SetMuted(0, true);
        mask.SetSoloed(9, true);

        Assert.Single(mask.AudibleChannels());

        mask.ClearSolo();

        Assert.False(mask.AnySoloed);
        Assert.False(mask.IsAudible(0));
        Assert.Equal(15, mask.AudibleChannels().Count());
    }

    [Fact]
    public void SoloOnlyReplacesAnyPreviousSolo()
    {
        var mask = new ChannelMask();
        mask.SetSoloed(1, true);
        mask.SetSoloed(2, true);
        Assert.Equal(2, mask.AudibleChannels().Count());

        mask.SoloOnly(7);
        Assert.Equal([7], mask.AudibleChannels());
    }

    [Fact]
    public void TogglesReportTheirNewState()
    {
        var mask = new ChannelMask();

        Assert.True(mask.ToggleMuted(4));
        Assert.False(mask.ToggleMuted(4));
        Assert.True(mask.ToggleSoloed(4));
        Assert.False(mask.ToggleSoloed(4));
        Assert.True(mask.IsDefault);
    }

    [Fact]
    public void SnapshotIsIndependentOfLaterChanges()
    {
        // A render takes a snapshot so a UI toggling mid-render cannot make some notes of a part
        // sound and others not.
        var live = new ChannelMask();
        live.SetMuted(2, true);

        var snapshot = live.Snapshot();
        live.Reset();
        live.SetMuted(11, true);

        Assert.False(snapshot.IsAudible(2));
        Assert.True(snapshot.IsAudible(11));
        Assert.True(live.IsAudible(2));
        Assert.False(live.IsAudible(11));
    }

    [Fact]
    public void RejectsChannelsOutsideTheSixteen()
    {
        var mask = new ChannelMask();

        Assert.Throws<ArgumentOutOfRangeException>(() => mask.SetMuted(-1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.SetMuted(16, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.IsAudible(99));
    }

    [SkippableFact]
    public void MutingAChannelRemovesItsNotesFromARender()
    {
        var renderer = VoiceRenderTests.SharedSequenceRenderer();

        var events = new List<MidiEvent>
        {
            new(0, MidiEventKind.Channel, 0xC0, 0, 0, null),        // ch1 piano
            new(0, MidiEventKind.Channel, 0xC1, 73, 0, null),       // ch2 flute
            new(320, MidiEventKind.Channel, 0x90, 60, 100, null),
            new(320, MidiEventKind.Channel, 0x91, 72, 100, null),
            new(16000, MidiEventKind.Channel, 0x80, 60, 0, null),
            new(16000, MidiEventKind.Channel, 0x81, 72, 0, null),
        };

        var sequence = SequenceBuilder.Build(events);
        var options = new RenderOptions { TailSeconds = 0.3, Reverb = false, Chorus = false, Delay = false };

        var both = renderer.Render(sequence, options);
        Assert.Equal(2, both.NoteCount);

        var muted = new ChannelMask();
        muted.SetMuted(1, true);
        var one = renderer.Render(sequence, options with { Channels = muted });

        Assert.Equal(1, one.NoteCount);
        Assert.True(one.Peak > 0, "The surviving channel should still sound.");
        Assert.True(one.Peak < both.Peak, "Removing a part should not make the mix louder.");

        var silent = new ChannelMask();
        silent.MuteAll();
        var none = renderer.Render(sequence, options with { Channels = silent });

        Assert.Equal(0, none.NoteCount);
        Assert.Equal(0f, none.Peak);
    }
}
