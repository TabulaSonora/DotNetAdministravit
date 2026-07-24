using Microsoft.JSInterop;
using TabulaSonora.Patches;

namespace TabulaSonora.Web.Services;

/// <summary>
/// Remembers the vintage and the three effect toggles across visits, in <c>localStorage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two habits are taken from the theme script in <c>index.html</c> deliberately. Storage access
/// <em>throws</em> where the browser has disabled it, and a preference is not worth failing the page
/// over, so both directions swallow that. And the default is the <em>absence</em> of an entry rather
/// than an entry saying "default", so a user who never touches the Engine panel leaves nothing
/// behind and a change to the defaults reaches them.
/// </para>
/// <para>
/// Synchronous, over <see cref="IJSInProcessRuntime"/>: this is standalone WebAssembly, where interop
/// is a direct call and <c>localStorage</c> is itself synchronous in the browser.
/// <see cref="SynthSession.SettingsChanged"/> is a plain event, so an asynchronous store would buy
/// nothing here and cost a fire-and-forget write with unobserved exceptions and no ordering. There is
/// no JavaScript module either, unlike <see cref="AssetStore"/>: two <c>localStorage</c> calls do not
/// earn a file.
/// </para>
/// </remarks>
/// <param name="js">The browser's runtime, which in WebAssembly is always in-process.</param>
public sealed class EnginePreferences(IJSRuntime js)
{
    /// <summary>Storage key, in the same namespace as the theme's <c>tabula-sonora.theme</c>.</summary>
    private const string Key = "tabula-sonora.engine";

    private readonly IJSInProcessRuntime _js = (IJSInProcessRuntime)js;

    /// <summary>Reads the remembered settings.</summary>
    /// <returns>
    /// What was stored, or <see cref="EngineSettings.Default"/> if nothing was, if what was cannot be
    /// read as this format, or if storage is unavailable.
    /// </returns>
    public EngineSettings Read()
    {
        string? stored;

        try
        {
            stored = _js.Invoke<string?>("localStorage.getItem", Key);
        }
        catch (JSException)
        {
            return EngineSettings.Default;
        }

        return Parse(stored) ?? EngineSettings.Default;
    }

    /// <summary>Writes the settings, or removes the entry when they are the defaults.</summary>
    /// <param name="settings">What to remember.</param>
    public void Write(EngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            if (settings == EngineSettings.Default)
            {
                _js.InvokeVoid("localStorage.removeItem", Key);
                return;
            }

            _js.InvokeVoid("localStorage.setItem", Key, Format(settings));
        }
        catch (JSException)
        {
            // The choice still holds for this page view; it just will not be remembered.
        }
    }

    /// <summary>Formats settings as <c>map,reverb,chorus,delay</c> — for instance <c>3,1,1,0</c>.</summary>
    /// <param name="settings">What to format.</param>
    /// <returns>The stored form.</returns>
    /// <remarks>
    /// Not JSON. Serialising the application's own types is a trimming and AOT liability, and this is
    /// four values with a fixed shape; <see cref="Parse"/> is the whole of the cost of hand-writing it.
    /// </remarks>
    private static string Format(EngineSettings settings) =>
        $"{(int)settings.Map},{Bit(settings.Reverb)},{Bit(settings.Chorus)},{Bit(settings.Delay)}";

    private static char Bit(bool on) => on ? '1' : '0';

    /// <summary>Reads back what <see cref="Format"/> wrote.</summary>
    /// <param name="stored">The stored form, or <see langword="null"/>.</param>
    /// <returns>The settings, or <see langword="null"/> if this is not that format.</returns>
    /// <remarks>
    /// Strict, and whole: a value that does not parse in every field is discarded entirely rather than
    /// honoured in part. Half of a stale format is worse than the defaults, because it is a setting
    /// the user never chose.
    /// </remarks>
    private static EngineSettings? Parse(string? stored)
    {
        if (stored is null)
        {
            return null;
        }

        var fields = stored.Split(',');
        if (fields.Length != 4)
        {
            return null;
        }

        if (!int.TryParse(fields[0], out var map) || !Enum.IsDefined((ToneMap)map))
        {
            return null;
        }

        if (Flag(fields[1]) is not { } reverb ||
            Flag(fields[2]) is not { } chorus ||
            Flag(fields[3]) is not { } delay)
        {
            return null;
        }

        return new EngineSettings((ToneMap)map, reverb, chorus, delay);
    }

    private static bool? Flag(string field) => field switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };
}
