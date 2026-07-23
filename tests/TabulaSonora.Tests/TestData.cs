namespace TabulaSonora.Tests;

/// <summary>
/// Locates the Roland-derived assets the conformance tests need.
/// </summary>
/// <remarks>
/// None of this data ships with the repo — <c>SCCore.dll</c> and everything derived from it is
/// copyrighted. Tests that need it skip when it is absent, so a clone without it still builds and
/// runs the pure-logic tests.
/// </remarks>
internal static class TestData
{
    /// <summary>
    /// Repository root, found by walking up from the test binaries.
    /// </summary>
    /// <remarks>
    /// Sibling checkouts are looked up relative to this rather than by absolute path, so the tests
    /// work on any machine and the repository carries no one developer's directory layout.
    /// </remarks>
    public static string RepositoryRoot { get; } = FindRoot();

    private static readonly string[] DllCandidates =
    [
        @"C:\Program Files\Roland VS\SOUND Canvas VA\SCCore.dll",
        Path.Combine(RepositoryRoot, "SCCore.dll"),
        Path.Combine(RepositoryRoot, "..", "SauceForYourEars", "SCCore.dll"),
    ];

    private static readonly string[] TableCandidates =
    [
        Path.Combine(RepositoryRoot, "tables"),
        Path.Combine(RepositoryRoot, "..", "TabulaSonora", "tables"),
        Path.Combine(RepositoryRoot, "..", "DeconstructingTheSauce", "tables"),
    ];

    private static readonly string[] TraceCandidates =
    [
        Path.Combine(RepositoryRoot, "..", "DeconstructingTheSauce"),
    ];

    /// <summary>Path to the pinned <c>SCCore.dll</c>, or <see langword="null"/> if not present.</summary>
    public static string? SccorePath { get; } = Resolve("TABULASONORA_SCCORE", DllCandidates, File.Exists);

    /// <summary>Directory of extracted <c>tables/*.bin</c>, or <see langword="null"/>.</summary>
    public static string? TablesDirectory { get; } =
        Resolve("TABULASONORA_TABLES", TableCandidates, Directory.Exists);

    /// <summary>Directory holding the golden traces and A/B captures, or <see langword="null"/>.</summary>
    public static string? TraceDirectory { get; } =
        Resolve("TABULASONORA_TRACES", TraceCandidates, Directory.Exists);

    /// <summary>Skips the calling test unless the pinned DLL is available.</summary>
    /// <returns>The DLL path.</returns>
    public static string RequireSccore()
    {
        Skip.If(SccorePath is null,
            "SCCore.dll not found. Set TABULASONORA_SCCORE to the pinned build to run this test.");
        return SccorePath!;
    }

    /// <summary>Skips the calling test unless the extracted table cache is available.</summary>
    /// <returns>The tables directory.</returns>
    public static string RequireTables()
    {
        Skip.If(TablesDirectory is null,
            "tables/*.bin not found. Set TABULASONORA_TABLES to run this test.");
        return TablesDirectory!;
    }

    /// <summary>Skips the calling test unless a named golden-trace file is available.</summary>
    /// <param name="fileName">File name within the trace directory.</param>
    /// <returns>The full path to the trace file.</returns>
    public static string RequireTrace(string fileName)
    {
        Skip.If(TraceDirectory is null, "Golden-trace directory not found. Set TABULASONORA_TRACES.");
        var path = Path.Combine(TraceDirectory!, fileName);
        Skip.IfNot(File.Exists(path), $"Golden trace '{fileName}' not found in '{TraceDirectory}'.");
        return path;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotNetAdministravitTabulaSonora.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string? Resolve(string environmentVariable, string[] candidates, Func<string, bool> exists)
    {
        var overridden = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && exists(overridden))
        {
            return overridden;
        }

        return candidates.FirstOrDefault(exists);
    }
}
