// The main-thread end of audio output: opens a context at the engine's own rate where the browser
// allows it, loads the worklet, and carries rendered blocks across to it.

let context = null;
let node = null;
let gain = null;
let pump = null;
let queued = 0;
let starved = 0;

// The .NET object woken when the worklet reports. Set before start() so no report is missed.
export function attach(dotnet) {
    pump = dotnet;
}

// Ask for 32 kHz explicitly. Every browser that honours it gives us a path to the device with no
// resampling anywhere in it; the ones that do not fall back to their own rate and the worklet
// interpolates. Either way the engine itself never runs at anything but 32 kHz.
//
// 'interactive', not 'playback': the hint asks the browser for a device buffer, and 'playback' asks
// for a deliberately large one. That buffer is added to whatever the pump is holding, and it cannot
// be shortened afterwards -- so choosing it would put a floor under live playing that no amount of
// tuning on our side could lift. The ring here already provides the safety a song needs.
export async function start(sourceRate) {
    if (context) {
        return status();
    }

    try {
        context = new AudioContext({ sampleRate: sourceRate, latencyHint: 'interactive' });
    } catch {
        context = new AudioContext({ latencyHint: 'interactive' });
    }

    await context.audioWorklet.addModule('js/synth-processor.js');

    node = new AudioWorkletNode(context, 'synth-processor', {
        numberOfInputs: 0,
        numberOfOutputs: 1,
        outputChannelCount: [2],
        processorOptions: { sourceRate },
    });

    // The worklet reports its queue depth from the audio thread, on the audio clock. Waking .NET from
    // that rather than from a timer is what makes a short lead safe: a setTimeout is delayed
    // arbitrarily by whatever else the page is doing, and the queue would have to be deep enough to
    // survive the worst of it. This arrives every 10 ms of *audio*, whatever the page is doing.
    node.port.onmessage = event => {
        queued = event.data.queued;
        starved = event.data.starved;
        pump?.invokeMethodAsync('OnQueueReportAsync', queued);
    };

    gain = new GainNode(context, { gain: 1 });
    node.connect(gain).connect(context.destination);

    return status();
}

// Browsers start a context suspended until a gesture. Every transport control calls this first.
export async function resume() {
    if (context && context.state !== 'running') {
        await context.resume();
    }

    return context ? context.state : 'closed';
}

export function status() {
    return {
        sampleRate: context ? context.sampleRate : 0,
        state: context ? context.state : 'closed',
        resampling: context ? Math.abs(context.sampleRate - 32000) > 0.5 : false,
        queued,
        starved,
    };
}

// One block arrives as raw bytes -- the two channels laid end to end -- because that is the only
// shape Blazor moves in bulk without turning every sample into JSON.
//
// The copy is not avoidable and not wasted: it detaches the samples from .NET's heap, which the
// transfer to the audio thread requires and which the reused buffer on the other side demands
// anyway.
export function push(bytes, frames) {
    if (!node) {
        return;
    }

    const wanted = frames * 2 * Float32Array.BYTES_PER_ELEMENT;
    const samples = new Float32Array(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + wanted));

    queued += frames;
    node.port.postMessage({ command: 'push', samples, frames }, [samples.buffer]);
}

export function play() {
    node?.port.postMessage({ command: 'play' });
}

export function pause() {
    node?.port.postMessage({ command: 'pause' });
}

export function flush() {
    queued = 0;
    node?.port.postMessage({ command: 'flush' });
}

export function setGain(value) {
    if (gain) {
        gain.gain.value = value;
    }
}

export async function stop() {
    if (!context) {
        return;
    }

    node.disconnect();
    gain.disconnect();
    await context.close();
    context = node = gain = null;
    queued = starved = 0;
}

// Hands a rendered file to the browser as a download. The bytes are already a complete WAV.
export function download(name, bytes) {
    const blob = new Blob([bytes], { type: 'audio/wav' });
    const url = URL.createObjectURL(blob);

    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = name;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();

    // Give the click a turn to be handled before the URL stops resolving.
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
