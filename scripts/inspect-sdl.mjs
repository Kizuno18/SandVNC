import { readFile, writeFile } from 'node:fs/promises';
const filename = process.argv[2];
if (!filename) throw new Error('Usage: node scripts/inspect-sdl.mjs <client-executable>');
const bytes = await readFile(filename);
const needles = ['SDL-2', 'SDL revision', 'SDL2', 'SDL_MOUSE_RELATIVE_MODE_WARP', 'SDL_JOYSTICK_RAWINPUT', 'SDL_WINDOWS_RAW_KEYBOARD', 'SDL_MOUSE_FOCUS_CLICKTHROUGH'];
const results = needles.map(needle => {
    const offset = bytes.indexOf(needle);
    return { needle, offset, value: offset < 0 ? null : bytes.subarray(offset, Math.min(bytes.indexOf(0, offset), offset + 120)).toString('ascii') };
});
const result = { file: filename, size: bytes.length, method: 'Exact byte searches; no executable modification or execution', results };
await writeFile(new URL('../evidence/client-sdl-strings.json', import.meta.url), JSON.stringify(result, null, 2) + '\n');
console.log(JSON.stringify(result));
