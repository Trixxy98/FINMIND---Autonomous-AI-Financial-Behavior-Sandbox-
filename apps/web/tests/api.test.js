import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { ApiError, createApi } from '../build-test/lib/api.js';

describe('api client', () => {
  it('sends the token only in the Authorization header', async () => {
    const calls = [];
    const api = createApi({
      token: () => 'tok-123',
      fetchImpl: async (url, init) => { calls.push({ url, init }); return new Response('{"runs":[]}', { status: 200 }); },
    });
    await api.listRuns({ status: 'RUNNING', limit: 10 });
    await api.cancelRun('r 1', 'stop now');
    assert.equal(calls[0].url, '/api/v1/runs?status=RUNNING&limit=10');
    assert.equal(calls[1].url, '/api/v1/runs/r%201/cancel');
    assert.deepEqual(JSON.parse(calls[1].init.body), { reason: 'stop now' });
    for (const call of calls) {
      assert.equal(call.init.headers.Authorization, 'Bearer tok-123');
      assert.ok(!call.url.includes('tok-123'));
      assert.equal(call.init.credentials, 'omit');
    }
  });

  it('reports errors with the API reason code, and 401 to the caller', async () => {
    let unauthorized = 0;
    const api = createApi({
      token: () => 't',
      onUnauthorized: () => { unauthorized += 1; },
      fetchImpl: async (url) => url.endsWith('/models')
        ? new Response('{"error":"Researcher credential required."}', { status: 401 })
        : new Response('{"status":"malformed","reasonCode":"INVALID_RUN_CONFIG","fields":["seed"]}', { status: 400 }),
    });
    await assert.rejects(api.listModels(), (e) => e instanceof ApiError && e.status === 401);
    assert.equal(unauthorized, 1);
    await assert.rejects(api.createRun({}), (e) => e.status === 400 && e.reasonCode === 'INVALID_RUN_CONFIG' && e.fields[0] === 'seed');
  });
});

// ADR-0014: the token lives in memory only.
describe('token handling', () => {
  it('no source file uses browser storage or cookies', async () => {
    const files = await readdir(new URL('../src', import.meta.url), { recursive: true });
    for (const file of files.filter((f) => /\.(ts|tsx)$/.test(f))) {
      const text = await readFile(new URL(`../src/${file}`, import.meta.url), 'utf8');
      assert.doesNotMatch(text, /localStorage|sessionStorage|document\.cookie|indexedDB/, file);
    }
  });
});

describe('operator switch client (ADR-0017)', () => {
  it('reads the status and sends stop and resume with a reason', async () => {
    const calls = [];
    const api = createApi({
      token: () => 'tok-123',
      fetchImpl: async (url, init) => { calls.push({ url, init }); return new Response('{"stopped":false}', { status: 200 }); },
    });
    await api.operatorStatus();
    await api.stopAll('drill');
    await api.resumeAll('drill over');
    assert.deepEqual(calls.map((c) => [c.init.method, c.url]),
      [['GET', '/api/v1/admin/status'], ['POST', '/api/v1/admin/stop'], ['POST', '/api/v1/admin/resume']]);
    assert.deepEqual(JSON.parse(calls[1].init.body), { reason: 'drill' });
    assert.deepEqual(JSON.parse(calls[2].init.body), { reason: 'drill over' });
  });
});
