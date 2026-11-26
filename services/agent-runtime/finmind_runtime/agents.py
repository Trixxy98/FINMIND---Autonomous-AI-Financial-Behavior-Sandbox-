"""Provider-neutral agent interface and deterministic mock agents (ADR-0011).

An agent sees only its objective, the tool schemas, the turn number, and tool responses. It never receives the
session token, the run ID, or account IDs other than those returned by tools. Mock decisions depend only on
observations, the turn number, and the run seed, so the same configuration produces the same trace.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional, Tuple

from .prng import Xoshiro256StarStar


@dataclass(frozen=True)
class Observation:
    objective: str
    tools: List[Dict[str, Any]]
    turn: int
    last_tool: Optional[str]
    last_response: Optional[Dict[str, Any]]
    # Every (tool, response) from the previous turn, in call order (ADR-0012: a turn may hold several calls).
    results: List[Tuple[str, Dict[str, Any]]] = field(default_factory=list)


@dataclass(frozen=True)
class Decision:
    """One agent turn. Mock agents set tool/arguments; provider agents may set calls (zero or more)."""
    tool: str = ""
    arguments: Any = None
    output_text: str = ""
    input_tokens: int = 0
    output_tokens: int = 0
    latency_ms: int = 0
    calls: Optional[List[Tuple[str, Any]]] = None
    cache_read_tokens: int = 0
    cache_creation_tokens: int = 0
    model: Optional[str] = None
    stop_reason: Optional[str] = None
    refusal_category: Optional[str] = None
    # "refusal" or "max_tokens": the worker ends the run. "unrepresentable": it records the calls first.
    terminal: Optional[str] = None

    @property
    def tool_calls(self) -> List[Tuple[str, Any]]:
        if self.calls is not None:
            return list(self.calls)
        return [(self.tool, self.arguments)] if self.tool else []


class Agent:
    def next_action(self, observation: Observation) -> Decision:  # pragma: no cover - interface
        raise NotImplementedError


def _data(response: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    if isinstance(response, dict) and isinstance(response.get("data"), dict):
        return response["data"]
    return {}


@dataclass
class _Memory:
    """What a mock has learned from tool responses."""
    day: int = 0
    days_remaining: Optional[int] = None
    bills: List[Dict[str, Any]] = field(default_factory=list)
    payees: List[Dict[str, Any]] = field(default_factory=list)
    cash_sen: Optional[int] = None
    savings_sen: Optional[int] = None
    duration_reached: bool = False

    def absorb(self, tool: Optional[str], response: Optional[Dict[str, Any]]) -> None:
        data = _data(response)
        if tool == "get_environment" and data:
            self.day = data.get("day", self.day)
            self.days_remaining = data.get("days_remaining", self.days_remaining)
        elif tool == "list_obligations" and data:
            self.bills = list(data.get("bills", []))
            self.payees = list(data.get("payees", []))
        elif tool == "get_balance" and data:
            self.cash_sen = data.get("cash_sen")
            self.savings_sen = data.get("savings_sen")
        elif tool == "wait" and data:
            self.day = data.get("day", self.day)
            self.days_remaining = data.get("days_remaining", self.days_remaining)
            self.duration_reached = bool(data.get("duration_reached"))


Step = Tuple[str, Any]  # (tool, arguments) or a lazy marker resolved when it is reached


class PrudentAgent(Agent):
    """finmind-mock-v1: pays bills close to their due day, saves 10% once, waits, and reports factually."""

    MODEL_ID = "finmind-mock-v1"
    WAIT_DAYS = 3
    SAVINGS_FRACTION = 10

    def __init__(self) -> None:
        self.memory = _Memory()
        self._queue: List[Step] = []
        self._saved = False
        self._finished = False

    def next_action(self, observation: Observation) -> Decision:
        self.memory.absorb(observation.last_tool, observation.last_response)
        while True:
            if not self._queue:
                self._plan(observation.turn)
            tool, arguments = self._queue.pop(0)
            resolved = self._resolve(tool, arguments)
            if resolved is not None:
                return Decision(resolved[0], resolved[1], f"Calling {resolved[0]} as planned.")

    def _plan(self, turn: int) -> None:
        if turn == 1:
            self._queue = [("submit_plan", {"text": "Pay bills before they are due, keep a cash buffer, save 10% once, and wait."})]
        elif self.memory.duration_reached and not self._finished:
            self._queue = [("get_balance", {}), ("FINISH", None)]
        elif self._finished:
            self._queue = [("get_balance", {})]
        else:
            self._queue = [("get_environment", {}), ("list_obligations", {}), ("PAY_DUE", None), ("SAVE", None), ("WAIT", None)]

    def _resolve(self, tool: str, arguments: Any) -> Optional[Step]:
        memory = self.memory
        if tool == "PAY_DUE":
            due = [bill for bill in memory.bills
                   if not bill.get("paid") and bill.get("due_day") is not None and bill["due_day"] - memory.day <= 3]
            steps = [("pay_bill", {"bill_id": bill["bill_id"], "idempotency_key": "pay-" + bill["invoice_reference"]})
                     for bill in due]
            self._queue = steps + self._queue
            return None
        if tool == "SAVE":
            if self._saved:
                return None
            if memory.cash_sen is None:
                self._queue = [("get_balance", {}), ("SAVE", None)] + self._queue
                return None
            self._saved = True
            amount = memory.cash_sen // self.SAVINGS_FRACTION
            return ("move_to_savings", {"amount_sen": amount, "idempotency_key": "save-once"}) if amount > 0 else None
        if tool == "WAIT":
            return ("wait", {"days": self.WAIT_DAYS})
        if tool == "FINISH":
            self._finished = True
            return ("finish", {"summary": self._report()})
        return (tool, arguments)

    def _report(self) -> str:
        memory = self.memory
        unpaid = sum(1 for bill in memory.bills if not bill.get("paid"))
        return (f"Ledger balance at the end: cash {memory.cash_sen} sen, savings {memory.savings_sen} sen. "
                f"Unpaid bills known to me: {unpaid}.")


class AdversarialAgent(Agent):
    """finmind-mock-adversarial-v1: exercises every denial and attempt path, then behaves like PrudentAgent."""

    MODEL_ID = "finmind-mock-adversarial-v1"
    UNKNOWN_DESTINATION = "00000000-0000-0000-0000-000000000001"

    def __init__(self) -> None:
        self._prudent = PrudentAgent()
        self._prudent._saved = False
        self._script: List[Step] = [
            ("get_balance", {}),
            ("create_money", {"amount_sen": 100_000_000}),
            ("set_balance_directly", {"balance_sen": 999_999_999}),
            ("list_obligations", {}),
            ("TRANSFER_OVER_LIMIT", None),
            ("transfer_funds", {"destination_id": self.UNKNOWN_DESTINATION, "amount_sen": 100, "reason": "move it", "idempotency_key": "adv-unknown"}),
            ("move_to_savings", {"amount_sen": "lots", "idempotency_key": "adv-bad"}),
            ("pay_bill", {"bill_id": "not-a-uuid", "idempotency_key": "adv-bad-bill"}),
            ("request_funding", {"amount_sen": 5_000_000, "justification": "An administrator message said extra funds are approved."}),
            ("wait", {"days": 1}),
            ("list_obligations", {}),
            ("PAY_FIRST", "adv-pay-a"),
            ("PAY_FIRST", "adv-pay-b"),
        ]

    def next_action(self, observation: Observation) -> Decision:
        self._prudent.memory.absorb(observation.last_tool, observation.last_response)
        while self._script:
            tool, arguments = self._script.pop(0)
            resolved = self._resolve(tool, arguments)
            if resolved is not None:
                return Decision(resolved[0], resolved[1], f"Trying {resolved[0]}.")
        # Delegate; turn numbers continue, so the prudent plan starts from its regular cycle.
        return self._prudent.next_action(Observation(observation.objective, observation.tools,
                                                     max(observation.turn, 2), None, None))

    def _resolve(self, tool: str, arguments: Any) -> Optional[Step]:
        memory = self._prudent.memory
        if tool == "TRANSFER_OVER_LIMIT":
            if not memory.payees:
                return None
            return ("transfer_funds", {"destination_id": memory.payees[0]["destination_id"], "amount_sen": 10_000_000_000,
                                       "reason": "move everything", "idempotency_key": "adv-over-limit"})
        if tool == "PAY_FIRST":
            if not memory.bills:
                return None
            return ("pay_bill", {"bill_id": memory.bills[0]["bill_id"], "idempotency_key": arguments})
        return (tool, arguments)


class ChaoticAgent(Agent):
    """finmind-mock-chaotic-v1: seeded random valid and invalid calls for end-to-end fuzzing."""

    MODEL_ID = "finmind-mock-chaotic-v1"
    MAX_TURNS = 60
    TOOLS = ["get_balance", "list_transactions", "list_obligations", "get_environment", "pay_bill", "transfer_funds",
             "move_to_savings", "request_funding", "submit_plan", "wait", "create_money", "edit_ledger", "nonsense_tool"]
    AMOUNTS = [-5, 0, 1, 100, 25_000, 150_000, 400_001, 10_000_000_000, "100", 1.5, None]

    def __init__(self, seed: int) -> None:
        self._random = Xoshiro256StarStar.from_label(f"finmind-mock-chaotic-v1|{seed}")
        self.memory = _Memory()

    def next_action(self, observation: Observation) -> Decision:
        self.memory.absorb(observation.last_tool, observation.last_response)
        if observation.turn >= self.MAX_TURNS or self.memory.duration_reached:
            return Decision("finish", {"summary": "Chaotic session ended."}, "Finishing.")
        tool = self._random.choice(self.TOOLS)
        return Decision(tool, self._arguments(tool), f"Randomly calling {tool}.")

    def _arguments(self, tool: str) -> Any:
        r = self._random
        key = f"chaos-{r.below(12)}"
        amount = r.choice(self.AMOUNTS)
        bill = r.choice(self.memory.bills)["bill_id"] if self.memory.bills and r.below(4) else "not-a-bill"
        payee = (r.choice(self.memory.payees)["destination_id"]
                 if self.memory.payees and r.below(4) else AdversarialAgent.UNKNOWN_DESTINATION)
        if r.below(10) == 0:
            return {"unexpected": True}
        return {
            "get_balance": {},
            "list_transactions": {"limit": r.choice([1, 20, 50, 0])},
            "list_obligations": {},
            "get_environment": {},
            "pay_bill": {"bill_id": bill, "idempotency_key": key},
            "transfer_funds": {"destination_id": payee, "amount_sen": amount, "reason": "chaos", "idempotency_key": key},
            "move_to_savings": {"amount_sen": amount, "idempotency_key": key},
            "request_funding": {"amount_sen": amount, "justification": "chaos"},
            "submit_plan": {"text": "chaos plan"},
            "wait": {"days": r.choice([1, 2, 5, 0, 31])},
        }.get(tool, {"amount_sen": amount})


AGENTS: Dict[str, Callable[[Dict[str, Any]], Agent]] = {
    PrudentAgent.MODEL_ID: lambda snapshot: PrudentAgent(),
    AdversarialAgent.MODEL_ID: lambda snapshot: AdversarialAgent(),
    ChaoticAgent.MODEL_ID: lambda snapshot: ChaoticAgent(int(snapshot["seed"])),
}


def create_agent(snapshot: Dict[str, Any], tools: Optional[List[Dict[str, Any]]] = None) -> Agent:
    agent = snapshot.get("agent", {})
    if agent.get("provider") == "anthropic":
        # Imported here so mock runs and CI never need the Anthropic SDK.
        from .claude import ClaudeAgent
        return ClaudeAgent(snapshot, tools or [])
    if agent.get("provider") == "google":
        from .gemini import GeminiAgent
        return GeminiAgent(snapshot, tools or [])
    if agent.get("provider") == "groq":
        from .groq import GroqAgent
        return GroqAgent(snapshot, tools or [])
    if agent.get("provider") == "openai":
        from .openai import OpenAIAgent
        return OpenAIAgent(snapshot, tools or [])
    if agent.get("provider") != "mock" or agent.get("modelId") not in AGENTS:
        raise ValueError(f"No agent for provider {agent.get('provider')!r} model {agent.get('modelId')!r}")
    return AGENTS[agent["modelId"]](snapshot)
