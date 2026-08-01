using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// The envelope hold clock — partial block byte 0x00, which delays a layer's envelope start or
/// suspends the envelope machine entirely for a one-shot.
/// </summary>
public class EnvelopeHoldTests
{
    private static (PatchDirectory Directory, EnvelopeMachine Envelopes) Load()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        return (new PatchDirectory(tables), new EnvelopeMachine(tables));
    }

    [SkippableFact]
    public void OrdinaryPartialsNeverArmTheClock()
    {
        var (directory, envelopes) = Load();

        // Piano 1, both partials: byte 0x00 is zero on almost the whole tone set.
        Assert.Equal(0, envelopes.HoldSamples(directory.GetPartialBySlot(0, 0), 100));
        Assert.Equal(0, envelopes.HoldSamples(directory.GetPartialBySlot(0, 1), 100));
    }

    [SkippableFact]
    public void DelayedLayersHoldForWholeControlTicks()
    {
        var (directory, envelopes) = Load();

        // Piano+Choir1 (tone 8): the choir partial carries clock byte 4 -> g_rate_curve[4] = 31 ms
        // -> 3 control ticks, so the layer enters 960 samples late. Neutral velocity byte, so the
        // hold is velocity-independent.
        Assert.Equal(3 * 320, envelopes.HoldSamples(directory.GetPartialBySlot(8, 1), 100));
        Assert.Equal(3 * 320, envelopes.HoldSamples(directory.GetPartialBySlot(8, 1), 32));

        // Puff Organ (tone 147): clock byte 7 -> 58 ms -> 5 ticks.
        Assert.Equal(5 * 320, envelopes.HoldSamples(directory.GetPartialBySlot(147, 0), 100));

        // Church Org.2 (tone 140): clock byte 2 -> 8 ms, which is under one 10 ms tick — the clock
        // computes zero ticks and never arms. Data carried from hardware, inert here.
        Assert.Equal(0, envelopes.HoldSamples(directory.GetPartialBySlot(140, 1), 100));
    }

    [SkippableFact]
    public void OneShotsHoldForever()
    {
        var (directory, envelopes) = Load();

        // The ".o" variation tones carry 0xff: envelope machine suspended for the voice's life.
        Assert.Equal(EnvelopeMachine.HoldForever, envelopes.HoldSamples(directory.GetPartialBySlot(44, 0), 100));   // Harpsi.o
        Assert.Equal(EnvelopeMachine.HoldForever, envelopes.HoldSamples(directory.GetPartialBySlot(173, 1), 100));  // MandolinTrem
    }
}
