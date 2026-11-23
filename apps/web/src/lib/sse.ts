// ADR-0014: the run event stream over fetch, because EventSource cannot send the Authorization header.
import { API_BASE, ApiError, type Fetch } from './api.js';

export interface SseMessage { id?: string; event?: string; data: string }

export class SseParser {
  private buffer = '';
  private fields: { id?: string; event?: string; data: string[] } = { data: [] };

  push(chunk: string): SseMessage[] {
    this.buffer += chunk;
    const messages: SseMessage[] = [];
    let newline: number;
    while ((newline = this.buffer.search(/\r?\n/)) >= 0) {
      const line = this.buffer.slice(0, newline);
      this.buffer = this.buffer.slice(newline + (this.buffer[newline] === '\r' ? 2 : 1));
      if (line === '') {
        if (this.fields.data.length || this.fields.event || this.fields.id !== undefined) {
          messages.push({ id: this.fields.id, event: this.fields.event, data: this.fields.data.join('\n') });
        }
        this.fields = { data: [] };
      } else if (!line.startsWith(':')) {
        const colon = line.indexOf(':');
        const name = colon < 0 ? line : line.slice(0, colon);
        const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
        if (name === 'data') this.fields.data.push(value);
        else if (name === 'id') this.fields.id = value;
        else if (name === 'event') this.fields.event = value;
      }
    }
    return messages;
  }
}

export interface StreamOptions {
  runId: string;
  token: string;
  afterSequence?: number;
  fetchImpl?: Fetch;
  signal?: AbortSignal;
  sleep?: (ms: number) => Promise<void>;
  onEvent: (event: any) => void;
  // Called after a dropped stream is reopened: an action shown earlier may have changed, so re-read it.
  onReconnect?: () => void;
  onUnauthorized?: () => void;
}

export interface StreamEnd { status: string | null; lastSequence: number }

const wait = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

// Resolves with the `end` message. Rejects on 401, 404, or abort; any other failure is retried with backoff.
export async function streamRunEvents(options: StreamOptions): Promise<StreamEnd> {
  const { runId, token, signal, onEvent, onReconnect, onUnauthorized } = options;
  const fetchImpl = options.fetchImpl ?? ((url, init) => fetch(url, init));
  const sleep = options.sleep ?? wait;
  let lastId: number | undefined = options.afterSequence;
  let delay = 500;
  let attempts = 0;

  for (;;) {
    signal?.throwIfAborted();
    const headers: Record<string, string> = { Accept: 'text/event-stream', Authorization: `Bearer ${token}` };
    if (lastId !== undefined) headers['Last-Event-ID'] = String(lastId);
    try {
      const response = await fetchImpl(`${API_BASE}/runs/${encodeURIComponent(runId)}/stream`,
        { method: 'GET', headers, signal, credentials: 'omit', cache: 'no-store' });
      if (response.status === 401) {
        onUnauthorized?.();
        throw new ApiError(401, undefined);
      }
      if (response.status === 404 || response.status === 400) throw new ApiError(response.status, undefined);
      if (!response.ok || !response.body) throw new Error(`stream returned HTTP ${response.status}`);
      if (attempts++ > 0) onReconnect?.();
      delay = 500;

      const parser = new SseParser();
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        for (const message of parser.push(decoder.decode(value, { stream: true }))) {
          if (message.event === 'end') return JSON.parse(message.data) as StreamEnd;
          const sequence = Number(message.id);
          if (lastId !== undefined && sequence <= lastId) continue; // never deliver an event twice
          lastId = sequence;
          onEvent(JSON.parse(message.data));
        }
      }
    } catch (error) {
      if (error instanceof ApiError || signal?.aborted) throw error;
    }
    await sleep(delay);
    delay = Math.min(delay * 2, 10_000);
  }
}
