using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TabulaSonora.Effects;
using TabulaSonora.Web;
using TabulaSonora.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// No HttpClient is registered, and that is worth stating rather than leaving to be noticed. Nothing
// here talks to a server: the engine, its tables and its effects are all in the browser, the user's
// DLL never leaves the machine, and the published output is static files that any file server can
// host. There is no back end to fail, and nothing to send anything to.

// One engine, one device, one pump, for the lifetime of the page.
builder.Services.AddSingleton<AssetStore>();
builder.Services.AddSingleton<EnginePreferences>();
builder.Services.AddSingleton<AudioOutput>();
builder.Services.AddSingleton<SynthSession>();
builder.Services.AddSingleton<MidiInput>();
builder.Services.AddSingleton<PlaybackPump>();

var host = builder.Build();

// The effect coefficients have to be in place before the first render, and they are the one thing
// here that cannot be derived from the user's own DLL in a browser -- see NOTICE.md. A copy the user
// harvested themselves takes precedence over the one this application ships.
await LoadPresetsAsync(host.Services);

// The vintage the user last chose has to be in place before the first render, for a related reason:
// the DLL cached from a previous visit is loaded without asking, so an engine built at the default
// and reconfigured a frame later would be a second generator and a page that visibly settles into
// the right vintage instead of opening in it.
RestoreEngineSettings(host.Services);

await host.RunAsync();

static void RestoreEngineSettings(IServiceProvider services)
{
    var preferences = services.GetRequiredService<EnginePreferences>();
    var session = services.GetRequiredService<SynthSession>();

    session.Settings = preferences.Read();

    // Subscribed after the restore rather than before it, so hydrating does not write itself back.
    session.SettingsChanged += () => preferences.Write(session.Settings);
}

static async Task LoadPresetsAsync(IServiceProvider services)
{
    var store = services.GetRequiredService<AssetStore>();

    try
    {
        if (await store.ReadTextAsync(AssetStore.PresetsKey) is { Length: > 0 } supplied)
        {
            using var theirs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(supplied));
            EffectPresets.Use(EffectPresets.Load(theirs));
            return;
        }
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
    {
        // A corrupt upload must not take the page down with it; fall through to the shipped copy.
        Console.Error.WriteLine($"Stored presets could not be read ({ex.Message}); using the shipped set.");
    }

    await using var shipped = typeof(Program).Assembly
        .GetManifestResourceStream("TabulaSonora.Web.presets.json")
        ?? throw new InvalidOperationException("The shipped effect presets are missing from this build.");

    EffectPresets.Use(EffectPresets.Load(shipped));
}
