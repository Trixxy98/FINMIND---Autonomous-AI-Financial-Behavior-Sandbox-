import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { SseParser, streamRunEvents } from '../build-test/lib/sse.js';

const message = (id, type, data) => `id: ${id}\nevent: ${type}\ndata: ${JSON.stringify(data)}\n\n`;

describe('SseParser', () => {
  it('handles any chunking, comments, CRLF, and multi-line data', () => {
    const text = ': hello\n\n' + message(1, 'A', { n: 1 }) + 'id: 2\r\nevent: B\r\ndata: x\r\ndata: y\r\n\r\n' + 'event: end\ndata: {}\n\n';
    for (const size of [1, 2, 7, text.length]) {
      const parser = new SseParser();
      const out = [];
      for (let i = 0; i < text.length; i += size) out.push(...parser.push(text.slice(i, i + size)));
      assert.deepEqual(out, [
        { id: '1', event: 'A', data: '{"n":1}' },
        { id: '2', event: 'B', data: 'x\ny' },
        { id: undefined, event: 'end', data: '{}' },
      ], `chunk size ${size}`);
    }
  });
});

function body(text, dropAfter) {
  const bytes = new TextEncoder().encode(text);
  let pulls = 0;
  // Like a real connection: the data that arrived is read first, then the next read fails.
  return new ReadableStream({
    pull(controller) {
      pulls += 1;
      if (pulls === 1) controller.enqueue(dropAfter === undefined ? bytes : bytes.slice(0, dropAfter));
      else if (dropAfter === undefined) controller.close();
      else controller.error(new TypeError('network dropped'));
    },
  });
}

describe('streamRunEvents', () => {
  it('resumes after a drop with Last-Event-ID, without gap or duplicate', async () => {
    const all = [1, 2, 3, 4].map((n) => message(n, 'E', { sequence: n })).join('');
    const firstPart = message(1, 'E', { sequence: 1 }) + message(2, 'E', { sequence: 2 }) + 'id: 3\nev';
    const requests = [];
    const fetchImpl = async (url, init) => {
      requests.push({ url, headers: init.headers });
      if (requests.length === 1) return new Response(body(all, firstPart.length), { status: 200 });
      const rest = message(3, 'E', { sequence: 3 }) + message(4, 'E', { sequence: 4 }) + 'event: end\ndata: {"status":"COMPLETED","lastSequence":4}\n\n';
      return new Response(body(rest), { status: 200 });
    };
    const seen = [];
    let reconnects = 0;
    const end = await streamRunEvents({
      runId: 'r1', token: 'secret-token', fetchImpl, sleep: async () => {},
      onEvent: (e) => seen.push(e.sequence), onReconnect: () => { reconnects += 1; },
    });
    assert.deepEqual(seen, [1, 2, 3, 4]);
    assert.deepEqual(end, { status: 'COMPLETED', lastSequence: 4 });
    assert.equal(reconnects, 1);
    assert.equal(requests[1].headers['Last-Event-ID'], '2');
    for (const r of requests) {
      assert.equal(r.headers.Authorization, 'Bearer secret-token');
      assert.ok(!r.url.includes('secret-token'));
      assert.equal(r.url, '/api/v1/runs/r1/stream');
    }
  });

  it('stops for good on 401 and on abort', async () => {
    let unauthorized = 0;
    await assert.rejects(streamRunEvents({
      runId: 'r1', token: 't', sleep: async () => {}, onEvent: () => {},
      fetchImpl: async () => new Response('{}', { status: 401 }), onUnauthorized: () => { unauthorized += 1; },
    }), /401/);
    assert.equal(unauthorized, 1);

    const controller = new AbortController();
    controller.abort();
    await assert.rejects(streamRunEvents({
      runId: 'r1', token: 't', signal: controller.signal, sleep: async () => {}, onEvent: () => {},
      fetchImpl: async (_, init) => { init.signal.throwIfAborted(); return new Response(''); },
    }), /abort/i);
  });
});
