using Microsoft.JSInterop;

namespace TabulaSonora.Web.Services;

/// <summary>One MIDI input the browser can see.</summary>
/// <param name="Id">Stable identifier to select it by.</param>
/// <param name="Name">Port name.</param>
/// <param name="Manufacturer">Manufacturer, where the browser reports one.</param>
/// <param name="Connected">Whether this is the port currently being listened to.</param>
public sealed record MidiDevice(string Id, string Name, string Manufacturer, bool Connected);

/// <summary>
/// Live MIDI in, straight into the engine.
/// </summary>
/// <remarks>
/// Messages go to <see cref="SynthSession.SendChannel"/> as they arrive, which is what the hardware's
/// MIDI in does: there is no sequencer in between, nothing is quantised, and a note is held for
/// exactly as long as the key is. Playing over a running song works for the same reason — both feed
/// one generator.
/// </remarks>
public sealed class MidiInput(IJSRuntime js, SynthSession session) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<MidiInput>? _self;

    /// <summary>Whether the browser granted access to MIDI devices.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Whether this browser has the Web MIDI API at all.</summary>
    public bool IsSupported { get; private set; } = true;

    /// <summary>The inputs the browser can see.</summary>
    public IReadOnlyList<MidiDevice> Devices { get; private set; } = [];

    /// <summary>Raised when the device list changes, or a message arrives that the UI shows.</summary>
    public event Action? Changed;

    /// <summary>Requests access and enumerates the inputs.</summary>
    /// <returns>Whether access was granted.</returns>
    public async Task<bool> OpenAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/midi-in.js");
        _self ??= DotNetObjectReference.Create(this);

        var devices = await _module.InvokeAsync<MidiDevice[]?>("open", _self);
        if (devices is null)
        {
            IsSupported = false;
            return false;
        }

        Devices = devices;
        IsOpen = true;
        return true;
    }

    /// <summary>Listens to one input, or to none.</summary>
    /// <param name="deviceId">The input to listen to; <see langword="null"/> detaches from all of them.</param>
    public async Task ListenAsync(string? deviceId)
    {
        if (_module is null)
        {
            return;
        }

        await _module.InvokeAsync<bool>("listen", deviceId);
        await RefreshAsync();
    }

    /// <summary>Re-reads the device list.</summary>
    public async Task RefreshAsync()
    {
        if (_module is null)
        {
            return;
        }

        Devices = await _module.InvokeAsync<MidiDevice[]>("devices");
        Changed?.Invoke();
    }

    /// <summary>Receives one channel voice message from the browser.</summary>
    /// <param name="status">Status byte.</param>
    /// <param name="data1">First data byte.</param>
    /// <param name="data2">Second data byte.</param>
    [JSInvokable]
    public void OnMidiMessage(int status, int data1, int data2) =>
        session.SendChannel(status, data1, data2);

    /// <summary>Receives notice that the browser's device list changed.</summary>
    /// <returns>A task that completes when the list has been re-read.</returns>
    [JSInvokable]
    public Task OnDevicesChanged() => RefreshAsync();

    /// <summary>Detaches from every input and releases the module.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeAsync<bool>("listen", (string?)null);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going away; the ports go with it.
            }

            _module = null;
        }

        _self?.Dispose();
        _self = null;
    }
}
