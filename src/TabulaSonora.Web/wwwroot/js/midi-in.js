// Web MIDI input. Messages go straight through to the engine's own SendChannel, which is what the
// hardware's MIDI in does -- there is no sequencer in between and nothing is quantised.

let access = null;
let listener = null;
let selected = null;

export async function open(dotnet) {
    if (!navigator.requestMIDIAccess) {
        return null;
    }

    listener = dotnet;
    access = await navigator.requestMIDIAccess({ sysex: false });
    access.onstatechange = () => listener?.invokeMethodAsync('OnDevicesChanged');
    return devices();
}

export function devices() {
    if (!access) {
        return [];
    }

    return Array.from(access.inputs.values()).map(input => ({
        id: input.id,
        name: input.name ?? input.id,
        manufacturer: input.manufacturer ?? '',
        connected: input.id === selected,
    }));
}

export function listen(id) {
    if (!access) {
        return false;
    }

    for (const input of access.inputs.values()) {
        input.onmidimessage = null;
    }

    selected = null;
    if (!id) {
        return true;
    }

    const input = access.inputs.get(id);
    if (!input) {
        return false;
    }

    input.onmidimessage = event => {
        const data = event.data;

        // Real-time messages (0xF8 and up) arrive constantly from a clock-sending device and the
        // engine has nothing to do with them.
        if (data.length < 2 || data[0] >= 0xf8) {
            return;
        }

        listener?.invokeMethodAsync('OnMidiMessage', data[0], data[1], data.length > 2 ? data[2] : 0);
    };

    selected = id;
    return true;
}
