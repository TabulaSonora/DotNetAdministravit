using TabulaSonora.Midi;
using TabulaSonora.Realtime;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// The second MIDI port, and the sixteen parts it reaches.
/// </summary>
/// <remarks>
/// <para>
/// The module allocates thirty-two parts and addresses them as <c>port × 16 + channel</c>, but
/// <c>midi_drain_ready_to_ports</c> masks the port field out of every packet on the way to the FIFO
/// (<c>and r8b,0Fh</c>), so a stock DLL can only ever reach the first sixteen. Widening that mask to
/// <c>0x1f</c> keeps the low bit of the port and admits exactly two — which is what these pin.
/// </para>
/// <para>
/// They assert on part state rather than audio because part selection is all the port decides: the
/// DSP under it is the same objects either way, and <see cref="RealtimeTests"/> already covers that.
/// </para>
/// </remarks>
public class PortTests : IDisposable
{
    private const int Channels = SequenceBuilder.ChannelCount;

    private RomImage? _rom;

    [Fact]
    public void ThirtyTwoPartsAcrossTwoPorts()
    {
        Assert.Equal(2, ToneGenerator.PortCount);
        Assert.Equal(32, ToneGenerator.PartCount);
    }

    [SkippableFact]
    public void PortBDrivesTheSecondSixteenParts()
    {
        var generator = Engine();

        generator.SendChannel(1, 0xC0, 48, 0);
        generator.SendChannel(1, 0xB0, 7, 40);

        Assert.Equal(48, generator.Parts[Channels].Program);
        Assert.Equal(40, generator.Parts[Channels].Volume);

        // The same channel on port A is untouched, which is the whole point of the second port.
        Assert.NotEqual(48, generator.Parts[0].Program);
        Assert.NotEqual(40, generator.Parts[0].Volume);
    }

    [SkippableFact]
    public void APortlessCallerGetsPortA()
    {
        var generator = Engine();

        generator.SendChannel(0xC0 | 3, 48, 0);

        Assert.Equal(48, generator.Parts[3].Program);
        Assert.NotEqual(48, generator.Parts[Channels + 3].Program);
    }

    [SkippableFact]
    public void PortsAboveTheSecondFoldOntoTheTwoThatExist()
    {
        var generator = Engine();

        // 0x1f keeps the low bit of the port and nothing above it, so even ports land on A and odd
        // ones on B rather than indexing parts that were never allocated.
        generator.SendChannel(2, 0xC0, 48, 0);
        generator.SendChannel(3, 0xC0, 52, 0);

        Assert.Equal(48, generator.Parts[0].Program);
        Assert.Equal(52, generator.Parts[Channels].Program);
    }

    [SkippableFact]
    public void APacketCarriesItsOwnPort()
    {
        var generator = Engine();

        // USB-MIDI Event Packet: (port << 4) | class in the low byte, message in the three above.
        generator.SendPacket(Packet(port: 1, status: 0xC0 | 5, data1: 48, data2: 0));
        generator.SendPacket(Packet(port: 0, status: 0xC0 | 5, data1: 52, data2: 0));

        Assert.Equal(52, generator.Parts[5].Program);
        Assert.Equal(48, generator.Parts[Channels + 5].Program);
    }

    [SkippableFact]
    public void NothingLatchesThePortBetweenMessages()
    {
        var generator = Engine();

        // The module dispatches on each packet's own port field as that packet drains, so a message
        // sent to port B does not leave the engine "on" port B for the one after it.
        generator.SendChannel(1, 0xC0, 48, 0);
        generator.SendChannel(0xC0, 52, 0);

        Assert.Equal(48, generator.Parts[Channels].Program);
        Assert.Equal(52, generator.Parts[0].Program);
    }

    [SkippableFact]
    public void EachPortHasItsOwnDrumKit()
    {
        var renderer = Renderer();
        var generator = new ToneGenerator(renderer);

        // Two programs the kit table actually distinguishes; asserting on a pair it maps to the same
        // kit would pass whether or not the ports were separate.
        var row = generator.EffectiveDrumMapRow;
        var first = Enumerable.Range(0, 128).First(p => renderer.Drums.KitForProgram(p, row) is not null);
        var second = Enumerable.Range(0, 128).First(p =>
            renderer.Drums.KitForProgram(p, row) is { } kit && kit != renderer.Drums.KitForProgram(first, row));

        generator.SendChannel(0, 0xC0 | 9, first, 0);
        generator.SendChannel(1, 0xC0 | 9, second, 0);

        Assert.Equal(renderer.Drums.KitForProgram(first, row), generator.DrumKitFor(0));
        Assert.Equal(renderer.Drums.KitForProgram(second, row), generator.DrumKitFor(1));

        // DrumKit without a port still means port A.
        Assert.Equal(generator.DrumKitFor(0), generator.DrumKit);
    }

    [SkippableFact]
    public void GsPartAddressingIsRelativeToTheArrivingPort()
    {
        var generator = Engine();

        // Delay send on GS block 2, which is channel 1. The same address names channel 1 of
        // whichever port the message came in on.
        generator.SendSysEx(1, DelaySend(block: 2, value: 40));

        Assert.Equal(40, generator.Parts[Channels + 1].DelaySend);
        Assert.NotEqual(40, generator.Parts[1].DelaySend);
    }

    [SkippableFact]
    public void ResetClearsBothPorts()
    {
        var generator = Engine();

        generator.SendChannel(0, 0xB0, 7, 40);
        generator.SendChannel(1, 0xB0, 7, 40);
        generator.Reset();

        Assert.NotEqual(40, generator.Parts[0].Volume);
        Assert.NotEqual(40, generator.Parts[Channels].Volume);
    }

    /// <summary>Releases the ROM image the test opened.</summary>
    public void Dispose()
    {
        _rom?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static int Packet(int port, int status, int data1, int data2) =>
        ((port & 0xF) << 4) | (status << 8) | (data1 << 16) | (data2 << 24);

    private static byte[] DelaySend(int block, int value)
    {
        // F0 41 10 42 12 40 1n 2C vv sum F7.
        byte[] message = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, (byte)(0x10 | block), 0x2C, (byte)value, 0x00, 0xF7];
        var sum = 0;
        for (var i = 5; i < 9; i++)
        {
            sum += message[i];
        }

        message[9] = (byte)((128 - (sum & 0x7F)) & 0x7F);
        return message;
    }

    private NoteRenderer Renderer()
    {
        _rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        return new NoteRenderer(_rom);
    }

    private ToneGenerator Engine() => new(Renderer());
}
