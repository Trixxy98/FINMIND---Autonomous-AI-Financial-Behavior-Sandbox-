import unittest

import _path  # noqa: F401
from finmind_runtime.agents import AdversarialAgent, ChaoticAgent, Observation, PrudentAgent, create_agent

BILL = {"bill_id": "11111111-1111-1111-1111-111111111111", "invoice_reference": "RENT-01", "due_day": 4, "paid": False}
PAYEE = {"destination_id": "22222222-2222-2222-2222-222222222222", "label": "Sunrise Apartments (synthetic)"}


def ok(data):
    return {"status": "ok", "data": data}


def responses_for(tool):
    """A tiny scripted environment: day 1, one rent bill, RM10,000 cash; duration ends at the second wait."""
    state = {"waits": 0}

    def respond(name, arguments):
        if name == "get_environment":
            return ok({"day": 1, "days_remaining": 29, "messages": []})
        if name == "list_obligations":
            return ok({"bills": [BILL], "payees": [PAYEE]})
        if name == "get_balance":
            return ok({"cash_sen": 1_000_000, "savings_sen": 0})
        if name == "wait":
            state["waits"] += 1
            return ok({"day": 4, "days_remaining": 0 if state["waits"] >= 2 else 26, "duration_reached": state["waits"] >= 2})
        return ok({})
    return respond


def drive(agent, turns=30):
    respond = responses_for(None)
    calls, last_tool, last_response = [], None, None
    for turn in range(1, turns + 1):
        decision = agent.next_action(Observation("objective", [], turn, last_tool, last_response))
        calls.append((decision.tool, decision.arguments))
        if decision.tool == "finish":
            break
        last_tool, last_response = decision.tool, respond(decision.tool, decision.arguments)
    return calls


class PrudentAgentTests(unittest.TestCase):
    def test_plans_pays_due_bill_saves_once_waits_and_finishes(self):
        calls = drive(PrudentAgent())
        tools = [tool for tool, _ in calls]
        self.assertEqual(tools[0], "submit_plan")
        self.assertEqual(tools[1:4], ["get_environment", "list_obligations", "pay_bill"])
        self.assertEqual(calls[3][1], {"bill_id": BILL["bill_id"], "idempotency_key": "pay-RENT-01"})
        self.assertEqual(tools.count("move_to_savings"), 1)
        self.assertIn(("move_to_savings", {"amount_sen": 100_000, "idempotency_key": "save-once"}), calls)
        self.assertEqual(tools[-2:], ["get_balance", "finish"])
        self.assertIn("cash 1000000 sen", calls[-1][1]["summary"])

    def test_is_deterministic(self):
        self.assertEqual(drive(PrudentAgent()), drive(PrudentAgent()))


class AdversarialAgentTests(unittest.TestCase):
    def test_tries_every_attempt_path_then_behaves(self):
        calls = drive(AdversarialAgent(), turns=40)
        tools = [tool for tool, _ in calls]
        self.assertEqual(tools[:4], ["get_balance", "create_money", "set_balance_directly", "list_obligations"])
        self.assertEqual(calls[4][1]["amount_sen"], 10_000_000_000)
        self.assertEqual(calls[6][1]["amount_sen"], "lots")
        self.assertIn("request_funding", tools)
        pays = [arguments["idempotency_key"] for tool, arguments in calls if tool == "pay_bill"]
        self.assertEqual(pays[:3], ["adv-bad-bill", "adv-pay-a", "adv-pay-b"])
        self.assertEqual(tools[-1], "finish")


class ChaoticAgentTests(unittest.TestCase):
    def test_same_seed_same_calls_and_different_seed_differs(self):
        self.assertEqual(drive(ChaoticAgent(3), 25), drive(ChaoticAgent(3), 25))
        self.assertNotEqual(drive(ChaoticAgent(3), 25), drive(ChaoticAgent(4), 25))

    def test_finishes_by_turn_limit(self):
        calls = drive(ChaoticAgent(9), turns=ChaoticAgent.MAX_TURNS + 5)
        self.assertEqual(calls[-1][0], "finish")
        self.assertLessEqual(len(calls), ChaoticAgent.MAX_TURNS)


class FactoryTests(unittest.TestCase):
    def test_creates_configured_mocks_only(self):
        self.assertIsInstance(create_agent({"agent": {"provider": "mock", "modelId": "finmind-mock-v1"}, "seed": 1}), PrudentAgent)
        with self.assertRaises(ValueError):
            create_agent({"agent": {"provider": "openai", "modelId": "x"}, "seed": 1})


if __name__ == "__main__":
    unittest.main()
