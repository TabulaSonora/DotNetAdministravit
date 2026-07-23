namespace TabulaSonora.Rom;

/// <summary>
/// The two raw streams that make up one wave: a signed delta per sample, and a shift exponent per
/// 16-sample block packed two to a byte.
/// </summary>
/// <param name="Delta">
/// One signed byte per sample, <see cref="SampleCount"/> + 1 long — the ping-pong sampler turns
/// around <em>on</em> index <see cref="SampleCount"/>, so that extra step is read.
/// </param>
/// <param name="Scale">
/// Shift-exponent nibbles: byte <c>i &gt;&gt; 5</c> holds the exponents for samples <c>i</c>, with the
/// low nibble used when <c>(i &gt;&gt; 4) &amp; 1</c> is zero and the high nibble otherwise.
/// </param>
/// <param name="SampleCount">Number of decodable samples.</param>
/// <param name="AlignedLoop">The descriptor's loop start rounded down to a 32-sample boundary.</param>
public readonly record struct WaveStreams(
    byte[] Delta,
    byte[] Scale,
    int SampleCount,
    int AlignedLoop);

/// <summary>
/// The 24 MB wave ROM embedded in <c>SCCore.dll</c> — the literal Sound Canvas hardware mask ROM,
/// two banks of 12 MB addressed in 1 MB regions.
/// </summary>
/// <remarks>
/// Bank A is the stacked SC-88/SC-88Pro ROM, bank B the SC-8820 ROM. Within a region the shift
/// exponents live at the bottom (one byte per 32 samples, so 32 KB covers the whole 1 MB) while the
/// deltas are addressed directly by sample index.
/// </remarks>
public sealed class WaveRom
{
    /// <summary>Size of one addressable ROM region, 1 MB.</summary>
    public const int RegionSize = 0x100000;

    /// <summary>Largest wave this class will attempt to read, as a sanity bound.</summary>
    public const int MaxSampleCount = 2_000_000;

    /// <summary>Regions of real ROM data in bank A.</summary>
    public const int BankARegionCount = 12;

    /// <summary>
    /// Regions of real ROM data in bank B — eight, not twelve.
    /// </summary>
    /// <remarks>
    /// The manifest records both banks as 12 MB, but bank B's data ends at file offset
    /// <c>0x1892730</c>: regions 0–3 are the tail of the 1996 <c>rom_make</c> image and regions 4–7
    /// are the 1999 <c>8820_wv0</c> image. The declared 12 MB span runs past the end of the file
    /// altogether, so the nominal size cannot be used as a bound.
    /// </remarks>
    public const int BankBRegionCount = 8;

    /// <summary>Number of regions of real ROM data in a bank.</summary>
    /// <param name="bank">0 for bank A, 1 for bank B.</param>
    /// <returns>The region count.</returns>
    public static int RegionCount(int bank) => bank == 0 ? BankARegionCount : BankBRegionCount;

    private readonly RomImage _rom;
    private readonly long _bankABase;
    private readonly long _bankBBase;

    /// <summary>Creates a wave-ROM view over an open image.</summary>
    /// <param name="rom">An open, verified ROM image.</param>
    public WaveRom(RomImage rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        _rom = rom;
        _bankABase = rom.Manifest.Region("wave_rom_bank_A").FileOffset;
        _bankBBase = rom.Manifest.Region("wave_rom_bank_B").FileOffset;
    }

    /// <summary>File offset of the first byte of a bank.</summary>
    /// <param name="bank">0 for bank A, 1 for bank B.</param>
    /// <returns>The offset within the DLL file.</returns>
    public long BankBase(int bank) => bank == 0 ? _bankABase : _bankBBase;

    /// <summary>Splits a descriptor's region byte into a bank and a bank-relative region index.</summary>
    /// <param name="region">The descriptor's region byte.</param>
    /// <returns>The bank (0 or 1) and the effective region within it.</returns>
    public static (int Bank, int EffectiveRegion) SplitRegion(int region)
    {
        var bank = (region >> 4) & 1;
        return (bank, region - (16 * bank));
    }

    /// <summary>File offset of the base of a region.</summary>
    /// <param name="region">The descriptor's region byte.</param>
    /// <returns>The offset within the DLL file.</returns>
    public long RegionBase(int region)
    {
        var (bank, effective) = SplitRegion(region);
        return BankBase(bank) + ((long)effective * RegionSize);
    }

    /// <summary>
    /// Reads the delta and scale streams for one wave.
    /// </summary>
    /// <param name="region">Descriptor region byte.</param>
    /// <param name="loop">Descriptor <c>loop</c> field — the data start, i.e. delta index 0.</param>
    /// <param name="start">Descriptor <c>start</c> field — the physical end of the data.</param>
    /// <returns>
    /// The streams, or <see langword="null"/> when the descriptor describes no usable data (a
    /// non-positive or implausibly large length).
    /// </returns>
    /// <remarks>
    /// The descriptor's field names are as recovered, and they are confusing: <c>loop</c> is the data
    /// start, <c>end</c> is the loop point, and <c>start</c> is the physical end.
    /// </remarks>
    public WaveStreams? ReadStreams(int region, int loop, int start)
    {
        var alignedLoop = loop & ~0x1f;
        var sampleCount = start - alignedLoop;
        if (sampleCount <= 0 || sampleCount > MaxSampleCount)
        {
            return null;
        }

        var regionBase = RegionBase(region);
        var deltaOffset = regionBase + alignedLoop;
        var scaleOffset = regionBase + (alignedLoop >> 5);

        // One extra delta: the ping-pong sampler applies the step at the turnaround index.
        var deltaLength = sampleCount + 1;
        var scaleLength = (deltaLength >> 5) + 4;

        var delta = _rom.Read(deltaOffset, deltaLength);
        var scale = _rom.Read(scaleOffset, scaleLength);

        return new WaveStreams(delta, scale, sampleCount, alignedLoop);
    }
}
