using System.Text.Json;
using TabulaSonora.Rom;

namespace TabulaSonora.Tools;

/// <summary>
/// One-step setup: everything the engine needs, derived from a single <c>SCCore.dll</c>.
/// </summary>
/// <remarks>
/// The project depends on no other repository. Given the DLL, this verifies it, extracts the static
/// tables, reads the delay presets, and harvests the reverb and chorus coefficients.
/// </remarks>
public static class Preparer
{
    /// <summary>Runs the full preparation.</summary>
    /// <param name="dllPath">Path to the pinned <c>SCCore.dll</c>.</param>
    /// <param name="presetsPath">Where to write the effect presets.</param>
    /// <param name="tablesDirectory">Optional directory to extract the static tables into.</param>
    /// <param name="log">Receives progress lines.</param>
    /// <returns>Zero on success, non-zero when something the user must act on is missing.</returns>
    public static int Prepare(string dllPath, string presetsPath, string? tablesDirectory, Action<string> log)
    {
        ArgumentException.ThrowIfNullOrEmpty(dllPath);
        ArgumentException.ThrowIfNullOrEmpty(presetsPath);
        ArgumentNullException.ThrowIfNull(log);

        // 1. Identity. Everything downstream is only valid for this exact build.
        log($"Verifying {Path.GetFileName(dllPath)} ...");
        using (var rom = RomImage.Open(dllPath, RomVerification.Full))
        {
            log($"  {rom.Length:N0} bytes, PE timestamp {rom.ReadPeTimestamp()} - matches the pinned build.");

            // 2. Static tables. The library reads these straight from the DLL at run time, so this
            //    step exists only for the conformance tests and for inspection.
            if (!string.IsNullOrEmpty(tablesDirectory))
            {
                Directory.CreateDirectory(tablesDirectory);
                long bytes = 0;
                foreach (var entry in rom.Manifest.CachedTables)
                {
                    var data = rom.Read(entry);
                    File.WriteAllBytes(Path.Combine(tablesDirectory, entry.Name), data);
                    bytes += data.Length;
                }

                log($"  extracted {rom.Manifest.CachedTables.Count} tables ({bytes:N0} bytes) to {tablesDirectory}");
            }

            // 3. Delay presets read straight out of the DLL's own preset table.
            var delayPresets = PresetBaker.ReadDelayPresets(rom);
            log($"  read {delayPresets.Length} delay presets from the DLL");

            // 4. Reverb and chorus. These are computed by the engine at initialisation and appear
            //    nowhere in the file, so they have to come from a running instance.
            if (!EffectHarvester.IsSupported)
            {
                log(string.Empty);
                log("Reverb and chorus coefficients could not be harvested on this platform.");
                log("  They are computed by the engine at start-up rather than stored in the DLL, so");
                log("  obtaining them means executing SCCore.dll, which is a 64-bit Windows binary.");
                log("  Run 'prepare' once on Windows x64 and copy the resulting presets.json here:");
                log("  it is identical for everyone using this DLL build and carries no machine state.");
                return 3;
            }

            log("Harvesting reverb and chorus coefficients from a live engine ...");
            var (reverbDefault, reverb, chorusDefault, chorus) = EffectHarvester.Harvest(dllPath);
            log($"  {reverb.Length} reverb and {chorus.Length} chorus types");

            PresetBaker.Write(presetsPath, reverbDefault, reverb, chorusDefault, chorus, delayPresets);
        }

        log(string.Empty);
        log($"Wrote {presetsPath} ({new FileInfo(presetsPath).Length:N0} bytes).");
        log("Ready. The engine needs nothing else.");
        return 0;
    }

    /// <summary>Reports what a preparation would need, without doing it.</summary>
    /// <param name="log">Receives the report.</param>
    public static void Explain(Action<string> log)
    {
        log("prepare derives everything from one SCCore.dll:");
        log("  - verifies size, PE timestamp and SHA-256 against the pinned build");
        log("  - extracts the 48 static tables (optional; the library reads them from the DLL directly)");
        log("  - reads the 10 GS delay presets from the DLL's own preset table");
        log("  - harvests the 8 reverb and 8 chorus coefficient sets from a live engine");
        log(string.Empty);
        log($"  harvesting supported on this platform: {(EffectHarvester.IsSupported ? "yes" : "no")}");
    }
}
