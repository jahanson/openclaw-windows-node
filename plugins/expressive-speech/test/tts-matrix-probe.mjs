import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';

// Each case runs the real packaged plugin and an unrelated ordinary-chat
// control. The host owns classification, synthesis and summarization.
for (const mode of ['always', 'tagged', 'inbound', 'off']) {
  console.log(`Core TTS compatibility matrix: ${mode}`);
  const child = spawn(process.execPath, ['test/packed-host-probe.mjs'], {
    env: { ...process.env, OPENCLAW_PROBE_LONG_SPEECH: '1', OPENCLAW_PROBE_TTS_MODE: mode },
    stdio: 'inherit', windowsHide: true,
  });
  const [code] = await once(child, 'exit');
  assert.equal(code, 0, `The ${mode} core TTS compatibility case must pass.`);
}
