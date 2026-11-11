"""Measures how well the configured model does the job, on the cases in evals/cases.jsonl.

Run with the API up (it serves the SAP tools) and AWS credentials for Bedrock:

    uv run pr-agent-evals                 # all cases
    uv run pr-agent-evals --only laptops  # one case
    uv run pr-agent-evals --min-pass 0.8  # exit code 1 below this share of passing cases

Each case is a plain-language need and what a correct form fill must contain. A case passes when
every check passes. Two checks apply to every case: the agent proposed something, and every code
it proposed exists in SAP (nothing invented).

The expectations match the mock SAP data set. When real SAP is connected, rewrite cases.jsonl with
real needs and real codes.
"""

from __future__ import annotations

import argparse
import asyncio
import dataclasses
import json
import re
import sys
import time
from pathlib import Path
from typing import Any

from .agent import SapTools
from .app import run_turn
from .config import Settings

ROOT = Path(__file__).resolve().parents[2]
FIELD_MAP = {
    "item_type": "itemType", "budget_type": "budgetType", "cost_object_type": "costObjectType", "material_group": "materialGroup",
    "material_number": "materialNumber", "cost_center": "costCenter", "wbs": "wbs", "asset_number": "assetNumber",
    "quantity": "quantity", "unit_price": "unitPrice", "uom": "uom",
}
CODE_KINDS = {"materialGroup": "material-groups", "costCenter": "cost-centers", "wbs": "wbs", "assetNumber": "assets", "materialNumber": "materials", "uom": "uoms"}


def load_cases(only: str | None) -> list[dict[str, Any]]:
    path = ROOT / "evals" / "cases.jsonl"
    cases = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    return [c for c in cases if only is None or c["id"] == only]


def known_codes(sap: SapTools) -> dict[str, set[str]]:
    """Every code that exists in SAP, per field, so invented codes can be detected."""
    _, _ = sap.connect()
    client = sap._client  # noqa: SLF001 - the eval needs the raw client for lookups
    known: dict[str, set[str]] = {}
    for field, kind in CODE_KINDS.items():
        result = client.call_tool_sync(tool_use_id=f"eval-{kind}", name="search_master_data", arguments={"type": kind, "query": "", "max": 25})
        text = "".join(block.get("text", "") for block in result.get("content", []))
        try:
            known[field] = {str(row["code"]).upper() for row in json.loads(text)}
        except (json.JSONDecodeError, KeyError, TypeError):
            known[field] = set()
    return known


def check_case(case: dict[str, Any], proposal: dict[str, Any] | None, reply: str, known: dict[str, set[str]]) -> list[tuple[str, bool, str]]:
    """Returns (check name, passed, detail) for every check of a case."""
    checks: list[tuple[str, bool, str]] = []
    if proposal is None:
        return [("proposed a form fill", False, "the agent never called propose_form_fill")]
    checks.append(("proposed a form fill", True, ""))

    lines = proposal.get("lines", [])
    expected = case.get("lines", [])
    checks.append(("line count", len(lines) == len(expected), f"expected {len(expected)}, got {len(lines)}"))

    for line in lines:
        for field, kind in CODE_KINDS.items():
            value = line.get(field)
            if value is not None and known.get(field) and str(value).upper() not in known[field]:
                checks.append((f"line {line.get('lineNo')} {field} exists in SAP", False, f"'{value}' is not a known {kind} code"))

    for index, want in enumerate(expected):
        got = lines[index] if index < len(lines) else {}
        for key, value in want.items():
            actual = got.get(FIELD_MAP[key])
            accepted = value if isinstance(value, list) else [value]
            if isinstance(accepted[0], (int, float)):
                ok = actual is not None and any(abs(float(actual) - float(a)) < 1e-6 for a in accepted)
            else:
                ok = actual is not None and str(actual).upper() in {str(a).upper() for a in accepted}
            checks.append((f"line {index + 1} {key}", ok, f"expected {value}, got {actual}"))

    for key in case.get("must_be_empty", []):
        filled = [line.get(FIELD_MAP[key]) for line in lines if line.get(FIELD_MAP[key])]
        checks.append((f"{key} left empty", not filled, f"the requestor gave none, the agent proposed {filled}"))

    if case.get("expect_split"):
        count = (proposal.get("check") or {}).get("sapPrCount")
        checks.append(("lines split into several PRs", bool(count and count > 1), f"sapPrCount was {count}"))
    if pattern := case.get("reply_must_match"):
        checks.append(("reply mentions it", re.search(pattern, reply) is not None, f"reply did not match {pattern}"))
    if pattern := case.get("reply_must_not_match"):
        checks.append(("reply makes no false claim", re.search(pattern, reply) is None, f"reply matched {pattern}"))
    return checks


async def run_case(case: dict[str, Any], settings: Settings, sap: SapTools) -> tuple[dict[str, Any] | None, str, str | None, float]:
    sap_tools, preview = sap.connect()
    payload = {"prompt": case["need"], "session_id": f"eval-{case['id']}-{int(time.time())}", "user": {"name": "Eval Requestor"}, "form": None, "today": time.strftime("%Y-%m-%d")}
    proposal, reply, error = None, "", None
    started = time.perf_counter()
    async for event in run_turn(payload, settings, sap_tools, preview):
        if event["type"] == "proposal":
            proposal = event["proposal"]  # the last proposal is the one the requestor ends up with
        elif event["type"] == "text":
            reply += event["delta"]
        elif event["type"] == "error":
            error = event["message"]
    return proposal, reply, error, time.perf_counter() - started


async def run(only: str | None, min_pass: float) -> int:
    settings = dataclasses.replace(Settings.from_env(), session_store="none")
    if settings.model_provider != "bedrock":
        print("Evals measure the language model. Set AGENT_MODEL_PROVIDER=bedrock (the scripted model is not an LLM).")
        return 2

    sap = SapTools(settings)
    try:
        known = known_codes(sap)
        cases = load_cases(only)
        print(f"Model: {settings.model_id}   Cases: {len(cases)}   SAP tools: {settings.mcp_url}\n")

        results, passed_cases = [], 0
        for case in cases:
            proposal, reply, error, seconds = await run_case(case, settings, sap)
            checks = [("turn completed", False, error)] if error else check_case(case, proposal, reply, known)
            ok = all(passed for _, passed, _ in checks)
            passed_cases += ok
            print(f"{'PASS' if ok else 'FAIL'}  {case['id']:<24} {seconds:5.1f}s")
            for name, passed, detail in checks:
                if not passed:
                    print(f"        - {name}: {detail}")
            results.append({"id": case["id"], "passed": ok, "seconds": round(seconds, 1), "failed_checks": [n for n, p, _ in checks if not p], "proposal": proposal, "reply": reply, "error": error})

        rate = passed_cases / len(cases) if cases else 0.0
        out = ROOT / "evals" / "last-run.json"
        out.write_text(json.dumps({"model": settings.model_id, "pass_rate": rate, "results": results}, indent=2, ensure_ascii=False), encoding="utf-8")
        print(f"\n{passed_cases}/{len(cases)} cases passed ({rate:.0%}). Threshold {min_pass:.0%}. Details: {out}")
        return 0 if rate >= min_pass else 1
    finally:
        sap.close()


def main() -> None:
    parser = argparse.ArgumentParser(description="Run the PR assistant evals against the configured model.")
    parser.add_argument("--only", help="run a single case by id")
    parser.add_argument("--min-pass", type=float, default=0.8, help="share of cases that must pass (default 0.8)")
    args = parser.parse_args()
    sys.exit(asyncio.run(run(args.only, args.min_pass)))


if __name__ == "__main__":
    main()
