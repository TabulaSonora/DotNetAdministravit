using System.Runtime.InteropServices;

namespace TabulaSonora.Tools;

/// <summary>One harvested reverb coefficient set, in the shape the preset file stores.</summary>
/// <param name="Diffusers">The four series allpass diffusers.</param>
/// <param name="TankA">First parallel tank.</param>
/// <param name="TankB">Second parallel tank.</param>
/// <param name="TankAllpasses">The four allpasses nested in the tanks.</param>
/// <param name="InjectionTap">Ring offset the diffuser chain is injected from.</param>
/// <param name="DampFeedback">Damping filter feedback coefficient.</param>
/// <param name="DampInput">Damping filter input coefficient.</param>
/// <param name="GainInput">Input gain.</param>
/// <param name="GainInjection">Injection gain.</param>
/// <param name="GainFeedback">Tank feedback gain.</param>
/// <param name="GainOutput">Output gain.</param>
public sealed record HarvestedReverb(
    object[] Diffusers,
    object TankA,
    object TankB,
    object TankAllpasses,
    int InjectionTap,
    double DampFeedback,
    double DampInput,
    double GainInput,
    double GainInjection,
    double GainFeedback,
    double GainOutput);

/// <summary>
/// Reads the reverb and chorus coefficients out of a running Sound Canvas engine.
/// </summary>
/// <remarks>
/// <para>
/// These coefficients are <em>computed at initialisation</em> from the GS macro parameters rather
/// than stored anywhere in the DLL — searching the file for the tap positions, and for the delay
/// lengths they derive from, finds neither. The only way to obtain them without re-deriving the
/// engine's own coefficient maths is to let the engine compute them and then read its state.
/// </para>
/// <para>
/// This is the <b>only</b> part of the project that loads <c>SCCore.dll</c> as code, and it is
/// confined to this setup tool: the library never does. Because the DLL is x64 Windows, harvesting
/// only runs there. The result does not depend on the machine, so a <c>presets.json</c> produced on
/// any Windows host is valid everywhere — see <see cref="IsSupported"/>.
/// </para>
/// </remarks>
public static unsafe class EffectHarvester
{
    private const long ImageBase = 0x180000000L;

    // Reverb state, reached through pointers the engine fills in during initialisation.
    private const long ReverbAllpass0 = 0x181A62AB0;
    private const long ReverbTankA = 0x181A62AD0;
    private const long ReverbTankB = 0x181A62AD8;
    private const long ReverbInjectionTap = 0x181A62AE0;
    private const long ReverbDampFeedback = 0x181A62AA8;
    private const long ReverbDampInput = 0x181A62AAC;
    private const long ReverbGainInput = 0x181A6ED70;
    private const long ReverbGainInjection = 0x181A6EE70;
    private const long ReverbGainFeedback = 0x181A6EEF0;
    private const long ReverbGainOutput = 0x181A6EDF0;

    // Chorus left-stage state. The right stage is gated off for every GS type.
    private const long ChorusLpfA = 0x181A62AF0;
    private const long ChorusLpfB = 0x181A62AF4;
    private const long ChorusLfoIncrement = 0x181A62AFC;
    private const long ChorusTap1Depth = 0x181A62B00;
    private const long ChorusTap2Depth = 0x181A62B02;
    private const long ChorusTap1Base = 0x181A62B04;
    private const long ChorusTap2Base = 0x181A62B08;
    private const long ChorusFeedback = 0x181A62B10;
    private const long ChorusGainWrite = 0x181A6EF70;
    private const long ChorusGainTap = 0x181A6F0F0;

    private static readonly string[] TankTapNames =
        ["tap10", "tap14", "tap18", "tap1C", "tap20", "tap24", "tap28", "tap2C"];

    /// <summary>Whether this platform can run the harvest.</summary>
    /// <remarks>
    /// <c>SCCore.dll</c> is a 64-bit Windows binary, so it can only be executed on Windows x64.
    /// Everything else <c>prepare</c> does works anywhere.
    /// </remarks>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Harvests every reverb and chorus preset.</summary>
    /// <param name="dllPath">Path to the pinned <c>SCCore.dll</c>.</param>
    /// <param name="reverbTypes">Number of GS reverb macros.</param>
    /// <param name="chorusTypes">Number of GS chorus macros.</param>
    /// <returns>The default plus per-type coefficient sets for each effect.</returns>
    /// <exception cref="PlatformNotSupportedException">This platform cannot execute the DLL.</exception>
    public static (object ReverbDefault, object[] Reverb, object ChorusDefault, object[] Chorus) Harvest(
        string dllPath, int reverbTypes = 8, int chorusTypes = 8)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Harvesting the reverb and chorus coefficients means executing SCCore.dll, which is a " +
                "64-bit Windows binary. Run 'prepare' once on Windows x64 and copy the resulting " +
                "presets.json; it does not depend on the machine that produced it.");
        }

        var handle = NativeLibrary.Load(dllPath);
        try
        {
            var engine = new Engine(handle);

            var reverb = new object[reverbTypes];
            for (var t = 0; t < reverbTypes; t++)
            {
                reverb[t] = HarvestReverb(engine, t);
            }

            var chorus = new object[chorusTypes];
            for (var t = 0; t < chorusTypes; t++)
            {
                chorus[t] = HarvestChorus(engine, t);
            }

            // The GM power-on defaults: a plain GS reset with no macro selected.
            var reverbDefault = HarvestReverb(engine, null);
            var chorusDefault = HarvestChorus(engine, null);

            return (reverbDefault, reverb, chorusDefault, chorus);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static object HarvestReverb(Engine engine, int? type)
    {
        engine.Reset();
        if (type is { } t)
        {
            engine.SendGsParameter(0x40, 0x01, 0x30, (byte)t);   // REVERB MACRO
        }

        engine.SetController(0, 0);
        engine.SetController(32, 0);
        engine.SetController(7, 127);
        engine.SetController(10, 64);
        engine.SetController(91, 127);
        engine.SetController(93, 0);
        engine.ProgramChange(12);                                 // marimba
        engine.Run(8);
        engine.NoteOn(60, 100);
        engine.Run(16);                                           // let the reverb settle

        var diffusers = new object[4];
        for (var i = 0; i < 4; i++)
        {
            diffusers[i] = ReadAllpass(engine.Pointer(ReverbAllpass0 + (i * 8)));
        }

        var tankA = engine.Pointer(ReverbTankA);
        var tankB = engine.Pointer(ReverbTankB);

        return new
        {
            diffusers,
            tankA = ReadTank(tankA),
            tankB = ReadTank(tankB),
            tankAllpasses = new
            {
                A0 = ReadAllpass(*(long*)tankA),
                A1 = ReadAllpass(*(long*)(tankA + 8)),
                B0 = ReadAllpass(*(long*)tankB),
                B1 = ReadAllpass(*(long*)(tankB + 8)),
            },
            injectionTap = engine.Int32(ReverbInjectionTap),
            dampFeedback = engine.Single(ReverbDampFeedback),
            dampInput = engine.Single(ReverbDampInput),
            gainInput = engine.Single(ReverbGainInput),
            gainInjection = engine.Single(ReverbGainInjection),
            gainFeedback = engine.Single(ReverbGainFeedback),
            gainOutput = engine.Single(ReverbGainOutput),
        };
    }

    private static object HarvestChorus(Engine engine, int? type)
    {
        engine.Reset();
        if (type is { } t)
        {
            engine.SendGsParameter(0x40, 0x01, 0x38, (byte)t);   // CHORUS MACRO
        }

        engine.SetController(0, 0);
        engine.SetController(32, 0);
        engine.SetController(7, 127);
        engine.SetController(10, 64);
        engine.SetController(91, 0);
        engine.SetController(93, 127);
        engine.ProgramChange(48);                                 // strings
        engine.Run(8);
        engine.NoteOn(60, 100);
        engine.Run(8);

        return new
        {
            lfoIncrement = engine.Int32(ChorusLfoIncrement),
            lpfA = engine.Single(ChorusLpfA),
            lpfB = engine.Single(ChorusLpfB),
            tap1Depth = (int)engine.Int16(ChorusTap1Depth),
            tap1Base = engine.Int32(ChorusTap1Base),
            tap2Depth = (int)engine.Int16(ChorusTap2Depth),
            tap2Base = engine.Int32(ChorusTap2Base),
            feedback = engine.Single(ChorusFeedback),
            gainWrite = engine.Single(ChorusGainWrite),
            gainTap = engine.Single(ChorusGainTap),
        };
    }

    private static object ReadAllpass(long p) => new
    {
        writeTap = *(int*)p,
        readTap = *(int*)(p + 4),
        coefA = (double)*(float*)(p + 8),
        coefB = (double)*(float*)(p + 0xC),
    };

    private static object ReadTank(long p)
    {
        var taps = new Dictionary<string, int>(TankTapNames.Length, StringComparer.Ordinal);
        for (var i = 0; i < TankTapNames.Length; i++)
        {
            taps[TankTapNames[i]] = *(int*)(p + 0x10 + (i * 4));
        }

        return new { taps, coefA = (double)*(float*)(p + 0x30), coefB = (double)*(float*)(p + 0x34) };
    }

    /// <summary>A loaded tone generator, driven through its exported ABI.</summary>
    private sealed class Engine
    {
        private readonly long _base;
        private readonly delegate* unmanaged[Cdecl]<uint, uint, void> _shortIn;
        private readonly delegate* unmanaged[Cdecl]<byte*, uint, void> _longIn;
        private readonly delegate* unmanaged[Cdecl]<void> _flush;
        private readonly delegate* unmanaged[Cdecl]<float*, float*, uint, void> _process;
        private readonly float[] _left = new float[512];
        private readonly float[] _right = new float[512];

        public Engine(nint handle)
        {
            _base = handle;

            var initialize = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(handle, "TG_initialize");
            var setSampleRate = (delegate* unmanaged[Cdecl]<float, void>)NativeLibrary.GetExport(handle, "TG_setSampleRate");
            var setBlockSize = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(handle, "TG_setMaxBlockSize");
            var activate = (delegate* unmanaged[Cdecl]<float, int, void>)NativeLibrary.GetExport(handle, "TG_activate");
            var setThread = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(handle, "TG_setInterruptThreadIdAtThisTime");

            _shortIn = (delegate* unmanaged[Cdecl]<uint, uint, void>)NativeLibrary.GetExport(handle, "TG_ShortMidiIn");
            _longIn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)NativeLibrary.GetExport(handle, "TG_LongMidiIn");
            _flush = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(handle, "TG_flushMidi");
            _process = (delegate* unmanaged[Cdecl]<float*, float*, uint, void>)NativeLibrary.GetExport(handle, "TG_Process");

            initialize(0);

            // The engine's own rate: harvesting at the host rate would give ring offsets scaled for
            // that rate instead.
            setSampleRate(32000f);
            setBlockSize(512);
            activate(32000f, 512);

            // Must be called on the thread that later calls Process.
            setThread();
        }

        /// <summary>Translates a virtual address into this module's mapping.</summary>
        public long Address(long va) => _base + (va - ImageBase);

        /// <summary>Dereferences a pointer stored at a virtual address.</summary>
        public long Pointer(long va) => *(long*)Address(va);

        public int Int32(long va) => *(int*)Address(va);

        public short Int16(long va) => *(short*)Address(va);

        public double Single(long va) => *(float*)Address(va);

        public void Reset()
        {
            SendGsParameter(0x40, 0x00, 0x7F, 0x00);   // GS reset
            Run(2);
        }

        public void SetController(int controller, int value) =>
            _shortIn((uint)(0xB0 | (controller << 8) | (value << 16)), 0);

        public void ProgramChange(int program) => _shortIn((uint)(0xC0 | (program << 8)), 0);

        public void NoteOn(int note, int velocity)
        {
            _shortIn((uint)(0x90 | (note << 8) | (velocity << 16)), 0);
            _flush();
        }

        /// <summary>Sends a Roland GS DT1 frame.</summary>
        public void SendGsParameter(byte a1, byte a2, byte a3, byte value)
        {
            var checksum = (byte)((128 - ((a1 + a2 + a3 + value) & 0x7F)) & 0x7F);
            byte[] message = [0xF0, 0x41, 0x10, 0x42, 0x12, a1, a2, a3, value, checksum, 0xF7];

            fixed (byte* p = message)
            {
                _longIn(p, 0);
            }

            _flush();
        }

        /// <summary>Renders blocks so the engine settles.</summary>
        public void Run(int blocks)
        {
            _flush();
            fixed (float* l = _left, r = _right)
            {
                for (var i = 0; i < blocks; i++)
                {
                    _process(l, r, 512);
                }
            }
        }
    }
}
