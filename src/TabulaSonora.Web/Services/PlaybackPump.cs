using Microsoft.JSInterop;
using TabulaSonora.Realtime;

namespace TabulaSonora.Web.Services;

/// <summary>What the pump is rendering.</summary>
public enum PlaybackMode
{
    /// <summary>A sequence, with its events dispatched as it goes.</summary>
    Song,

    /// <summary>Whatever is played into the engine right now, and nothing else.</summary>
    Live,
}

/// <summary>What the transport is doing.</summary>
public enum TransportState
{
    /// <summary>Nothing is being rendered.</summary>
    Stopped,

    /// <summary>Rendering and sounding.</summary>
    Playing,

    /// <summary>Rendering stopped, whatever was queued kept.</summary>
    Paused,
}

/// <summary>
/// Keeps the audio device fed.
/// </summary>
/// <remarks>
/// <para>
/// The audio thread cannot call into .NET, so nothing pulls blocks out of the engine; this pushes them
/// in, staying about a second ahead of the speaker. The engine renders far faster than realtime, so
/// each wake-up does a few milliseconds of work and gives the thread straight back — which is what
/// keeps the page responsive on the one thread WebAssembly gives us.
/// </para>
/// <para>
/// A lead of a second is deliberately generous. The cost of too much is a slow response to a seek,
/// which is flushed anyway; the cost of too little is a dropout, which is audible and unrecoverable.
/// </para>
/// </remarks>
public sealed class PlaybackPump(SynthSession session, AudioOutput audio) : IDisposable
{
    /// <summary>Frames rendered per pass while a song is playing — 128 ms at the engine's rate.</summary>
    public const int SongChunkFrames = 4096;

    /// <summary>Frames kept queued ahead of the device while a song is playing.</summary>
    public const int SongLeadFrames = ToneGenerator.SampleRate;

    /// <summary>Frames rendered per pass when playing live — 8 ms.</summary>
    public const int LiveChunkFrames = 256;

    /// <summary>Frames kept queued ahead of the device when playing live — 40 ms.</summary>
    /// <remarks>
    /// The floor is set by how late the pump can wake, not by how fast it renders: the loop is driven
    /// by a timer the browser is free to delay, so the queue has to cover a missed wake-up. Below
    /// about a poll interval and a half this trades audible latency for audible dropouts.
    /// </remarks>
    public const int LiveLeadFrames = 1280;

    /// <summary>
    /// How often the fallback loop wakes, for redraws and for when the worklet is not reporting.
    /// </summary>
    /// <remarks>
    /// Filling is driven by the worklet, not by this. A Blazor render is not free and a clock reading
    /// tenths cannot show more than ten a second, so this is deliberately lazy.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    // One pair per mode rather than one pair sliced, because the arrays are marshalled to JavaScript
    // whole: a slice would mean a copy on every push, hundreds of times a second when playing live.
    private readonly float[] _songLeft = new float[SongChunkFrames];
    private readonly float[] _songRight = new float[SongChunkFrames];
    private readonly float[] _liveLeft = new float[LiveChunkFrames];
    private readonly float[] _liveRight = new float[LiveChunkFrames];

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private DotNetObjectReference<PlaybackPump>? _self;
    private bool _filling;

    private readonly System.Diagnostics.Stopwatch _renderTime = new();
    private long _renderedFrames;

    /// <summary>Raised after each pass, for the UI to redraw from.</summary>
    public event Action? Tick;

    /// <summary>What the transport is doing.</summary>
    public TransportState State { get; private set; } = TransportState.Stopped;

    /// <summary>What the pump is rendering.</summary>
    public PlaybackMode Mode { get; private set; } = PlaybackMode.Live;

    /// <summary>The device state as of the last pass.</summary>
    public AudioStatus? Audio { get; private set; }

    /// <summary>
    /// Seconds of audio produced per second spent rendering, measured over the last few seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number that says whether playback can work at all. Above 1 the renderer is faster than the
    /// music and the queue fills; at or below 1 it can never catch up and the device starves no matter
    /// how the pump is tuned. Polyphony moves it, so a thin passage can read 4× and a dense one 0.9×
    /// in the same song.
    /// </para>
    /// <para>
    /// Kept rather than removed after the tuning it was added for: it is the one measurement that
    /// distinguishes "not enough throughput" from "enough throughput, badly scheduled", and those
    /// have completely different fixes.
    /// </para>
    /// </remarks>
    public double RealtimeFactor { get; private set; }

    /// <summary>
    /// The position the listener is actually hearing, in samples.
    /// </summary>
    /// <remarks>
    /// Not where the renderer has got to: that runs a second ahead, and a progress bar driven from it
    /// would show the song finishing before it is heard to.
    /// </remarks>
    public long AudiblePosition =>
        Math.Max(0, session.RenderedSamples - (Audio?.Queued ?? 0));

    /// <summary>Starts or resumes the loaded song.</summary>
    public Task PlaySongAsync() => StartAsync(PlaybackMode.Song);

    /// <summary>
    /// Opens the device for live playing, without starting any loaded song.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PlaySongAsync"/> because the two want opposite buffering, and because
    /// a song being <em>loaded</em> says nothing about whether it is running: playing a keyboard over
    /// a stopped song must not wind that song forward underneath it.
    /// </remarks>
    public Task ArmLiveAsync() => StartAsync(PlaybackMode.Live);

    /// <summary>
    /// Opens the device for live playing unless a song is already running.
    /// </summary>
    /// <returns>A task that completes when the device is ready to be played into.</returns>
    /// <remarks>
    /// What every control that can sound a note calls before sounding it — nothing is audible until a
    /// context exists, and a browser only starts one inside a gesture. The exception is the point: a
    /// running song must not be reduced to a 40 ms lead just because a key was pressed over it, so
    /// while one plays, live notes ride its buffer and arrive late instead.
    /// </remarks>
    public Task ArmForKeysAsync() =>
        Mode == PlaybackMode.Song && State == TransportState.Playing
            ? Task.CompletedTask
            : ArmLiveAsync();

    private async Task StartAsync(PlaybackMode mode)
    {
        if (!session.IsReady)
        {
            return;
        }

        // Switching modes changes how much is buffered, so what is already queued is the wrong
        // length by definition and has to go.
        if (Mode != mode && audio.IsStarted)
        {
            await audio.FlushAsync();
        }

        Mode = mode;

        if (!audio.IsStarted)
        {
            // Attached before the context opens, so the first queue report has somewhere to go.
            _self ??= DotNetObjectReference.Create(this);
            await audio.AttachAsync(_self);
            Audio = await audio.StartAsync();
        }

        await audio.ResumeAsync();

        State = TransportState.Playing;

        // Fill the ring before letting the worklet drain it, so playback does not begin on an
        // underrun the listener would hear as a stutter at the top of every song.
        await PrimeAsync();
        await audio.PlayAsync();

        _cancellation ??= new CancellationTokenSource();
        _loop ??= RunAsync(_cancellation.Token);
    }

    /// <summary>Stops rendering, keeping what is queued.</summary>
    public async Task PauseAsync()
    {
        State = TransportState.Paused;
        await audio.PauseAsync();
    }

    /// <summary>Stops rendering and discards the queue.</summary>
    public async Task StopAsync()
    {
        State = TransportState.Stopped;
        await audio.PauseAsync();
        await audio.FlushAsync();
    }

    /// <summary>Jumps to a position and discards everything already rendered past it.</summary>
    /// <param name="sample">Where to jump to.</param>
    /// <remarks>
    /// The queue has to go: it holds up to a second of the passage being left, and playing it out
    /// after the jump would be heard as the seek arriving late.
    /// </remarks>
    public async Task SeekAsync(long sample)
    {
        session.Seek(sample);
        await audio.FlushAsync();

        if (State == TransportState.Playing)
        {
            await PrimeAsync();
            await audio.PlayAsync();
        }
    }

    /// <summary>
    /// Tops the queue up. Called by the worklet every 10 ms of audio, from the audio clock.
    /// </summary>
    /// <param name="queued">Frames the worklet still holds.</param>
    /// <returns>A task that completes when the queue has been refilled.</returns>
    /// <remarks>
    /// This, and not a timer, is what makes a 40 ms lead safe. A <c>setTimeout</c> is delayed by
    /// whatever else the page is doing — a layout, a garbage collection, a background tab — so a
    /// timer-driven pump has to carry a queue deep enough to survive the worst of those, and that
    /// depth is latency the player feels on every key. The audio thread has no such problem: it
    /// reports on the audio clock, so the queue only has to cover the render itself.
    /// </remarks>
    [JSInvokable]
    public async Task OnQueueReportAsync(int queued)
    {
        // Reports keep arriving while a fill is in flight; without this they would interleave into
        // the same buffers and the same generator.
        if (_filling || State != TransportState.Playing)
        {
            return;
        }

        _filling = true;
        try
        {
            await FillAsync(queued);

            if (Mode == PlaybackMode.Song && session.SongComplete && queued == 0)
            {
                await StopAsync();
            }
        }
        finally
        {
            _filling = false;
        }
    }

    private async Task PrimeAsync()
    {
        var status = await audio.StatusAsync();
        await FillAsync(status.Queued);
    }

    private async Task FillAsync(int queued)
    {
        // How far ahead the pump runs is not one number. A song wants a generous lead: the only cost
        // is a slower response to a seek, which flushes the queue anyway, and the benefit is that no
        // scheduling hiccup can reach the speaker. Live playing wants the opposite, because there the
        // lead IS the latency between pressing a key and hearing it.
        var live = Mode == PlaybackMode.Live;
        var lead = live ? LiveLeadFrames : SongLeadFrames;
        var left = live ? _liveLeft : _songLeft;
        var right = live ? _liveRight : _songRight;

        while (queued < lead)
        {
            if (!live && session.SongComplete)
            {
                break;
            }

            _renderTime.Start();
            if (live)
            {
                session.RenderLive(left, right);
            }
            else
            {
                session.RenderSong(left, right);
            }

            _renderTime.Stop();

            _renderedFrames += left.Length;
            await audio.PushAsync(left, right);

            queued += left.Length;
        }

        Measure();
        Audio = Audio is { } previous ? previous with { Queued = queued } : Audio;
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                // Filling is the worklet's job now. This exists to refresh the fields a report does
                // not carry, to redraw at a rate a person can read, and to keep the queue moving if
                // the worklet ever stops reporting.
                Audio = await audio.StatusAsync();

                if (State == TransportState.Playing && !_filling)
                {
                    await OnQueueReportAsync(Audio.Queued);
                }

                Tick?.Invoke();
                await Task.Delay(PollInterval, cancellation);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed while waiting; nothing to unwind.
        }
    }

    // Averaged over a few seconds of audio and then restarted, so the reading follows the passage
    // being played rather than smearing a dense one into everything that came before it.
    private void Measure()
    {
        const int Window = ToneGenerator.SampleRate * 3;
        if (_renderedFrames < Window)
        {
            return;
        }

        var spent = _renderTime.Elapsed.TotalSeconds;
        if (spent > 0)
        {
            RealtimeFactor = _renderedFrames / (double)ToneGenerator.SampleRate / spent;
        }

        _renderedFrames = 0;
        _renderTime.Reset();
    }

    /// <summary>Stops the pump.</summary>
    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _loop = null;

        _self?.Dispose();
        _self = null;
    }
}
