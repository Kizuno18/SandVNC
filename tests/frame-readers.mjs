import { readFile, writeFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
const runtime = JSON.parse(await readFile(new URL('../runtime/viewers.json', import.meta.url)));
const duration = Number(process.argv[2] ?? 8) * 1000;
const start = performance.now();
const readers = await Promise.all(runtime.viewers.map(async viewer => {
    let bytes = 0, frames = 0, unavailable = 0;
    while (performance.now() - start < duration) {
        const cycle = performance.now();
        const response = await fetch(runtime.url + '/frame', { headers: { Authorization: 'Bearer ' + viewer.token } });
        if (response.ok) { bytes += (await response.arrayBuffer()).byteLength; frames++; }
        else unavailable++;
        await new Promise(resolve => setTimeout(resolve, Math.max(1, 100 - (performance.now() - cycle))));
    }
    return { name: viewer.name, bytes, frames, unavailable };
}));
const elapsedSeconds = (performance.now() - start) / 1000;
await writeFile(new URL('../evidence/bandwidth.json', import.meta.url), JSON.stringify({
    mode: 'Two Node HTTP frame readers, not browser or PXG', elapsedSeconds, readers,
    totalMbps: readers.reduce((total, item) => total + item.bytes, 0) * 8 / elapsedSeconds / 1e6
}, null, 2) + '\n');
