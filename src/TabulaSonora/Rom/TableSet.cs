using System.Runtime.InteropServices;

namespace TabulaSonora.Rom;

/// <summary>
/// The 48 static tables the engine needs, loaded from <c>SCCore.dll</c> or from a directory of
/// extracted <c>.bin</c> slices.
/// </summary>
/// <remarks>
/// <para>
/// Property names follow the project's symbol labels (<c>g_amp_curve_hi</c> becomes
/// <see cref="AmpCurveHi"/>) so the code greps cleanly against the reverse-engineering notes. Those
/// labels are behavioural hypotheses, not Roland's own names.
/// </para>
/// <para>
/// Element counts are derived from each table's byte length, not from the manifest's <c>shape</c>
/// string — several shapes record a byte count where an element count would be expected
/// (<c>curve_level_2a00.bin</c> is 256 bytes holding 128 <see cref="ushort"/> entries, yet its shape
/// reads <c>256</c>).
/// </para>
/// </remarks>
public sealed class TableSet
{
    /// <summary>Cache file names for every table in the manifest.</summary>
    public static class Names
    {
#pragma warning disable CS1591 // Self-describing constants; the purpose column lives in the manifest.
        public const string AmpCurveHi = "curve_amp_hi_2ba0.bin";
        public const string AmpCurveLo = "curve_amp_lo_2da0.bin";
        public const string TvfEnvDepth = "curve_filtenvdepth_2f00.bin";
        public const string FilterTypeCoef = "curve_filttype_987b00.bin";
        public const string LevelCurve = "curve_level_2a00.bin";
        public const string PitchBias = "curve_pitchbias_2890.bin";
        public const string PitchDepthVs = "curve_pitchdepthvs_28d0.bin";
        public const string PitchEnv = "curve_pitchenv_2578.bin";
        public const string EnvRateOut = "curve_rateout_3060.bin";
        public const string ResoCurve = "curve_reso_2b88.bin";
        public const string EnvScaleCurve = "curve_scale_28e8.bin";
        public const string RateCurve = "curve_segrate_2900.bin";
        public const string VelCurve = "curve_vel_2b00.bin";
        public const string VelSens = "curve_velsens_01520.bin";
        public const string VelXfade = "curve_velxfade_01020.bin";
        public const string EnvShape = "env_shape_7a90.bin";
        public const string EnvStartPhase = "env_startphase_7a30.bin";
        public const string InterpCoef = "interp_coef_a0f210.bin";
        public const string KfPitch = "kf_pitch_01b20.bin";
        public const string KfPitchRate0 = "kf_pitchrate0_01f20.bin";
        public const string KfPitchRate1 = "kf_pitchrate1_01aa0.bin";
        public const string KfTvaLevel2 = "kf_tvalevel2_01fa0.bin";
        public const string KfTvaLevel = "kf_tvalevel_026a0.bin";
        public const string KfTvaRate0 = "kf_tvarate0_023a0.bin";
        public const string KfTvaRate1 = "kf_tvarate1_020a0.bin";
        public const string KfTvfEnv = "kf_tvfenv_02a20.bin";
        public const string KfTvfRate = "kf_tvfrate_03920.bin";
        public const string Layered = "layered_1896690.bin";
        public const string LfoCents = "lfo_cents_2690.bin";
        public const string LfoDelay = "lfo_delay_a2590.bin";
        public const string LfoRate = "lfo_rate_2790.bin";
        public const string LfoWave = "lfo_wave_1740.bin";
        public const string LfoWaveBank = "lfo_wavebank_a17f0.bin";
        public const string LfoWaveMap = "lfo_wavemap_87ae0.bin";
        public const string DirLut1 = "lut1_2e30.bin";
        public const string DirLut2 = "lut2_28b0.bin";
        public const string DirLut3 = "lut3_32b0.bin";
        public const string Multisample = "multisample_a.bin";
        public const string Pan = "pan_a2fa1.bin";
        public const string RampDivider = "ramp_divider_a03e00.bin";
        public const string RampExp = "ramp_exp_986420.bin";
        public const string RampFlagword = "ramp_flagword_a84d8.bin";
        public const string Tone = "tone_a.bin";
        public const string TvfCutoffCeil = "tvf_ceil_a7ed0.bin";
        public const string TvfQLp = "tvf_q_lp_a7cd0.bin";
        public const string TvfQT6 = "tvf_q_t6_a7fd0.bin";
        public const string TvfCutoffWarp = "tvf_warp_a83d0.bin";
        public const string Wavedesc = "wavedesc_a.bin";
#pragma warning restore CS1591
    }

    private readonly Dictionary<string, byte[]> _raw;

    private TableSet(Dictionary<string, byte[]> raw, TableManifest manifest)
    {
        _raw = raw;
        Manifest = manifest;

        AmpCurveHi = U16(raw[Names.AmpCurveHi]);
        AmpCurveLo = U16(raw[Names.AmpCurveLo]);
        TvfEnvDepth = raw[Names.TvfEnvDepth];
        FilterTypeCoef = U32(raw[Names.FilterTypeCoef]);
        LevelCurve = U16(raw[Names.LevelCurve]);
        PitchBias = raw[Names.PitchBias];
        PitchDepthVs = U16(raw[Names.PitchDepthVs]);
        PitchEnv = raw[Names.PitchEnv];
        EnvRateOut = U16(raw[Names.EnvRateOut]);
        ResoCurve = S16(raw[Names.ResoCurve]);
        EnvScaleCurve = raw[Names.EnvScaleCurve];
        RateCurve = U16(raw[Names.RateCurve]);
        VelCurve = raw[Names.VelCurve];
        VelSens = U16(raw[Names.VelSens]);
        VelXfade = raw[Names.VelXfade];
        EnvShape = U16(raw[Names.EnvShape]);
        EnvStartPhase = U16(raw[Names.EnvStartPhase]);
        InterpCoef = F32(raw[Names.InterpCoef]);
        KfPitch = S16(raw[Names.KfPitch]);
        KfPitchRate0 = raw[Names.KfPitchRate0];
        KfPitchRate1 = raw[Names.KfPitchRate1];
        KfTvaLevel = raw[Names.KfTvaLevel];
        KfTvaLevel2 = raw[Names.KfTvaLevel2];
        KfTvaRate0 = raw[Names.KfTvaRate0];
        KfTvaRate1 = raw[Names.KfTvaRate1];
        KfTvfEnv = S16(raw[Names.KfTvfEnv]);
        KfTvfRate = raw[Names.KfTvfRate];
        Layered = raw[Names.Layered];
        LfoCents = U16(raw[Names.LfoCents]);
        LfoDelay = U16(raw[Names.LfoDelay]);
        LfoRate = U16(raw[Names.LfoRate]);
        LfoWave = raw[Names.LfoWave];
        LfoWaveBank = S16(raw[Names.LfoWaveBank]);
        LfoWaveMap = raw[Names.LfoWaveMap];
        DirLut1 = raw[Names.DirLut1];
        DirLut2 = raw[Names.DirLut2];
        DirLut3 = U16(raw[Names.DirLut3]);
        Multisample = raw[Names.Multisample];
        Pan = raw[Names.Pan];
        RampDivider = raw[Names.RampDivider];
        RampExp = S32(raw[Names.RampExp]);
        RampFlagword = raw[Names.RampFlagword];
        Tone = raw[Names.Tone];
        TvfCutoffCeil = U16(raw[Names.TvfCutoffCeil]);
        TvfQLp = U16(raw[Names.TvfQLp]);
        TvfQT6 = U16(raw[Names.TvfQT6]);
        TvfCutoffWarp = U16(raw[Names.TvfCutoffWarp]);
        Wavedesc = raw[Names.Wavedesc];
    }

    /// <summary>The offset map these tables were loaded through.</summary>
    public TableManifest Manifest { get; }

    /// <summary>Loads every table by slicing the DLL at its manifest offset.</summary>
    /// <param name="rom">An open, verified ROM image.</param>
    /// <returns>The loaded table set.</returns>
    public static TableSet FromRom(RomImage rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        RequireLittleEndian();

        var raw = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in rom.Manifest.CachedTables)
        {
            raw[entry.Name] = rom.Read(entry);
        }

        return new TableSet(raw, rom.Manifest);
    }

    /// <summary>Loads every table from a directory of extracted <c>.bin</c> slices.</summary>
    /// <param name="directory">Directory holding the cache files.</param>
    /// <param name="manifest">Offset map to use; defaults to the embedded one.</param>
    /// <returns>The loaded table set.</returns>
    /// <exception cref="FileNotFoundException">A table's cache file is missing.</exception>
    /// <exception cref="InvalidDataException">A cache file has the wrong length.</exception>
    public static TableSet FromCacheDirectory(string directory, TableManifest? manifest = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        RequireLittleEndian();
        manifest ??= TableManifest.Default;

        var raw = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in manifest.CachedTables)
        {
            var path = System.IO.Path.Combine(directory, entry.Name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Table cache '{entry.Name}' not found in '{directory}'. " +
                    "Extract it from SCCore.dll first.", path);
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != entry.Size)
            {
                throw new InvalidDataException(
                    $"Table cache '{entry.Name}' is {bytes.Length} bytes; expected {entry.Size}.");
            }

            raw[entry.Name] = bytes;
        }

        return new TableSet(raw, manifest);
    }

    /// <summary>Returns the untouched bytes of one table.</summary>
    /// <param name="name">Cache file name; see <see cref="Names"/>.</param>
    /// <returns>The raw bytes.</returns>
    public ReadOnlySpan<byte> Raw(string name) => _raw[name];

#pragma warning disable CA1819 // Tables are fixed-size lookup data; exposing the arrays is the point.

    /// <summary><c>g_amp_curve_hi</c> — 256 entries, high half of the log-to-linear amplitude lookup.</summary>
    public ushort[] AmpCurveHi { get; }

    /// <summary><c>g_amp_curve_lo</c> — 256 entries, low half of the log-to-linear amplitude lookup.</summary>
    public ushort[] AmpCurveLo { get; }

    /// <summary><c>g_tvf_env_depth</c> — TVF env-depth conversion region, addressed by VA-relative offset.</summary>
    public byte[] TvfEnvDepth { get; }

    /// <summary><c>g_filter_type_coef</c> — 16 entries; filter TYPE byte to tap/coefficient word.</summary>
    public uint[] FilterTypeCoef { get; }

    /// <summary><c>g_level_curve</c> — 128 entries; envelope stage byte to 16-bit log attenuation.</summary>
    public ushort[] LevelCurve { get; }

    /// <summary><c>g_pitch_bias</c> — 128 entries; pitch-envelope bias sub-table.</summary>
    public byte[] PitchBias { get; }

    /// <summary><c>g_pitch_depth_vs</c> — 64 entries; pitch-envelope depth versus velocity.</summary>
    public ushort[] PitchDepthVs { get; }

    /// <summary><c>g_pitch_env</c> — pitch-envelope conversion region, addressed by VA-relative offset.</summary>
    public byte[] PitchEnv { get; }

    /// <summary><c>g_env_rate_out</c> — 512 entries; the rate-scale output curve, exactly 2^((i−0x80)/32) in 8.8.</summary>
    public ushort[] EnvRateOut { get; }

    /// <summary><c>g_reso_curve</c> — 64 entries; resonance curve for partial block byte 0x4a.</summary>
    public short[] ResoCurve { get; }

    /// <summary><c>g_env_scale_curve</c> — 256 entries; envelope rate/level scale, 0x40-neutral.</summary>
    public byte[] EnvScaleCurve { get; }

    /// <summary><c>g_rate_curve</c> — 128 entries; rate byte to segment time.</summary>
    public ushort[] RateCurve { get; }

    /// <summary><c>g_vel_curve</c> — 256 entries; velocity response curve.</summary>
    public byte[] VelCurve { get; }

    /// <summary><c>g_vel_sens</c> — 1024 entries; velocity sensitivity curve.</summary>
    public ushort[] VelSens { get; }

    /// <summary>
    /// <c>g_vel_xfade</c> — the multisample velocity-crossfade curve, 16 rows of 0x80 <em>bytes</em>.
    /// The manifest records this as <c>u16</c>, but the engine indexes it byte-wise as
    /// <c>[(selector &amp; 0xf) * 0x80 + position]</c>.
    /// </summary>
    public byte[] VelXfade { get; }

    /// <summary>
    /// <c>g_env_shape</c> — the fast-approach segment shape. Only the first 256 entries (0x200 bytes)
    /// are used; the cache over-reads slightly. Must be interpolated, not looked up bare.
    /// </summary>
    public ushort[] EnvShape { get; }

    /// <summary><c>g_env_startphase</c> — 128 entries; per-segment initial phase seed.</summary>
    public ushort[] EnvStartPhase { get; }

    /// <summary>
    /// <c>g_interp_coef_table</c> — the 4-tap FIR resample kernel as 128 phases of 4 taps, flattened
    /// to 512 floats. Index a phase as <c>phase * 4 + tap</c>. Every phase sums to 1.0.
    /// </summary>
    public float[] InterpCoef { get; }

    /// <summary><c>g_kf_pitch</c> — 8 rows of 128; pitch key-follow, indexed <c>row * 0x80 + key</c>.</summary>
    public short[] KfPitch { get; }

    /// <summary><c>g_kf_pitchrate0</c> — 128×128; pitch-envelope rate key-follow for the segments.</summary>
    public byte[] KfPitchRate0 { get; }

    /// <summary><c>g_kf_pitchrate1</c> — 128×128; pitch-envelope rate key-follow for the release.</summary>
    public byte[] KfPitchRate1 { get; }

    /// <summary><c>g_kf_tvalevel</c> — 128×128; TVA base-level key-follow.</summary>
    public byte[] KfTvaLevel { get; }

    /// <summary><c>g_kf_tvalevel2</c> — 128×128; TVA level key-follow, second term.</summary>
    public byte[] KfTvaLevel2 { get; }

    /// <summary><c>g_kf_tvarate0</c> — 128×128; TVA envelope rate key-follow for the segments.</summary>
    public byte[] KfTvaRate0 { get; }

    /// <summary><c>g_kf_tvarate1</c> — 128×128; TVA envelope rate key-follow for the release.</summary>
    public byte[] KfTvaRate1 { get; }

    /// <summary>
    /// <c>g_kf_tvfenv</c> — TVF env-depth key-follow. Only rows 0–15 are used; the cache is an
    /// over-read whose used prefix matches the DLL.
    /// </summary>
    public short[] KfTvfEnv { get; }

    /// <summary>
    /// <c>g_kf_tvfrate</c> — 128×128; TVF envelope rate key-follow. Row 0 doubles as
    /// <c>g_kf_tvfrate2</c>, the release table, which covers 98% of partials.
    /// </summary>
    public byte[] KfTvfRate { get; }

    /// <summary><c>g_layered_table</c> — 50 alternate-articulation records of 0x18 bytes.</summary>
    public byte[] Layered { get; }

    /// <summary><c>g_lfo_cents_tbl</c> — 128 entries; LFO pitch-depth byte to milli-semitones.</summary>
    public ushort[] LfoCents { get; }

    /// <summary><c>g_lfo_delay_tbl</c> — 128 entries; LFO1 delay byte to delay ticks.</summary>
    public ushort[] LfoDelay { get; }

    /// <summary><c>g_lfo_rate_tbl</c> — 128 entries; LFO1 rate byte to 16-bit phase increment.</summary>
    public ushort[] LfoRate { get; }

    /// <summary><c>g_lfo_wave</c> — the half-sine LFO waveform used by selector 0.</summary>
    public byte[] LfoWave { get; }

    /// <summary><c>g_lfo_wavebank</c> — LFO wavetables 8..0x1f, 24 rows of 0x81 entries.</summary>
    public short[] LfoWaveBank { get; }

    /// <summary><c>g_lfo_wavemap</c> — 32 entries; waveform selector nibble to table index.</summary>
    public byte[] LfoWaveMap { get; }

    /// <summary><c>g_dir_lut1</c> — 128 entries; tone-map to LUT2 row.</summary>
    public byte[] DirLut1 { get; }

    /// <summary><c>g_dir_lut2</c> — bank to LUT3 row.</summary>
    public byte[] DirLut2 { get; }

    /// <summary><c>g_dir_lut3</c> — 32768 entries; program to tone number.</summary>
    public ushort[] DirLut3 { get; }

    /// <summary><c>g_multisample_table</c> — key/velocity zone to wave number, stride 0x8c.</summary>
    public byte[] Multisample { get; }

    /// <summary>
    /// <c>g_pan_tbl</c> — 128 entries. Left gain is <c>T[127 − p] / 127</c>, right is
    /// <c>T[p − 1] / 127</c>; centre is 75/127, neither constant-power nor linear.
    /// </summary>
    public byte[] Pan { get; }

    /// <summary><c>g_ramp_divider</c> — the anti-zipper zero-order-hold masks <c>[0, 7, 31, 127]</c>.</summary>
    public byte[] RampDivider { get; }

    /// <summary><c>g_ramp_exp_tbl</c> — 257 entries of 2^17 · 2^(i/256), the SVF f-coefficient decode.</summary>
    public int[] RampExp { get; }

    /// <summary><c>g_ramp_flagword</c> — per-destination ramp-divider selector bits.</summary>
    public byte[] RampFlagword { get; }

    /// <summary><c>g_tone_table</c> — tone records of stride 0x100: 0x24 header plus partial blocks of 0x6e.</summary>
    public byte[] Tone { get; }

    /// <summary><c>g_tvf_cutoff_ceil</c> — 128 entries; cutoff ceiling by resonance byte.</summary>
    public ushort[] TvfCutoffCeil { get; }

    /// <summary><c>g_tvf_q_lp</c> — 256 entries; Q trim for filter type 0 at neutral resonance.</summary>
    public ushort[] TvfQLp { get; }

    /// <summary><c>g_tvf_q_t6</c> — 256 entries; Q trim for filter type 6.</summary>
    public ushort[] TvfQT6 { get; }

    /// <summary><c>g_tvf_cutoff_warp</c> — 129 entries; the cutoff warp applied at stage C.</summary>
    public ushort[] TvfCutoffWarp { get; }

    /// <summary><c>g_wavedesc_table</c> — wave descriptors of stride 0x16: ROM coordinates, root key, loop.</summary>
    public byte[] Wavedesc { get; }

#pragma warning restore CA1819

    private static void RequireLittleEndian()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "SCCore.dll table data is little-endian; this library requires a little-endian host.");
        }
    }

    private static ushort[] U16(byte[] bytes)
    {
        var result = new ushort[bytes.Length / sizeof(ushort)];
        MemoryMarshal.Cast<byte, ushort>(bytes)[..result.Length].CopyTo(result);
        return result;
    }

    private static short[] S16(byte[] bytes)
    {
        var result = new short[bytes.Length / sizeof(short)];
        MemoryMarshal.Cast<byte, short>(bytes)[..result.Length].CopyTo(result);
        return result;
    }

    private static uint[] U32(byte[] bytes)
    {
        var result = new uint[bytes.Length / sizeof(uint)];
        MemoryMarshal.Cast<byte, uint>(bytes)[..result.Length].CopyTo(result);
        return result;
    }

    private static int[] S32(byte[] bytes)
    {
        var result = new int[bytes.Length / sizeof(int)];
        MemoryMarshal.Cast<byte, int>(bytes)[..result.Length].CopyTo(result);
        return result;
    }

    private static float[] F32(byte[] bytes)
    {
        var result = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes)[..result.Length].CopyTo(result);
        return result;
    }
}
