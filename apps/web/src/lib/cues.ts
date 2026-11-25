// ADR-0016: recorded events to animation cues for the pixel-art view. Each cue names the one event it comes from.
// Coins move only for a ledger commit. Agent words appear only as speech labelled REPORTED BY AGENT.
import { formatSen } from './money.js';

export const CUES_VERSION = 'finmind-cues/v1';

export type Place = 'home' | 'mailbox' | 'counter' | 'vault' | 'wallet' | 'postbox' | 'desk' | 'treasury' | 'missing_door' | string;

export interface Cue {
  sequence: number;
  kind: 'day' | 'bill' | 'message' | 'walk' | 'coins' | 'blocked' | 'missing_door' | 'letter' | 'noop' | 'speech' | 'end';
  caption: string;
  evidence?: 'proposed' | 'denied' | 'committed' | 'reported_by_agent';
  toolCallId?: string;
  tool?: string;
  place?: Place;
  from?: Place;
  to?: Place;
  amountSen?: number;
  reversal?: boolean;
  reasonCode?: string;
  day?: number;
  status?: string;
  text?: string;
}

const TOOL_PLACES: Record<string, Place> = {
  pay_bill: 'counter', transfer_funds: 'counter', get_balance: 'counter', list_transactions: 'counter',
  move_to_savings: 'vault', list_obligations: 'mailbox', get_environment: 'mailbox', wait: 'home',
  submit_plan: 'desk', finish: 'desk', request_funding: 'postbox',
};

// A tool that does not exist has no place: the avatar goes to a door that is not there.
export const placeForTool = (tool: string): Place => TOOL_PLACES[tool] ?? 'missing_door';

const TERMINAL = new Set(['COMPLETED', 'FAILED', 'CANCELLED', 'BUDGET_EXCEEDED']);
const REJECTED = new Set(['denied', 'malformed', 'conflict']);

export function eventsToCues(events: readonly any[]): Cue[] {
  const places = new Map<string, Place>();
  const names = new Map<Place, string>([['wallet', 'cash'], ['vault', 'savings'], ['treasury', 'the simulator']]);
  const cues: Cue[] = [];

  for (const event of events) {
    const p = event.payload ?? {};
    const sequence = event.sequence;
    switch (event.eventType) {
      case 'ACCOUNT_CREATED': {
        const place: Place = p.kind === 'agent_cash' ? 'wallet' : p.kind === 'agent_savings' ? 'vault'
          : p.kind === 'simulator_treasury' ? 'treasury' : `merchant:${p.accountId}`;
        places.set(p.accountId, place);
        if (p.label) names.set(place, p.label);
        break;
      }
      case 'SCENARIO_MATERIALIZED': cues.push({ sequence, kind: 'day', day: 0, caption: 'Day 0: the run begins.' }); break;
      case 'CLOCK_ADVANCED': cues.push({ sequence, kind: 'day', day: p.toDay, caption: `Day ${p.toDay}.` }); break;
      case 'OBLIGATION_CREATED':
        cues.push({ sequence, kind: 'bill', amountSen: p.amountSen,
          caption: `A bill arrives: ${p.label ?? 'bill'}, ${formatSen(p.amountSen)}${p.dueDay != null ? `, due day ${p.dueDay}` : ''}.` });
        break;
      case 'SCENARIO_EVENT_DELIVERED':
        if (p.kind === 'message') cues.push({ sequence, kind: 'message', caption: `A message arrives (${p.eventKey}).` });
        break;
      case 'TOOL_CALL_PROPOSED':
        cues.push({ sequence, kind: 'walk', evidence: 'proposed', toolCallId: p.toolCallId, tool: p.tool, place: placeForTool(p.tool),
          caption: `The agent proposes ${p.tool}.` });
        break;
      case 'LEDGER_COMMITTED': {
        const out = (p.postings ?? []).find((x: any) => x.amountSen < 0);
        const into = (p.postings ?? []).find((x: any) => x.amountSen > 0);
        if (!out || !into) break;
        const from = places.get(out.accountId) ?? 'unknown', to = places.get(into.accountId) ?? 'unknown';
        const reversal = p.kind === 'reversal';
        cues.push({ sequence, kind: 'coins', evidence: 'committed', from, to, amountSen: into.amountSen, reversal,
          toolCallId: event.traceId ?? undefined,
          caption: `${reversal ? 'Reversed: ' : ''}${formatSen(into.amountSen)} moves from ${names.get(from) ?? from} to ${names.get(to) ?? to}.` });
        break;
      }
      case 'TOOL_RETURNED': {
        const base = { sequence, toolCallId: p.toolCallId, tool: p.tool };
        if (p.status === 'unsupported_capability') {
          cues.push({ ...base, kind: 'missing_door', evidence: 'denied', place: 'missing_door',
            caption: `${p.tool} does not exist. Nothing happens.` });
        } else if (REJECTED.has(p.status)) {
          cues.push({ ...base, kind: 'blocked', evidence: 'denied', place: placeForTool(p.tool), reasonCode: p.reasonCode ?? undefined,
            caption: `${p.tool} is refused${p.reasonCode ? ` (${p.reasonCode})` : ''}. Nothing moves.` });
        } else if (p.status === 'ok' && p.tool === 'request_funding') {
          cues.push({ ...base, kind: 'letter', evidence: 'reported_by_agent', place: 'postbox',
            caption: 'The agent asks for more funds. The request is recorded; nothing is credited.' });
        } else if (p.status === 'replayed') {
          cues.push({ ...base, kind: 'noop', evidence: 'proposed', caption: `${p.tool} was already done. Nothing new moves.` });
        } else if (p.status === 'internal_error') {
          cues.push({ ...base, kind: 'noop', evidence: 'proposed', caption: `${p.tool} hit a platform error. See the ledger for what happened.` });
        }
        break;
      }
      case 'MODEL_INVOCATION_COMPLETED':
        if (p.outputText) cues.push({ sequence, kind: 'speech', evidence: 'reported_by_agent', text: p.outputText, toolCallId: event.traceId ?? undefined, caption: 'The agent says:' });
        break;
      case 'AGENT_PLAN_SUBMITTED':
        if (p.text) cues.push({ sequence, kind: 'speech', evidence: 'reported_by_agent', text: p.text, toolCallId: event.traceId ?? undefined, caption: 'The agent submits a plan:' });
        break;
      case 'AGENT_FINAL_REPORT':
        if (p.summary) cues.push({ sequence, kind: 'speech', evidence: 'reported_by_agent', text: p.summary, toolCallId: event.traceId ?? undefined, caption: 'The agent reports:' });
        break;
      case 'RUN_STATUS_CHANGED':
        if (TERMINAL.has(p.to)) cues.push({ sequence, kind: 'end', status: p.to, caption: `The run ends: ${p.to}.` });
        break;
      default: break; // events this view does not show
    }
  }
  return cues;
}
