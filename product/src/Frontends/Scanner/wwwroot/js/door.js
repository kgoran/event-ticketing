// The scanner's browser side (D62): the camera with jsQR, ES256 with WebCrypto, and IndexedDB.
// Both the camera and WebCrypto need a secure context: https, or http on localhost as in the lab.

let stream = null;
let running = false;
let last = { text: null, at: 0 };

export async function startCamera(video, page) {
    stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
    video.srcObject = stream;
    await video.play();
    running = true;
    const canvas = document.createElement('canvas');
    const context = canvas.getContext('2d', { willReadFrequently: true });
    const tick = () => {
        if (!running) {
            return;
        }
        if (video.readyState === video.HAVE_ENOUGH_DATA) {
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            context.drawImage(video, 0, 0, canvas.width, canvas.height);
            const image = context.getImageData(0, 0, canvas.width, canvas.height);
            const started = performance.now();
            const code = jsQR(image.data, image.width, image.height, { inversionAttempts: 'dontInvert' });
            // The same code held in front of the camera is one scan, not thirty a second.
            if (code && code.data && (code.data !== last.text || performance.now() - last.at > 3000)) {
                last = { text: code.data, at: performance.now() };
                page.invokeMethodAsync('OnCodeAsync', code.data, performance.now() - started);
            }
        }
        requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
}

export function stopCamera() {
    running = false;
    stream?.getTracks().forEach(track => track.stop());
    stream = null;
}

// ES256 (ECDSA P-256 with SHA-256) over the JWS's "header.payload", with the key its header names.
// A JWS signature is r and s side by side, which is what WebCrypto expects.
const imported = new Map();

export async function verify(jws, keys) {
    const parts = jws.split('.');
    if (parts.length !== 3) {
        return false;
    }
    try {
        const header = JSON.parse(new TextDecoder().decode(fromBase64Url(parts[0])));
        const jwk = keys.find(key => key.kid === header.kid);
        if (!jwk || header.alg !== 'ES256') {
            return false;
        }
        let key = imported.get(jwk.kid);
        if (!key) {
            key = await crypto.subtle.importKey('jwk', { kty: 'EC', crv: 'P-256', x: jwk.x, y: jwk.y, ext: true },
                { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
            imported.set(jwk.kid, key);
        }
        return await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, fromBase64Url(parts[2]),
            new TextEncoder().encode(parts[0] + '.' + parts[1]));
    } catch {
        return false;
    }
}

function fromBase64Url(text) {
    const base64 = text.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(text.length / 4) * 4, '=');
    return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}

// IndexedDB: the door list (a few MB for a big event), the scans waiting to upload, the device.
const database = 'eventticketing-door';
const storeName = 'door';

function open() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(database, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(storeName);
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

async function transact(mode, work) {
    const db = await open();
    try {
        return await new Promise((resolve, reject) => {
            const transaction = db.transaction(storeName, mode);
            const request = work(transaction.objectStore(storeName));
            transaction.oncomplete = () => resolve(request?.result);
            transaction.onerror = () => reject(transaction.error);
        });
    } finally {
        db.close();
    }
}

export async function load(key) {
    return (await transact('readonly', store => store.get(key))) ?? null;
}

export async function save(key, value) {
    await transact('readwrite', store => store.put(value, key));
}

export async function forget() {
    await transact('readwrite', store => store.clear());
}

export function online() {
    return navigator.onLine;
}
