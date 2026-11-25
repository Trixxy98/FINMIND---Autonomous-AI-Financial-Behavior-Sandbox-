// ADR-0014: the researcher API client. The token is read from memory for each request and sent only in the
// Authorization header: never in a URL, a cookie, or storage.
export const API_BASE = '/api/v1';

export class ApiError extends Error {
  constructor(readonly status: number, readonly reasonCode: string | undefined, readonly fields: string[] = [], message?: string) {
    super(message ?? `API returned HTTP ${status}${reasonCode ? ` (${reasonCode})` : ''}`);
  }
}

export type Fetch = (url: string, init: RequestInit & { headers: Record<string, string> }) => Promise<Response>;

export interface ApiOptions {
  token: () => string | null;
  fetchImpl?: Fetch;
  onUnauthorized?: () => void;
}

export interface RunFilters { status?: string; kind?: 'managed' | 'legacy'; limit?: number; before?: string }

const id = (value: string) => encodeURIComponent(value);

function query(values: Record<string, string | number | undefined>): string {
  const parts = Object.entries(values).filter(([, v]) => v !== undefined && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`);
  return parts.length ? `?${parts.join('&')}` : '';
}

export function createApi({ token, fetchImpl = (url, init) => fetch(url, init), onUnauthorized }: ApiOptions) {
  async function request<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = { Accept: 'application/json', Authorization: `Bearer ${token() ?? ''}` };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetchImpl(API_BASE + path, {
      method, headers, credentials: 'omit', cache: 'no-store',
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    const parsed = text ? JSON.parse(text) : null;
    if (response.status === 401) onUnauthorized?.();
    if (!response.ok) {
      throw new ApiError(response.status, parsed?.reasonCode, parsed?.fields ?? [], parsed?.message ?? parsed?.error);
    }
    return parsed as T;
  }

  return {
    listRuns: (filters: RunFilters = {}) => request<any>('GET', `/runs${query({ ...filters })}`),
    getRun: (runId: string) => request<any>('GET', `/runs/${id(runId)}`),
    wallets: (runId: string) => request<any>('GET', `/runs/${id(runId)}/wallets`),
    ledger: (runId: string, afterSequence = 0, limit?: number) =>
      request<any>('GET', `/runs/${id(runId)}/ledger${query({ afterSequence, limit })}`),
    actions: (runId: string, afterSequence = 0, limit?: number) =>
      request<any>('GET', `/runs/${id(runId)}/actions${query({ afterSequence, limit })}`),
    events: (runId: string, afterSequence = 0, limit?: number) =>
      request<any>('GET', `/runs/${id(runId)}/events${query({ afterSequence, limit })}`),
    verify: (runId: string) => request<any>('GET', `/runs/${id(runId)}/verify`),
    createRun: (config: unknown) => request<any>('POST', '/runs', config),
    startRun: (runId: string) => request<any>('POST', `/runs/${id(runId)}/start`),
    cancelRun: (runId: string, reason: string) => request<any>('POST', `/runs/${id(runId)}/cancel`, { reason }),
    // ADR-0017: the operator switch stops every run and refuses starts until resumed.
    operatorStatus: () => request<any>('GET', '/admin/status'),
    stopAll: (reason: string) => request<any>('POST', '/admin/stop', { reason }),
    resumeAll: (reason: string) => request<any>('POST', '/admin/resume', { reason }),
    metrics: (runId: string) => request<any>('GET', `/runs/${id(runId)}/metrics`),
    classifications: (runId: string) => request<any>('GET', `/runs/${id(runId)}/classifications`),
    review: (runId: string, key: string, label: string, note?: string) =>
      request<any>('POST', `/runs/${id(runId)}/reviews`, note ? { key, label, note } : { key, label }),
    pendingReviews: (limit = 50) => request<any>('GET', `/reviews/pending${query({ limit })}`),
    listBatches: (limit = 100) => request<any>('GET', `/batches${query({ limit })}`),
    getBatch: (batchId: string) => request<any>('GET', `/batches/${id(batchId)}`),
    createBatch: (body: unknown) => request<any>('POST', '/batches', body),
    compare: (batchIds: string[]) => request<any>('GET', `/compare?${batchIds.map((b) => `batch=${encodeURIComponent(b)}`).join('&')}`),
    // The file is fetched with the Authorization header and handed to the browser as a blob: no token in a URL.
    exportRun: async (runId: string, format: 'jsonl' | 'csv') => {
      const response = await fetchImpl(`${API_BASE}/runs/${id(runId)}/export?format=${format}`, {
        method: 'GET', credentials: 'omit', cache: 'no-store', headers: { Authorization: `Bearer ${token() ?? ''}` },
      });
      if (response.status === 401) onUnauthorized?.();
      if (!response.ok) throw new ApiError(response.status, undefined);
      return response.blob();
    },
    listScenarios: () => request<any>('GET', '/scenarios'),
    listModels: () => request<any>('GET', '/models'),
  };
}

export type Api = ReturnType<typeof createApi>;
