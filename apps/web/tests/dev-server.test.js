import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

// ADR-0013 item 14: the dev server listens on the loopback interface only.
const config = await readFile(new URL('../vite.config.ts', import.meta.url), 'utf8');

describe('dev server', () => {
  it('binds to 127.0.0.1 on a fixed port', () => {
    assert.match(config, /host:\s*'127\.0\.0\.1'/);
    assert.match(config, /strictPort:\s*true/);
    assert.doesNotMatch(config, /0\.0\.0\.0|host:\s*true/);
  });
  it('proxies /api to the local API so the browser keeps its own origin', () => {
    assert.match(config, /'\/api':\s*\{\s*target:\s*'http:\/\/127\.0\.0\.1:5080'/);
  });
});
