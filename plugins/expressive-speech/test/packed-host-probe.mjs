import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const artifacts = join(root, 'artifacts');
await mkdir(artifacts, { recursive: true });
assert.ok(process.env.npm_execpath, 'Run through npm run probe:packed.');
const packed = spawnSync(process.execPath, [process.env.npm_execpath, 'pack', '--json', '--pack-destination', artifacts], { cwd: root, windowsHide: true, encoding: 'utf8' });
assert.equal(packed.status, 0, packed.stderr);
const info = JSON.parse(packed.stdout)[0];
assert.ok(info.files.some((file) => file.path === 'dist/index.js'), 'The artifact must contain compiled entrypoint code.');
assert.equal(info.files.some((file) => file.path.startsWith('test/') || file.path.startsWith('src/') || file.path.startsWith('node_modules/')), false, 'Fixtures, source-only entrypoints and host dependencies must not ship.');
const unpacked = await mkdtemp(join(tmpdir(), 'expressive-speech-packed-'));
execFileSync('tar', ['-xz'], { cwd: unpacked, input: await readFile(join(artifacts, info.filename)), windowsHide: true });
const manifest = JSON.parse(await readFile(join(unpacked, 'package', 'package.json'), 'utf8'));
assert.deepEqual(manifest.openclaw.extensions, ['./dist/index.js']);
const probe = spawnSync(process.execPath, [join(root, 'test', 'host-probe.mjs')], {
  cwd: root, windowsHide: true, stdio: 'inherit',
  env: { ...process.env, OPENCLAW_PROBE_ENTRY: process.env.OPENCLAW_PROBE_ENTRY ?? join(root, 'node_modules', 'openclaw', 'openclaw.mjs'), OPENCLAW_PROBE_PLUGIN: join(unpacked, 'package'), OPENCLAW_PROBE_PRODUCTION: '1', OPENCLAW_PROBE_TOOL: '1', OPENCLAW_PROBE_CONTROL: '', OPENCLAW_PROBE_ORCHESTRATOR: '' },
});
assert.equal(probe.status, 0, 'The compiled, unpacked production artifact must pass the actual-host probe.');
console.log(`Verified plugin artifact: ${join(artifacts, info.filename)} (${info.integrity})`);
