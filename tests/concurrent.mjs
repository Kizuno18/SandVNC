import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const runtime = JSON.parse(await readFile(new URL('../runtime/viewers.json', import.meta.url)));
const results = [];
const sockets = [];
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
function record(name, evidence) { results.push({ name, status: 'passed', evidence }); console.log(`PASS ${name}`); }
async function connect(viewer) {
    const ws = new WebSocket(runtime.url.replace('http:', 'ws:') + '/input');
    sockets.push(ws);
    let sequence = 0;
    const pending = new Map();
    let resolveLease, rejectLease;
    const lease = new Promise((resolve, reject) => { resolveLease = resolve; rejectLease = reject; });
    const timeout = setTimeout(() => rejectLease(Error('Lease timeout')), 4000);
    ws.onopen = () => ws.send(viewer.token);
    ws.onerror = () => rejectLease(Error('WebSocket error'));
    ws.onmessage = event => {
        const data = JSON.parse(event.data);
        if (data.type === 'lease') { clearTimeout(timeout); resolveLease(data); }
        else if (data.type === 'ack') { pending.get(data.sequence)?.(data); pending.delete(data.sequence); }
    };
    const hello = await lease;
    return { ws, hello, viewer, send(type, a = 0, b = 0, frame = 0) {
        return new Promise((resolve, reject) => {
            const seq = ++sequence;
            const timeout = setTimeout(() => { pending.delete(seq); reject(Error(`${viewer.name} ${type} ACK timeout`)); }, 1500);
            pending.set(seq, value => { clearTimeout(timeout); resolve(value); });
            ws.send(JSON.stringify({ type, sequence: seq, a, b, frame }));
        });
    } };
}
async function frame(viewer, filename, after) {
    let response;
    for (let i = 0; i < 30; i++) {
        response = await fetch(runtime.url + '/frame', { headers: { Authorization: `Bearer ${viewer.token}` } });
        if (response.status !== 503) {
            if (!after || (Number(response.headers.get('X-Input-Epoch')) === after.epoch &&
                Number(response.headers.get('X-Input-Sequence')) >= after.sequence)) break;
            await response.arrayBuffer();
        }
        await sleep(100);
    }
    assert.equal(response.status, 200);
    if (after) {
        assert.equal(Number(response.headers.get('X-Input-Epoch')), after.epoch);
        assert.ok(Number(response.headers.get('X-Input-Sequence')) >= after.sequence, 'No frame after acknowledged input');
    }
    const bytes = Buffer.from(await response.arrayBuffer());
    assert.deepEqual([...bytes.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
    if (filename) await writeFile(new URL('../evidence/' + filename, import.meta.url), bytes);
    return { number: Number(response.headers.get('X-Frame')), hash: createHash('sha256').update(bytes).digest('hex') };
}
try {
    const [a, b] = await Promise.all(runtime.viewers.map(connect));
    assert.notEqual(a.hello.pid, b.hello.pid);
    record('Two distinct native processes with simultaneous control leases', { pids: [a.hello.pid, b.hello.pid] });
    await sleep(250);
    const base = await Promise.all(runtime.viewers.map(v => frame(v)));
    const initialApplied = (await a.send('status')).state.applied;
    await Promise.all(Array.from({ length: 200 }, (_, i) => a.send('key', 87, i % 2 === 0 ? 1 : 0)));
    let drained;
    for (let i = 0; i < 30; i++) {
        drained = (await a.send('status')).state;
        if (drained.applied >= initialApplied + 200) break;
        await sleep(20);
    }
    assert.ok(drained.applied >= initialApplied + 200);
    assert.equal((await frame(a.viewer, undefined, drained)).hash, base[0].hash);
    record('Rapid down/up transitions preserve message-time keyboard state', { transitions: 200, renderedErrors: 0 });
    const held = await Promise.all([a.send('key', 87, 1), b.send('key', 87, 1)]);
    assert.equal(held[0].state.keys, 1); assert.equal(held[1].state.keys, 1);
    const both = await Promise.all([frame(a.viewer, 'a-held.png', held[0].state), frame(b.viewer, 'b-held.png', held[1].state)]);
    assert.notEqual(both[0].hash, base[0].hash); assert.notEqual(both[1].hash, base[1].hash);
    const up = await a.send('key', 87, 0);
    const stateB = await b.send('status');
    assert.equal(stateB.state.keys, 1);
    const released = await Promise.all([frame(a.viewer, 'a-released.png', up.state), frame(b.viewer, 'b-still-held.png', stateB.state)]);
    assert.notEqual(released[0].hash, both[0].hash); assert.equal(released[1].hash, both[1].hash);
    record('Concurrent W hold; A release preserves B state and B rendered pixels', { bKeys: stateB.state.keys, bFrameUnchanged: true });
    await b.send('key', 87, 0);
    await a.send('key', 17, 1);
    await b.send('text', 98);
    const geometry = await Promise.all(runtime.viewers.map(v => frame(v)));
    await Promise.all([a.send('move', 100, 120, geometry[0].number), b.send('move', 250, 160, geometry[1].number)]);
    await a.send('button', 0, 1, geometry[0].number);
    const independent = await b.send('button', 0, 1, geometry[1].number);
    assert.equal(independent.state.modifiers, 0);
    await b.send('button', 0, 0, geometry[1].number);
    await b.send('move', 270, 180, geometry[1].number);
    const drag = await a.send('status');
    assert.equal(drag.state.modifiers, 2); assert.equal(drag.state.x, 100); assert.equal(drag.state.y, 120);
    assert.equal(drag.state.keys, 2);
    await sleep(250);
    await Promise.all([frame(a.viewer, 'a-drag.png'), frame(b.viewer, 'b-type-click.png')]);
    record('A Ctrl + drag while B types, clicks and moves without Ctrl', { a: drag.state, b: independent.state });
    const delayResponse = await fetch(runtime.url + '/diagnostic/capture-delay?ms=4000', {
        method: 'POST', headers: { Authorization: `Bearer ${a.viewer.token}` } });
    assert.equal(delayResponse.status, 200);
    await sleep(300);
    const frozenFrame = await frame(a.viewer);
    await sleep(300);
    assert.equal((await frame(a.viewer)).number, frozenFrame.number);
    const timings = [];
    for (let i = 0; i < 10; i++) {
        const start = performance.now();
        await Promise.all([a.send('status'), b.send('key', 87, i % 2)]);
        timings.push(performance.now() - start);
    }
    assert.ok(Math.max(...timings) < 1500);
    record('A capture delayed 4 seconds while both input channels continue', { frozenFrame: frozenFrame.number, roundTripMs: timings });
    const replacement = await connect(a.viewer);
    const reset = await replacement.send('status');
    assert.equal(reset.state.keys, 0); assert.equal(replacement.hello.pid, a.hello.pid);
    assert.ok(replacement.hello.epoch > a.hello.epoch);
    await sleep(100);
    assert.notEqual(a.ws.readyState, WebSocket.OPEN);
    record('Reconnect retains process, resets input and revokes old connection', { oldEpoch: a.hello.epoch, newEpoch: replacement.hello.epoch, pid: replacement.hello.pid });
    replacement.ws.close();
    await b.send('pulse');
    await sleep(100);
    const again = await connect(a.viewer);
    assert.equal((await again.send('status')).state.keys, 0);
    record('Disconnect does not terminate the target', { pid: again.hello.pid });
    const unauthorized = await fetch(runtime.url + '/frame', { headers: { Authorization: `Bearer ${'0'.repeat(64)}` } });
    assert.equal(unauthorized.status, 401);
    const forbidden = await fetch(runtime.url + '/status', { headers: { Authorization: `Bearer ${b.viewer.token}`, Origin: 'https://example.org' } });
    assert.equal(forbidden.status, 403);
    record('Unauthorized capture and cross-origin requests rejected', { unauthorized: unauthorized.status, crossOrigin: forbidden.status });
    again.ws.send(JSON.stringify({ type: 'key', sequence: 1, a: 87, b: 1 }));
    await sleep(200);
    assert.notEqual(again.ws.readyState, WebSocket.OPEN);
    assert.equal((await b.send('status')).state.modifiers, 0);
    record('Duplicate sequence closes only the offending controller', {});
    await fetch(runtime.url + '/diagnostic/capture-delay?ms=0', { method: 'POST', headers: { Authorization: `Bearer ${a.viewer.token}` } });
    await b.send('key', 87, 1);
    await sleep(2300);
    const metrics = await (await fetch(runtime.url + '/status', { headers: { Authorization: `Bearer ${b.viewer.token}` } })).json();
    assert.equal(metrics.state.keys, 0);
    assert.notEqual(b.ws.readyState, WebSocket.OPEN);
    record('Missing viewer heartbeat releases keys and closes its lease', { keys: metrics.state.keys });
    const renewed = await Promise.all(runtime.viewers.map(connect));
    await sleep(300);
    const physicalBase = await frame(renewed[1].viewer);
    execFileSync(fileURLToPath(new URL('../out/SandVnc.exe', import.meta.url)), ['--post-probe-messages', String(renewed[1].hello.pid)]);
    await sleep(300);
    assert.equal((await frame(renewed[1].viewer)).hash, physicalBase.hash);
    record('Nonremote keyboard, character and mouse messages are filtered in the native probe', { imageUnchanged: true, physicalHardwareTest: false });
    const oldGeometry = await frame(renewed[1].viewer);
    execFileSync(fileURLToPath(new URL('../out/SandVnc.exe', import.meta.url)), ['--resize-probe', String(renewed[1].hello.pid)]);
    await sleep(300);
    const newGeometry = await frame(renewed[1].viewer);
    assert.ok(newGeometry.number > oldGeometry.number);
    renewed[1].ws.send(JSON.stringify({ type: 'move', sequence: 1, a: 100, b: 100, frame: oldGeometry.number }));
    await sleep(150);
    assert.notEqual(renewed[1].ws.readyState, WebSocket.OPEN);
    renewed[1] = await connect(renewed[1].viewer);
    assert.equal((await renewed[1].send('move', 100, 100, newGeometry.number)).state.x, 100);
    record('Resize rejects old frame geometry and permits a fresh binding', { oldFrame: oldGeometry.number, newFrame: newGeometry.number });
    execFileSync(fileURLToPath(new URL('../out/SandVnc.exe', import.meta.url)), ['--recreate-probe', String(renewed[0].hello.pid)]);
    await sleep(500);
    const retired = await fetch(runtime.url + '/frame', { headers: { Authorization: `Bearer ${renewed[0].viewer.token}` } });
    assert.equal(retired.status, 503);
    assert.equal((await renewed[1].send('key', 87, 1)).state.keys, 1);
    assert.notEqual(renewed[0].ws.readyState, WebSocket.OPEN);
    record('Destroy/recreate A window retires its session while B input continues', { aCapture: retired.status });
} catch (error) {
    results.push({ name: 'Test execution', status: 'failed', evidence: error.stack });
    console.error(error); process.exitCode = 1;
} finally {
    for (const socket of sockets) socket.close();
    await mkdir(new URL('../evidence/', import.meta.url), { recursive: true });
    await writeFile(new URL('../evidence/concurrent-results.json', import.meta.url), JSON.stringify({
        timestamp: new Date().toISOString(), mode: 'synthetic native processes, NOT Sandboxie/PXG acceptance', results
    }, null, 2) + '\n');
}
