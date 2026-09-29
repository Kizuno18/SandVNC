import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
const runtime = JSON.parse(await readFile(new URL('../runtime/viewers.json', import.meta.url)));
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const observations = [];
const connections = [];
let debug = {};
async function connect(viewer) {
    const ws = new WebSocket(runtime.url.replace('http:', 'ws:') + '/input');
    let sequence = 0;
    const pending = new Map();
    let ready;
    const connected = new Promise(resolve => { ready = resolve; });
    ws.onopen = () => ws.send(viewer.token);
    ws.onmessage = event => {
        const message = JSON.parse(event.data);
        if (message.type === 'lease') ready();
        if (message.type === 'ack') { pending.get(message.sequence)?.(message.state); pending.delete(message.sequence); }
    };
    await Promise.race([connected, sleep(3000).then(() => { throw Error('Connection timeout'); })]);
    const headers = { Authorization: `Bearer ${viewer.token}` };
    const client = { viewer, ws, headers, send(type, a = 0, b = 0) {
        return new Promise((resolve, reject) => {
            const seq = ++sequence;
            const timer = setTimeout(() => { pending.delete(seq); reject(Error('ACK timeout')); }, 1500);
            pending.set(seq, value => { clearTimeout(timer); resolve(value); });
            ws.send(JSON.stringify({ type, a, b, sequence: seq }));
        });
    }, async status() { return (await fetch(runtime.url + '/status', { headers })).json(); },
    async frame(after) {
        for (let attempt = 0; attempt < 100; attempt++) {
            const response = await fetch(runtime.url + '/frame', { headers });
            if (response.ok) {
                const data = Buffer.from(await response.arrayBuffer());
                const epoch = Number(response.headers.get('X-Input-Epoch'));
                const sequence = Number(response.headers.get('X-Input-Sequence'));
                const number = Number(response.headers.get('X-Frame'));
                if (!after || (epoch === after.epoch && sequence >= after.sequence))
                    return { data, epoch, sequence, number, hash: createHash('sha256').update(data).digest('hex') };
            }
            await sleep(20);
        }
        throw Error('No frame captured after the acknowledged input');
    } };
    client.heartbeat = setInterval(() => { if (ws.readyState === WebSocket.OPEN) client.send('pulse').catch(() => {}); }, 400);
    connections.push(client);
    return client;
}
try {
    const [a, b] = await Promise.all(runtime.viewers.map(connect));
    const initial = await a.send('key', 87, 0);
    const baseline = await a.frame(initial);
    await fetch(runtime.url + '/diagnostic/publish-delay?ms=650', { method: 'POST', headers: a.headers });
    let pending;
    for (let i = 0; i < 50; i++) {
        pending = (await a.status()).pendingFrame;
        if (pending && pending.number > baseline.number) break;
        await sleep(20);
    }
    assert.ok(pending);
    const held = await Promise.all([a.send('key', 87, 1), b.send('key', 87, 1)]);
    await sleep(250);
    const oldFrame = await a.frame();
    assert.ok(oldFrame.sequence < held[0].sequence);
    assert.equal(oldFrame.hash, baseline.hash);
    assert.equal((await a.status()).state.keys, 1);
    await writeFile(new URL('../evidence/frame-before-ack.png', import.meta.url), oldFrame.data);
    const fresh = await a.frame(held[0]);
    await writeFile(new URL('../evidence/frame-after-ack.png', import.meta.url), fresh.data);
    assert.notEqual(fresh.hash, baseline.hash);
    observations.push({ test: 'A 250 ms sleep may still return a pre-input image', oldFrameSequence: oldFrame.sequence,
        inputSequence: held[0].sequence, freshFrameSequence: fresh.sequence, nativeKeysDuringOldFrame: 1, status: 'reproduced' });
    await fetch(runtime.url + '/diagnostic/publish-delay?ms=0', { method: 'POST', headers: a.headers });
    let repeats = 0;
    for (; repeats < 30; repeats++) {
        const reset = await Promise.all([a.send('key', 87, 0), b.send('key', 87, 0)]);
        const empty = await Promise.all([a.frame(reset[0]), b.frame(reset[1])]);
        debug = { iteration: repeats, reset, empty };
        const down = await Promise.all([a.send('key', 87, 1), b.send('key', 87, 1)]);
        const both = await Promise.all([a.frame(down[0]), b.frame(down[1])]);
        Object.assign(debug, { down, both });
        assert.notEqual(both[0].hash, empty[0].hash); assert.notEqual(both[1].hash, empty[1].hash);
        const up = await a.send('key', 87, 0);
        const end = await Promise.all([a.frame(up), b.frame(down[1])]);
        Object.assign(debug, { up, end });
        assert.equal(end[0].hash, empty[0].hash); assert.equal(end[1].hash, both[1].hash);
        assert.equal((await b.send('status')).keys, 1);
    }
    observations.push({ test: 'Frame-sequence synchronized independent holds', iterations: repeats, status: 'passed' });
    console.log(JSON.stringify(observations, null, 2));
} catch (error) {
    for (const stage of ['empty', 'both', 'end']) {
        if (!debug[stage]) continue;
        for (let i = 0; i < 2; i++) await writeFile(new URL(`../evidence/stress-${stage}-${i}.png`, import.meta.url), debug[stage][i].data);
        debug[stage] = debug[stage].map(({ data, ...metadata }) => metadata);
    }
    observations.push({ debug, current: await Promise.all(connections.map(client => client.status())) });
    observations.push({ status: 'failed', error: error.stack });
    console.error(error); process.exitCode = 1;
} finally {
    for (const client of connections) {
        await fetch(runtime.url + '/diagnostic/publish-delay?ms=0', { method: 'POST', headers: client.headers }).catch(() => {});
        clearInterval(client.heartbeat); client.ws.close();
    }
    await writeFile(new URL('../evidence/frame-order.json', import.meta.url), JSON.stringify({
        timestamp: new Date().toISOString(), mode: 'Synthetic Win32 processes; this does not establish the historical failure cause', observations
    }, null, 2) + '\n');
}
