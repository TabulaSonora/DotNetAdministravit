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
    /// <summary>Frames rendered per pass — 8 ms at the engine's rate.</summary>
    public const int ChunkFrames = 256;

    /// <summary>
    /// Frames kept queued ahead of the device — 40 ms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One lead for both modes, and it is the short one. A song used to run a full second ahead on the
    /// reasoning that the only cost was a slow response to a seek, which flushes the queue anyway.
    /// That reasoning was incomplete: the mixer changes a channel <em>while</em> the song plays, and a
    /// second of queued audio is a second before the fader is heard to move. Control latency is the
    /// cost of a deep queue, not just seek latency.
    /// </para>
    /// <para>
    /// What makes 40 ms safe is that filling is driven by the worklet's report on the audio clock
    /// rather than by a timer the browser can delay — see <see cref="OnQueueReportAsync"/>. The queue
    /// only has to cover the render itself, and the engine renders a block in a small fraction of the
    /// time that block lasts. Where it does not, the transport says so: the starved-frame count and
    /// the realtime factor are the readouts to check before deciding this is too short.
    /// </para>
    /// </remarks>
    public const int LeadFrames = 1280;

    /// <summary>
    /// How often the fallback loop wakes, for redraws and for when the worklet is not reporting.
    /// </summary>
    /// <remarks>
    /// Filling is driven by the worklet, not by this. A Blazor render is not free and a clock reading
    /// tenths cannot show more than ten a second, so this is deliberately lazy.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    // Marshalled to JavaScript whole, so these are reused rather than sliced: a slice would mean a
    // copy on every push, hundreds of times a second.
    private readonly float[] _left = new float[ChunkFrames];
    private readonly float[] _right = new float[ChunkFrames];

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
    /// context exists, and a browser only starts one inside a gesture. The exception is that a running
    /// song stays the thing being rendered: a key pressed over it plays into the same generator and
    /// needs no mode change, and switching would flush the song's queue mid-bar.
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

        // Both modes queue the same depth now, so this is no longer about buffer length: what is
        // queued belongs to the other mode. Arming live over a stopped song would otherwise play out
        // the song's tail under the first key.
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
        var live = Mode == PlaybackMode.Live;

        while (queued < LeadFrames)
        {
            if (!live && session.SongComplete)
            {
                break;
            }

            _renderTime.Start();
            if (live)
            {
                session.RenderLive(_left, _right);
            }
            else
            {
                session.RenderSong(_left, _right);
            }

            _renderTime.Stop();

            _renderedFrames += _left.Length;
            await audio.PushAsync(_left, _right);

            queued += _left.Length;
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
