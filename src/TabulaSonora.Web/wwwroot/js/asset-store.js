// Persistent storage for the two files the engine needs and the page cannot ship: the user's own
// SCCore.dll, and optionally their own presets.json.
//
// The DLL is 27 MB. Reading it and writing it stays entirely on this side -- the bytes never travel
// through Blazor's InputFile stream, which is the wrong shape for a file this size. .NET asks for
// them once, into a buffer it has already sized, through readInto below.

const DB_NAME = 'tabula-sonora';
const DB_VERSION = 1;
const STORE = 'assets';

let cached = new Map();

function open() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);
        request.onupgradeneeded = () => request.result.createObjectStore(STORE);
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function transact(db, mode, work) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, mode);
        const request = work(tx.objectStore(STORE));
        tx.oncomplete = () => resolve(request ? request.result : undefined);
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error);
    });
}

async function sha256Hex(buffer) {
    const digest = await crypto.subtle.digest('SHA-256', buffer);
    return Array.from(new Uint8Array(digest))
        .map(b => b.toString(16).padStart(2, '0'))
        .join('');
}

// Ask the browser not to evict us. Without it a 27 MB record is "best effort" storage and can be
// cleared under disk pressure, which would silently send the user back to the file picker.
export async function requestPersistence() {
    if (!navigator.storage?.persist) {
        return false;
    }

    return (await navigator.storage.persisted()) || (await navigator.storage.persist());
}

// Metadata only. The bytes stay in IndexedDB until .NET actually asks for them, so a page load that
// finds nothing cached costs one small read rather than 27 MB of decoding.
export async function describe(key) {
    const db = await open();
    try {
        const record = await transact(db, 'readonly', store => store.get(key));
        if (!record) {
            return null;
        }

        return {
            key,
            name: record.name,
            size: record.bytes.byteLength,
            sha256: record.sha256,
            savedAt: record.savedAt,
        };
    } finally {
        db.close();
    }
}

// Reads the file sitting in a picker, hashes it, and stores it. Taking the element rather than a
// Blazor IBrowserFile is what keeps 27 MB off the interop path twice over: the bytes go from the
// File straight into IndexedDB, and .NET only ever sees them once, on the way into the engine.
export async function putFromInput(key, element) {
    const file = element?.files?.[0];
    if (!file) {
        return null;
    }

    const bytes = await file.arrayBuffer();
    const sha256 = await sha256Hex(bytes);
    const record = { name: file.name, bytes, sha256, savedAt: new Date().toISOString() };

    const db = await open();
    try {
        await transact(db, 'readwrite', store => store.put(record, key));
    } finally {
        db.close();
    }

    cached.set(key, bytes);
    return { key, name: record.name, size: bytes.byteLength, sha256, savedAt: record.savedAt };
}

export async function remove(key) {
    cached.delete(key);
    const db = await open();
    try {
        await transact(db, 'readwrite', store => store.delete(key));
    } finally {
        db.close();
    }
}

// Hands the stored bytes to .NET, which reads them into a buffer of its own.
//
// Returns the array itself, NOT DotNet.createJSStreamReference(it): when the .NET side declares the
// result as IJSStreamReference, Blazor calls that for us on whatever comes back. Wrapping it here as
// well hands it a plain object to wrap a second time, which fails with "Supplied value is not a
// typed array or blob".
export async function readStream(key) {
    let bytes = cached.get(key);
    if (!bytes) {
        const db = await open();
        try {
            const record = await transact(db, 'readonly', store => store.get(key));

            // Empty rather than null: Blazor dereferences whatever comes back on its way to a stream
            // reference, so returning null would throw inside the framework instead of here.
            if (!record) {
                return new Uint8Array(0);
            }

            bytes = record.bytes;
        } finally {
            db.close();
        }

        cached.set(key, bytes);
    }

    return new Uint8Array(bytes);
}

// The 27 MB stays referenced above so a retry does not re-read IndexedDB, but it is dead weight once
// .NET has its own copy. Called after the ROM is handed over.
export function releaseCache(key) {
    cached.delete(key);
}

export async function readText(key) {
    const db = await open();
    try {
        const record = await transact(db, 'readonly', store => store.get(key));
        return record ? new TextDecoder().decode(record.bytes) : null;
    } finally {
        db.close();
    }
}
