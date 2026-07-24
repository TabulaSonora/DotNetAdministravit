using System.Runtime.InteropServices;
using Microsoft.JSInterop;

namespace TabulaSonora.Web.Services;

/// <summary>What the audio device is doing.</summary>
/// <param name="SampleRate">Rate the browser actually opened the context at.</param>
/// <param name="State">Web Audio context state: <c>running</c>, <c>suspended</c> or <c>closed</c>.</param>
/// <param name="Resampling">Whether the host rate differs from the engine's 32 kHz.</param>
/// <param name="Queued">Frames sitting in the worklet's ring, ahead of the speaker.</param>
/// <param name="Starved">How many frames the worklet has had to invent since it started.</param>
public sealed record AudioStatus(int SampleRate, string State, bool Resampling, int Queued, int Starved)
{
    /// <summary>How far ahead of the speaker the render has got.</summary>
    public TimeSpan Lead => TimeSpan.FromSeconds(Queued / (double)Realtime.ToneGenerator.SampleRate);
}

/// <summary>
/// Audio output through a Web Audio worklet.
/// </summary>
/// <remarks>
/// <para>
/// The context is asked for 32 kHz — the engine's own rate — and where the browser agrees, nothing
/// resamples between the last mix and the device, which is the same property the desktop player gets
/// by opening its device at 32 kHz. Where the browser refuses, the worklet interpolates, and
/// <see cref="AudioStatus.Resampling"/> says so.
/// </para>
/// <para>
/// Blocks are pushed rather than pulled: the audio thread cannot call into .NET, so the engine renders
/// ahead into the worklet's ring and the worklet reports how much is left.
/// </para>
/// </remarks>
public sealed class AudioOutput(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private byte[] _transfer = [];

    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/audio.js");

    /// <summary>Whether the context has been created.</summary>
    public bool IsStarted { get; private set; }

    /// <summary>Registers the object the worklet's queue reports call back into.</summary>
    /// <param name="listener">Reference to a type with the callback the worklet invokes.</param>
    /// <remarks>
    /// Call before <see cref="StartAsync"/>, so no report is dropped between the context opening and
    /// the listener being known.
    /// </remarks>
    public async ValueTask AttachAsync<T>(DotNetObjectReference<T> listener) where T : class =>
        await (await ModuleAsync()).InvokeVoidAsync("attach", listener);

    /// <summary>Creates the context and loads the worklet.</summary>
    /// <returns>The device state after starting.</returns>
    public async ValueTask<AudioStatus> StartAsync()
    {
        var status = await (await ModuleAsync())
            .InvokeAsync<AudioStatus>("start", Realtime.ToneGenerator.SampleRate);

        IsStarted = true;
        return status;
    }

    /// <summary>Resumes a context the browser suspended pending a user gesture.</summary>
    /// <returns>The context state afterwards.</returns>
    public async ValueTask<string> ResumeAsync() =>
        await (await ModuleAsync()).InvokeAsync<string>("resume");

    /// <summary>Reads the device state.</summary>
    /// <returns>The current status.</returns>
    public async ValueTask<AudioStatus> StatusAsync() =>
        await (await ModuleAsync()).InvokeAsync<AudioStatus>("status");

    /// <summary>Queues a rendered block.</summary>
    /// <param name="left">Left channel.</param>
    /// <param name="right">Right channel; must be the same length as <paramref name="left"/>.</param>
    /// <exception cref="ArgumentException">The two channels are different lengths.</exception>
    /// <remarks>
    /// <para>
    /// Sent as one <see cref="byte"/> array, the two channels laid end to end, and <em>not</em> as two
    /// <c>float[]</c>. That is not a micro-optimisation: Blazor marshals a <c>float[]</c> argument by
    /// serialising it to JSON, so pushing blocks that way turns every sample into decimal text and
    /// back. Byte arrays have their own transport and are copied as bytes.
    /// </para>
    /// <para>
    /// The transfer buffer is reused, which is safe because the caller awaits each push before
    /// rendering the next block into it.
    /// </para>
    /// </remarks>
    public async ValueTask PushAsync(ReadOnlyMemory<float> left, ReadOnlyMemory<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("The two channels must be the same length.", nameof(right));
        }

        var bytes = left.Length * sizeof(float);
        if (_transfer.Length < bytes * 2)
        {
            _transfer = new byte[bytes * 2];
        }

        MemoryMarshal.AsBytes(left.Span).CopyTo(_transfer);
        MemoryMarshal.AsBytes(right.Span).CopyTo(_transfer.AsSpan(bytes));

        await (await ModuleAsync()).InvokeVoidAsync("push", _transfer, left.Length);
    }

    /// <summary>Starts draining the ring to the device.</summary>
    public async ValueTask PlayAsync() => await (await ModuleAsync()).InvokeVoidAsync("play");

    /// <summary>Stops draining the ring, keeping what is in it.</summary>
    public async ValueTask PauseAsync() => await (await ModuleAsync()).InvokeVoidAsync("pause");

    /// <summary>Discards everything queued — what a seek has to do.</summary>
    public async ValueTask FlushAsync() => await (await ModuleAsync()).InvokeVoidAsync("flush");

    /// <summary>Sets the output gain.</summary>
    /// <param name="gain">Linear gain.</param>
    public async ValueTask SetGainAsync(double gain) =>
        await (await ModuleAsync()).InvokeVoidAsync("setGain", gain);

    /// <summary>Offers a rendered file to the browser as a download.</summary>
    /// <param name="name">Suggested file name.</param>
    /// <param name="bytes">The complete file.</param>
    public async ValueTask DownloadAsync(string name, byte[] bytes) =>
        await (await ModuleAsync()).InvokeVoidAsync("download", name, bytes);

    /// <summary>Closes the context and releases the module.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("stop");
        }
        catch (JSDisconnectedException)
        {
            // The page is going away; there is no context left to close.
        }

        await _module.DisposeAsync();
        _module = null;
        IsStarted = false;
    }
}
