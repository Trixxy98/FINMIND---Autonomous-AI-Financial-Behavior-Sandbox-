import { useEffect, useState, type ReactNode } from 'react';
import { ApiError, type Api } from '../lib/api.js';
import { batchRequest } from '../lib/analysis.js';
import { formatSen } from '../lib/money.js';
import {
  buildConfig, defaultForm, effortAfterModelChange, effortsFor, formatMicroUsd, isPaid, modelLabel, modelNotices, parseUsdToMicro, validateForm,
  type Model, type RunForm, type Scenario,
} from '../lib/runForm.js';

interface Props {
  api: Api;
  // ADR-0015: the same form creates a batch of replications.
  batch?: boolean;
  copied?: { form: RunForm; warnings: string[] };
  onCreated: (runId: string) => void;
  onCancel: () => void;
}

const FIELD_NAMES: Record<string, string> = {
  objective: 'Objective', scenarioId: 'Scenario', 'agent.modelId': 'Model', seed: 'Seed', startingBalanceSen: 'Starting balance',
  transactionLimitSen: 'Transaction limit', durationDays: 'Duration', maxActions: 'Action budget', maxModelTokens: 'Token budget',
  maxWallTimeSeconds: 'Wall-time budget', maxCostMicroUsd: 'Cost budget', 'agent.settings.effort': 'Effort',
  'agent.settings.maxOutputTokensPerTurn': 'Output tokens per turn',
};

export function NewRunScreen({ api, batch, copied, onCreated, onCancel }: Props) {
  const [scenarios, setScenarios] = useState<Scenario[]>([]);
  const [models, setModels] = useState<Model[]>([]);
  const [form, setForm] = useState<RunForm | null>(copied?.form ?? null);
  const [step, setStep] = useState<'edit' | 'review'>('edit');
  const [errors, setErrors] = useState<string[]>([]);
  const [message, setMessage] = useState('');
  const [confirmedCost, setConfirmedCost] = useState(false);
  const [busy, setBusy] = useState(false);
  const [count, setCount] = useState('3');
  const [seeds, setSeeds] = useState('');
  const [label, setLabel] = useState('');

  useEffect(() => {
    void (async () => {
      try {
        const [scenarioList, modelList] = await Promise.all([api.listScenarios(), api.listModels()]);
        const latest = (scenarioList.scenarios as Scenario[]).filter((s) => s.latest);
        setScenarios(latest);
        setModels(modelList.models);
        // The plain mock agent by default: no provider call, and not the adversarial or chaotic test agents.
        const preferred = modelList.models.find((m: Model) => m.modelId === 'finmind-mock-v1')
          ?? modelList.models.find((m: Model) => m.provider === 'mock');
        setForm((current) => current ?? defaultForm(latest[0], preferred));
      } catch (e) {
        setMessage(e instanceof Error ? e.message : 'Could not load the catalogs.');
      }
    })();
  }, [api]);

  if (!form) return <section><h2>New run</h2><p>{message || 'Loading…'}</p></section>;
  const paid = isPaid(form.provider);
  const model = models.find((m) => m.provider === form.provider && m.modelId === form.modelId);
  const notices = modelNotices(model);
  const set = (patch: Partial<RunForm>) => { setForm({ ...form, ...patch }); setErrors([]); };
  const invalid = (field: string) => errors.includes(field);

  function review() {
    const found = validateForm(form!, model);
    const batchError = batch ? batchRequest({}, count, seeds, label, false).error : undefined;
    setErrors(found);
    setMessage(found.length ? 'Correct the highlighted fields.' : batchError ?? '');
    if (batchError) return;
    if (!found.length) { setStep('review'); setConfirmedCost(false); }
  }

  async function create(start: boolean) {
    setBusy(true);
    setMessage('');
    try {
      if (batch) {
        const created = await api.createBatch(batchRequest(buildConfig(form!), count, seeds, label, start).body);
        onCreated(created.batchId);
        return;
      }
      const run = await api.createRun(buildConfig(form!));
      if (start) await api.startRun(run.runId);
      onCreated(run.runId);
    } catch (e) {
      if (e instanceof ApiError && e.fields.length) {
        setErrors(e.fields);
        setStep('edit');
        setMessage('The server rejected these fields.');
      } else {
        setMessage(e instanceof Error ? e.message : 'The run was not created.');
      }
    } finally {
      setBusy(false);
    }
  }

  const row = (label: string, field: string, input: ReactNode, hint?: string) => (
    <tr>
      <th style={{ textAlign: 'left', fontWeight: 500, paddingRight: 12, color: invalid(field) ? '#a50e0e' : undefined }}>
        <label htmlFor={field}>{label}</label>
      </th>
      <td>{input}{hint && <div style={{ fontSize: 12, color: '#5f6368' }}>{hint}</div>}
        {invalid(field) && <div role="alert" style={{ fontSize: 12, color: '#a50e0e' }}>Check this value.</div>}</td>
    </tr>
  );
  const text = (field: string, key: keyof RunForm, width = 160) => (
    <input id={field} aria-invalid={invalid(field)} style={{ width }} value={String(form[key])}
      onChange={(e) => set({ [key]: e.target.value } as Partial<RunForm>)} />
  );

  if (step === 'review') {
    const config = buildConfig(form);
    return (
      <section>
        <h2>{batch ? 'Review new batch' : 'Review new run'}</h2>
        {batch && <p><strong>{count} runs</strong>{seeds.trim() ? ` with seeds ${seeds}` : ', same configuration and seed (replications)'}
          {label.trim() ? `, labelled "${label.trim()}"` : ''}. They start in a recorded random order.</p>}
        <table style={{ fontSize: 14 }}>
          <tbody>
            {[
              ['Scenario', form.scenarioId],
              ['Model', `${form.provider} / ${form.modelId}${paid ? ` (paid, effort ${form.effort})` : ''}`],
              ['Objective', form.objective],
              ['Starting balance', formatSen(config.startingBalanceSen as number)],
              ['Transaction limit', formatSen(config.transactionLimitSen as number)],
              ['Duration', `${form.durationDays} virtual days`],
              ['Budgets', `${form.maxActions} actions, ${form.maxModelTokens} model tokens, ${form.maxWallTimeSeconds} s wall time`],
              ['Seed', form.seed],
              ...(paid ? [['Cost budget', formatMicroUsd(parseUsdToMicro(form.maxCostUsd)!)]] : []),
            ].map(([k, v]) => <tr key={k}><th style={{ textAlign: 'left', paddingRight: 12 }}>{k}</th><td>{v}</td></tr>)}
          </tbody>
        </table>
        {notices.map((n) => <p key={n} role="note" style={{ background: '#fce8e6', padding: 8 }}>{n}</p>)}
        {paid && (
          <p style={{ background: '#fef7e0', padding: 8 }}>
            <label>
              <input type="checkbox" checked={confirmedCost} onChange={(e) => setConfirmedCost(e.target.checked)} />{' '}
              This run calls a paid model and spends real money. The cost budget is an estimate from FINMIND's price table
              and token counts, not a billing limit. A spend limit in the provider console is still needed.
            </label>
          </p>
        )}
        {message && <p role="alert" style={{ color: '#a50e0e' }}>{message}</p>}
        <p>
          <button type="button" onClick={() => setStep('edit')} disabled={busy}>Back</button>{' '}
          <button type="button" onClick={() => void create(false)} disabled={busy || (paid && !confirmedCost)}>Create as draft</button>{' '}
          <button type="button" onClick={() => void create(true)} disabled={busy || (paid && !confirmedCost)}>Create and start</button>
        </p>
      </section>
    );
  }

  return (
    <section>
      <h2>{batch ? 'New batch' : 'New run'}</h2>
      {batch && (
        <p>
          <label>Runs (1–20) <input style={{ width: 50 }} value={count} onChange={(e) => setCount(e.target.value)} /></label>{' '}
          <label>Seed list (optional) <input style={{ width: 160 }} placeholder="same seed for all" value={seeds}
            onChange={(e) => setSeeds(e.target.value)} /></label>{' '}
          <label>Label <input style={{ width: 200 }} value={label} onChange={(e) => setLabel(e.target.value)} /></label>
        </p>
      )}
      {copied?.warnings.map((w) => <p key={w} role="alert" style={{ background: '#fef7e0', padding: 8 }}>{w}</p>)}
      <table style={{ fontSize: 14 }}>
        <tbody>
          {row('Scenario', 'scenarioId',
            <select id="scenarioId" value={form.scenarioId} onChange={(e) => {
              const scenario = scenarios.find((s) => s.id === e.target.value)!;
              set({ scenarioId: scenario.id, objective: scenario.recommendedObjective ?? form.objective });
            }}>
              {scenarios.map((s) => <option key={s.id} value={s.id}>{s.versionLabel}{s.title ? ` — ${s.title}` : ''}</option>)}
            </select>)}
          {row('Model', 'agent.modelId',
            <select id="agent.modelId" value={`${form.provider}|${form.modelId}`} onChange={(e) => {
              const [provider, modelId] = e.target.value.split('|');
              const chosen = models.find((m) => m.provider === provider && m.modelId === modelId);
              set({ provider, modelId, effort: effortAfterModelChange(form.effort, chosen),
                maxModelTokens: isPaid(provider) ? (form.maxModelTokens === '0' ? '300000' : form.maxModelTokens) : '0' });
            }}>
              {models.map((m) => <option key={`${m.provider}|${m.modelId}`} value={`${m.provider}|${m.modelId}`}>{modelLabel(m)}</option>)}
            </select>, paid ? 'Paid: a cost budget and a confirmation are required.' : 'Deterministic mock agent: no provider call.')}
          {notices.length > 0 && (
            <tr><td /><td>{notices.map((n) => <p key={n} role="note" style={{ background: '#fce8e6', padding: 8, margin: '4px 0', maxWidth: 560 }}>{n}</p>)}</td></tr>
          )}
          {row('Objective', 'objective',
            <textarea id="objective" aria-invalid={invalid('objective')} rows={3} cols={60} value={form.objective}
              onChange={(e) => set({ objective: e.target.value })} />)}
          {row('Starting balance (MYR)', 'startingBalanceSen', text('startingBalanceSen', 'startingBalance'), 'Funded through an opening journal.')}
          {row('Transaction limit (MYR)', 'transactionLimitSen', text('transactionLimitSen', 'transactionLimit'), 'Largest single payment or transfer.')}
          {row('Duration (virtual days)', 'durationDays', text('durationDays', 'durationDays', 80))}
          {row('Action budget', 'maxActions', text('maxActions', 'maxActions', 80))}
          {row('Model token budget', 'maxModelTokens', text('maxModelTokens', 'maxModelTokens', 120), paid ? undefined : 'Zero for the mock agent.')}
          {row('Wall-time budget (seconds)', 'maxWallTimeSeconds', text('maxWallTimeSeconds', 'maxWallTimeSeconds', 80))}
          {row('Seed', 'seed', text('seed', 'seed', 120), 'Same seed and configuration give the same scenario.')}
          {paid && row('Effort', 'agent.settings.effort',
            <select id="agent.settings.effort" value={form.effort} onChange={(e) => set({ effort: e.target.value })}>
              {effortsFor(model).map((x) => <option key={x}>{x}</option>)}
            </select>)}
          {paid && row('Output tokens per turn', 'agent.settings.maxOutputTokensPerTurn',
            text('agent.settings.maxOutputTokensPerTurn', 'maxOutputTokens', 100), 'Optional, 1024 to 16000 (default 16000).')}
          {paid && row('Cost budget (US$)', 'maxCostMicroUsd', text('maxCostMicroUsd', 'maxCostUsd', 100),
            'An estimate, not a billing limit.')}
          <tr><th style={{ textAlign: 'left', fontWeight: 500 }}>Unavailable capabilities</th><td><label>
            <input type="checkbox" checked={form.describeUnavailableCapabilities}
              onChange={(e) => set({ describeUnavailableCapabilities: e.target.checked })} /> Describe them to the agent
          </label></td></tr>
        </tbody>
      </table>
      {message && <p role="alert" style={{ color: '#a50e0e' }}>{message}{errors.length ? ` (${errors.map((f) => FIELD_NAMES[f] ?? f).join(', ')})` : ''}</p>}
      <p><button type="button" onClick={onCancel}>Cancel</button>{' '}<button type="button" onClick={review}>Review</button></p>
    </section>
  );
}
