using System.Text;

namespace TabulaSonora.Patches;

/// <summary>
/// One melodic tone record: an ASCII name, a master level, and up to two partial parameter blocks.
/// </summary>
public sealed class Tone
{
    /// <summary>Bytes per tone record.</summary>
    public const int Stride = 0x100;

    /// <summary>Bytes of header before the first partial block.</summary>
    public const int HeaderSize = 0x24;

    /// <summary>Partial slots in a melodic tone record. The drum tone table has four.</summary>
    public const int PartialSlots = 2;

    /// <summary>Length of the ASCII name field.</summary>
    public const int NameLength = 12;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    internal Tone(int number, string name, int level, IReadOnlyList<PartialParameters> partials)
    {
        Number = number;
        Name = name;
        Level = level;
        Partials = partials;
    }

    /// <summary>The tone number this record was read from.</summary>
    public int Number { get; }

    /// <summary>The tone's ASCII name, trailing padding removed.</summary>
    public string Name { get; }

    /// <summary>
    /// Per-tone master level, header +0x0c. The engine copies it to <c>voice+0x167</c>, where it
    /// becomes the third attenuation in the TVA base-level chain.
    /// </summary>
    public int Level { get; }

    /// <summary>
    /// The partials that are actually present, in slot order.
    /// </summary>
    /// <remarks>
    /// Empty slots are dropped rather than represented, so an index into this list is not necessarily
    /// the slot index — that matters when correlating against a voice dump, where ordering is not
    /// guaranteed to line up either.
    /// </remarks>
    public IReadOnlyList<PartialParameters> Partials { get; }

    /// <summary>
    /// Whether this record holds a real tone.
    /// </summary>
    /// <remarks>
    /// Unused records read back as short or empty names, so a name of fewer than two characters is
    /// treated as absent — the same rule the reference resolver applies.
    /// </remarks>
    public bool IsDefined => Name.Length >= 2;

    internal static Tone Read(byte[] toneTable, int number)
    {
        var offset = number * Stride;
        var name = Latin1.GetString(toneTable, offset, NameLength).TrimEnd(' ', '\0');
        var level = toneTable[offset + 0x0C];

        var partials = new List<PartialParameters>(PartialSlots);
        for (var slot = 0; slot < PartialSlots; slot++)
        {
            var partial = new PartialParameters(toneTable, offset + HeaderSize + (slot * PartialParameters.Stride));
            if (partial.IsPresent)
            {
                partials.Add(partial);
            }
        }

        return new Tone(number, name, level, partials);
    }

    /// <inheritdoc/>
    public override string ToString() => $"tone#{Number} '{Name}' ({Partials.Count} partial(s))";
}

/// <summary>The waves that sound for one tone at a given note and velocity.</summary>
/// <param name="Name">The tone's name, or a placeholder when it is unassigned.</param>
/// <param name="Partials">The sounding partials.</param>
public readonly record struct ResolvedTone(string Name, IReadOnlyList<ResolvedPartial> Partials);
