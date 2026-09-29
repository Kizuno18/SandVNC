import { readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { performance } from 'node:perf_hooks';
const runtime = JSON.parse(await readFile(new URL('../runtime/viewers.json', import.meta.url)));
const viewer = runtime.viewers[0];
const socket = new WebSocket(runtime.url.replace('http:', 'ws:') + '/input');
let sequence = 0;
const pending = new Map();
const ready = new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(Error('Lease timeout')), 3000);
    socket.onopen = () => socket.send(viewer.token);
    socket.onmessage = event => {
        const message = JSON.parse(event.data);
        if (message.type === 'lease') { clearTimeout(timeout); resolve(); }
        if (message.type === 'ack') { pending.get(message.sequence)?.(); pending.delete(message.sequence); }
    };
});
function send(type, a = 0, b = 0) {
    return new Promise((resolve, reject) => {
        const seq = ++sequence;
        const timeout = setTimeout(() => reject(Error('ACK timeout')), 1500);
        pending.set(seq, () => { clearTimeout(timeout); resolve(); });
        socket.send(JSON.stringify({ type, a, b, sequence: seq }));
    });
}
async function image() {
    let response;
    for (let i = 0; i < 30; i++) {
        response = await fetch(runtime.url + '/frame', { headers: { Authorization: 'Bearer ' + viewer.token } });
        if (response.status !== 503) break;
        await sleep(100);
    }
    if (!response.ok) throw Error(`Frame ${response.status}`);
    return createHash('sha256').update(Buffer.from(await response.arrayBuffer())).digest('hex');
}
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const trials = [];
try {
    await ready;
    await send('key', 87, 0);
    await sleep(300);
    for (let i = 0; i < 10; i++) {
        const before = await image();
        const start = performance.now();
        await send('key', 87, i % 2 === 0 ? 1 : 0);
        const ackMs = performance.now() - start;
        while (await image() === before) {
            if (performance.now() - start > 1500) throw Error('No visual response');
            await sleep(5);
        }
        trials.push({ ackMs, changedPngReceivedMs: performance.now() - start });
        await sleep(20);
    }
    await writeFile(new URL('../evidence/visual-timing.json', import.meta.url), JSON.stringify({ timestamp: new Date().toISOString(),
        mode: 'Synthetic native probe only; input-to-changed-PNG at HTTP client, excludes browser decode/display and network WAN', trials
    }, null, 2) + '\n');
    console.log(JSON.stringify(trials));
} finally { socket.close(); }
