using TabulaSonora.Midi;
using TabulaSonora.Patches;
using TabulaSonora.Realtime;
using TabulaSonora.Rom;
using TabulaSonora.Voices;

namespace TabulaSonora.Web.Services;

/// <summary>The identity of the DLL the engine is running on.</summary>
/// <param name="Name">File name as the user picked it.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="Sha256">SHA-256 as lower-case hex.</param>
/// <param name="Verified">Whether the full hash was checked this session rather than trusted from storage.</param>
public sealed record RomIdentity(string Name, long Size, string Sha256, bool Verified);

/// <summary>The engine settings the page remembers between visits.</summary>
/// <param name="Map">Which vintage's tone map program changes resolve against.</param>
/// <param name="Reverb">Whether the reverb runs on its send bus.</param>
/// <param name="Chorus">Whether the chorus runs on its send bus.</param>
/// <param name="Delay">Whether the system delay runs on its send bus.</param>
/// <remarks>
/// Exactly the four values <see cref="ToneGeneratorOptions"/> is built from that the page can change,
/// held as one unit so they are restored in a single rebuild rather than four.
/// </remarks>
public sealed record EngineSettings(ToneMap Map, bool Reverb, bool Chorus, bool Delay)
{
    /// <summary>Power-on state: SC-8820, with all three effects running as the module has them.</summary>
    public static EngineSettings Default { get; } = new(ToneMap.Sc8820, Reverb: true, Chorus: true, Delay: true);
}

/// <summary>The loaded song.</summary>
/// <param name="Name">File name as the user picked it.</param>
/// <param name="Events">Parsed events, ordered by position.</param>
/// <param name="LengthSamples">Position of the last event of any kind.</param>
/// <param name="UsedChannels">
/// Bit <c>n</c> is set when some channel voice message addresses channel <c>n</c>.
/// </param>
public sealed record LoadedSong(
    string Name,
    IReadOnlyList<MidiEvent> Events,
    long LengthSamples,
    int UsedChannels)
{
    /// <summary>Duration up to the last event.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(LengthSamples / (double)ToneGenerator.SampleRate);

    /// <summary>Whether the song addresses a channel at all.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <returns>Whether any channel voice message names it.</returns>
    public bool Uses(int channel) => (UsedChannels & (1 << channel)) != 0;
}

/// <summary>
/// One running engine, and everything the page can do to it.
/// </summary>
/// <remarks>
/// <para>
/// Holds the objects in the order the library builds them: a <see cref="RomImage"/> over the bytes
/// from storage, one <see cref="NoteRenderer"/> that loads the tables, and a
/// <see cref="ToneGenerator"/> over that. Changing a setting that lives in
/// <see cref="ToneGeneratorOptions"/> rebuilds the generator but <em>not</em> the note renderer, so
/// switching vintage or effect type costs nothing but the voices that were sounding — the 27 MB of
/// tables are read once per session.
/// </para>
/// <para>
/// Not thread-safe, and does not need to be: Blazor WebAssembly is single-threaded, so the pump, the
/// UI events and the live MIDI callbacks all arrive on the same thread, which is exactly the contract
/// <see cref="ToneGenerator"/> asks for. <see cref="ChannelMask"/> is the one thing the library
/// documents as safe to change from anywhere, and it is shared rather than rebuilt.
/// </para>
/// </remarks>
public sealed class SynthSession : IDisposable
{
    private RomImage? _rom;
    private NoteRenderer? _notes;
    private ToneGenerator? _engine;
    private SequencePlayer? _player;

    private EngineSettings _settings = EngineSettings.Default;
    private int _drumMapRow;

    /// <summary>Per-channel mute and solo, shared with every generator this session builds.</summary>
    public ChannelMask Channels { get; } = new();

    /// <summary>The channel routed to the drum path.</summary>
    /// <remarks>
    /// Read from the engine's own default rather than restated, because this page does not change it:
    /// GM channel 10, which every part of the UI that distinguishes drums from instruments needs.
    /// </remarks>
    public static int DrumChannel { get; } = new ToneGeneratorOptions().DrumChannel;

    /// <summary>
    /// Raised when the loaded ROM or song changes.
    /// </summary>
    /// <remarks>
    /// One component loading a song is another component's business — the export button is enabled by
    /// it, for instance — and Blazor only re-renders the component that handled the event. Without
    /// this, panels sit showing state that stopped being true.
    /// </remarks>
    public event Action? Changed;

    /// <summary>
    /// Raised when one of the four settings in <see cref="EngineSettings"/> changes.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Changed"/> because that also fires on a ROM or song load, and whoever
    /// remembers these values between visits has no business writing storage on those.
    /// </remarks>
    public event Action? SettingsChanged;

    /// <summary>The DLL in use, or <see langword="null"/> before one is loaded.</summary>
    public RomIdentity? Rom { get; private set; }

    /// <summary>The song in use, or <see langword="null"/> if none is loaded.</summary>
    public LoadedSong? Song { get; private set; }

    /// <summary>Whether an engine exists to render from.</summary>
    public bool IsReady => _engine is not null;

    /// <summary>The patch directory, for browsing programs by name.</summary>
    public PatchDirectory? Directory => _notes?.Directory;

    /// <summary>The drum kit table, for browsing kits and their key maps.</summary>
    public DrumKitTable? Drums => _notes?.Drums;

    /// <summary>
    /// The sixteen parts as the running engine holds them, or <see langword="null"/> before one exists.
    /// </summary>
    /// <remarks>
    /// Read-only here by intent. The engine has no MIDI out and nothing else tracks channel state, so
    /// this is the only way a panel can show what a song has done to a channel — but the way to
    /// <em>change</em> one is still to send it a message, which is what the module would receive.
    /// </remarks>
    public IReadOnlyList<Part>? Parts => _engine?.Parts;

    /// <summary>The voice allocator, for per-channel activity.</summary>
    public VoicePool? Voices => _engine?.Voices;

    /// <summary>The drum kit in force, or -1 with no engine.</summary>
    public int DrumKit => _engine?.DrumKit ?? -1;

    /// <summary>Which drum map row a program change on the drum part resolves against.</summary>
    /// <remarks>
    /// Held here as well as on the generator because a vintage or effect change builds a new one, and
    /// a setting the user chose must not be lost to a rebuild it has nothing to do with. Not
    /// remembered between visits: <see cref="EnginePreferences"/> stores exactly the four values a
    /// rebuild depends on, and a fifth field would invalidate every preference already stored.
    /// </remarks>
    public int DrumMapRow
    {
        get => _drumMapRow;
        set
        {
            _drumMapRow = value;
            if (_engine is not null)
            {
                _engine.DrumMapRow = value;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>All four engine settings at once.</summary>
    /// <remarks>
    /// Setting this rebuilds once rather than four times, which is what restoring a remembered set on
    /// start-up and putting them all back to <see cref="EngineSettings.Default"/> both want.
    /// </remarks>
    public EngineSettings Settings
    {
        get => _settings;
        set => Reconfigure(() => _settings = value);
    }

    /// <summary>Which vintage's tone map program changes resolve against.</summary>
    /// <remarks>Setting this rebuilds the generator; sounding voices stop.</remarks>
    public ToneMap Map
    {
        get => _settings.Map;
        set => Reconfigure(() => _settings = _settings with { Map = value });
    }

    /// <summary>Whether the reverb runs on its send bus.</summary>
    public bool Reverb
    {
        get => _settings.Reverb;
        set => Reconfigure(() => _settings = _settings with { Reverb = value });
    }

    /// <summary>Whether the chorus runs on its send bus.</summary>
    public bool Chorus
    {
        get => _settings.Chorus;
        set => Reconfigure(() => _settings = _settings with { Chorus = value });
    }

    /// <summary>Whether the system delay runs on its send bus.</summary>
    public bool Delay
    {
        get => _settings.Delay;
        set => Reconfigure(() => _settings = _settings with { Delay = value });
    }

    /// <summary>How many voices are sounding right now.</summary>
    public int ActiveVoices => _engine?.ActiveVoices ?? 0;

    /// <summary>How many notes have sounded since the last reset.</summary>
    public int NoteCount => _engine?.NoteCount ?? 0;

    /// <summary>Where the renderer has got to, in samples. Ahead of what is audible by the queue depth.</summary>
    public long RenderedSamples => _player?.Position ?? _engine?.Position ?? 0;

    /// <summary>Whether the song has been rendered to its end.</summary>
    public bool SongComplete => _player is not null && Song is not null &&
        _player.Position >= Song.LengthSamples + (TailSeconds * ToneGenerator.SampleRate);

    /// <summary>Seconds of release and effect tail rendered past the last event.</summary>
    public const double TailSeconds = 2.2;

    /// <summary>Loads a DLL and builds the engine over it.</summary>
    /// <param name="image">The whole file's bytes.</param>
    /// <param name="name">File name, for display and error messages.</param>
    /// <param name="expectedSha256">
    /// A hash computed when the file was stored. Supplying it lets the engine verify by size and PE
    /// timestamp only, which avoids re-hashing 27 MB on every page load.
    /// </param>
    /// <exception cref="RomIdentityException">The bytes are not the pinned build.</exception>
    public void LoadRom(byte[] image, string name, string? expectedSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        var trusted = expectedSha256 is { Length: 64 };
        var rom = RomImage.FromMemory(
            image,
            trusted ? RomVerification.Quick : RomVerification.Full,
            name: name);

        // A stored hash still has to match the build this library is pinned to; the Quick check above
        // only skips recomputing it, it does not skip believing it.
        if (trusted && !string.Equals(expectedSha256, rom.Manifest.Dll.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            rom.Dispose();
            throw new RomIdentityException(
                $"The copy of '{name}' in browser storage has SHA-256 {expectedSha256}; the pinned " +
                $"build is {rom.Manifest.Dll.Sha256}. Pick the file again.");
        }

        Unload();

        _rom = rom;
        _notes = new NoteRenderer(rom);
        Rom = new RomIdentity(name, rom.Length, rom.Manifest.Dll.Sha256, Verified: !trusted);
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>Parses a Standard MIDI File and arms it for playback.</summary>
    /// <param name="midi">The file's bytes.</param>
    /// <param name="name">File name, for display.</param>
    /// <exception cref="InvalidDataException">The file is malformed or uses SMPTE timing.</exception>
    public void LoadSong(byte[] midi, string name)
    {
        ArgumentNullException.ThrowIfNull(midi);

        var events = SmfReader.Parse(midi, ToneGenerator.SampleRate);

        // Which channels the file touches at all, so the mixer can dim the twelve strips a four-part
        // sequence never addresses instead of showing sixteen identical dead ones.
        var used = 0;
        foreach (var message in events)
        {
            if (message.Kind == MidiEventKind.Channel)
            {
                used |= 1 << message.Channel;
            }
        }

        Song = new LoadedSong(name, events, events.Count > 0 ? events[^1].Position : 0, used);

        _engine?.Reset();
        _player = _engine is null ? null : new SequencePlayer(_engine, events);
        Changed?.Invoke();
    }

    /// <summary>Forgets the loaded song, leaving the engine up for live playing.</summary>
    public void UnloadSong()
    {
        Song = null;
        _player = null;
        _engine?.Reset();
        Changed?.Invoke();
    }

    /// <summary>Renders the next block of the loaded song, dispatching its events on the way.</summary>
    /// <param name="left">Receives the left channel.</param>
    /// <param name="right">Receives the right channel; must be the same length as <paramref name="left"/>.</param>
    /// <remarks>
    /// Live notes still sound: the sequence and the keyboard feed one generator, so a keyboard can be
    /// played over a running song.
    /// </remarks>
    public void RenderSong(Span<float> left, Span<float> right)
    {
        if (_player is not null)
        {
            _player.Render(left, right);
            return;
        }

        RenderLive(left, right);
    }

    /// <summary>Renders the next block from the generator alone, without advancing any song.</summary>
    /// <param name="left">Receives the left channel.</param>
    /// <param name="right">Receives the right channel; must be the same length as <paramref name="left"/>.</param>
    /// <remarks>
    /// What live playing renders through, including when a song is loaded but stopped — going through
    /// the player there would wind the song forward silently under the keyboard.
    /// </remarks>
    public void RenderLive(Span<float> left, Span<float> right)
    {
        if (_engine is not null)
        {
            _engine.Render(left, right);
            return;
        }

        // The pump reuses its buffers, so leaving them untouched would queue the previous block
        // again — a stutter rather than the silence that is meant.
        left.Clear();
        right.Clear();
    }

    /// <summary>Jumps the song to a position, replaying the controllers up to it.</summary>
    /// <param name="sample">Where to jump to.</param>
    public void Seek(long sample) => _player?.Seek(Math.Max(0, sample));

    /// <summary>Silences everything and returns every part to its power-on state.</summary>
    public void Panic() => _engine?.Reset();

    /// <summary>Applies one channel voice message — what live MIDI and the on-screen keyboard send.</summary>
    /// <param name="status">Status byte.</param>
    /// <param name="data1">First data byte.</param>
    /// <param name="data2">Second data byte.</param>
    public void SendChannel(int status, int data1, int data2) =>
        _engine?.SendChannel(status, data1, data2);

    /// <summary>Applies one Control Change — what the mixer's faders send.</summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <param name="controller">Controller number.</param>
    /// <param name="value">Controller value, 0–127.</param>
    /// <remarks>
    /// A fader really does send the controller rather than setting the part behind the engine's back,
    /// so a running sequence overwrites it at its next controller event exactly as it would on the
    /// module's own front panel. Mute and solo are the ones that survive, because
    /// <see cref="ChannelMask"/> sits at the mix where no message reaches.
    /// </remarks>
    public void SendControl(int channel, int controller, int value) =>
        _engine?.SendChannel(0xB0 | channel, controller, value);

    /// <summary>
    /// Renders the whole song to a WAV file, yielding between chunks so the page stays alive.
    /// </summary>
    /// <param name="progress">Receives progress from 0 to 1.</param>
    /// <param name="cancellation">Cancels the export.</param>
    /// <returns>The complete file.</returns>
    /// <remarks>
    /// Renders through a <em>second</em> generator over the same note renderer, so exporting does not
    /// disturb what is playing and costs no second read of the tables. That is the same block loop
    /// <c>render --stream</c> uses, which is what makes the exported file comparable with one rendered
    /// on the command line rather than merely similar to it.
    /// </remarks>
    public async Task<byte[]> ExportWavAsync(IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        if (_notes is null || Song is null)
        {
            throw new InvalidOperationException("Load a DLL and a song before exporting.");
        }

        // The drum map row goes with it: an export that resolved kits through a different map than the
        // one being listened to would be a different arrangement, not a rendering of this one.
        var engine = new ToneGenerator(_notes, Options()) { DrumMapRow = _drumMapRow };
        var player = new SequencePlayer(engine, Song.Events);

        var total = (int)(Song.LengthSamples + (TailSeconds * ToneGenerator.SampleRate));
        var left = new float[total];
        var right = new float[total];

        // A quarter of a second at a time: long enough that the yield costs nothing measurable,
        // short enough that the frame it lands in is not visibly dropped.
        const int Chunk = ToneGenerator.SampleRate / 4;

        for (var start = 0; start < total; start += Chunk)
        {
            cancellation.ThrowIfCancellationRequested();

            var count = Math.Min(Chunk, total - start);
            player.Render(left.AsSpan(start, count), right.AsSpan(start, count));
            progress?.Report((start + count) / (double)total);

            await Task.Yield();
        }

        return WavWriter.ToBytes(left, right, ToneGenerator.SampleRate);
    }

    private ToneGeneratorOptions Options() => new()
    {
        Map = _settings.Map,
        Reverb = _settings.Reverb,
        Chorus = _settings.Chorus,
        Delay = _settings.Delay,
        Channels = Channels,
    };

    private void Reconfigure(Action change)
    {
        change();
        Rebuild();

        SettingsChanged?.Invoke();

        // Changing vintage renames the whole program list, so this is not only the engine's business.
        Changed?.Invoke();
    }

    private void Rebuild()
    {
        if (_notes is null)
        {
            return;
        }

        var position = _player?.Position ?? 0;

        _engine = new ToneGenerator(_notes, Options()) { DrumMapRow = _drumMapRow };
        _player = Song is null ? null : new SequencePlayer(_engine, Song.Events);

        // Put the new generator back where the old one was, so changing vintage mid-song resumes
        // rather than restarting. Seek replays the controllers, which is what makes that sound right.
        if (position > 0)
        {
            _player?.Seek(position);
        }
    }

    private void Unload()
    {
        _engine = null;
        _player = null;
        _notes = null;
        Rom = null;

        _rom?.Dispose();
        _rom = null;
    }

    /// <summary>Releases the ROM image.</summary>
    /// <remarks>
    /// Also what "forget the stored DLL" calls, so it announces itself — the page has to collapse back
    /// to the loader. On teardown the listeners have already detached and the notice goes nowhere.
    /// </remarks>
    public void Dispose()
    {
        Unload();
        Changed?.Invoke();
    }
}
