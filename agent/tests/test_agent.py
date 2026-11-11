"""Tests of the agent's plumbing, run with the scripted model so no AWS access is needed.

They prove the event stream, the tool wiring and the proposal format. They say nothing about how
well Amazon Nova performs; that is what `pr-agent-evals` measures.
"""

from __future__ import annotations

import dataclasses
import json
from typing import Any

import pytest
from strands import tool

from pr_agent.app import run_turn
from pr_agent.config import ConfigError, Settings
from pr_agent.prompts import SYSTEM_PROMPT, system_prompt_for
from pr_agent.scripted import ScriptedModel
from pr_agent.tools import ProposedLine, _verdict_for_model, make_propose_tool, to_form_line

BASE = Settings(
    model_provider="scripted", model_id=None, region=None, temperature=0.2, max_tokens=1000,
    guardrail_id=None, guardrail_version=None, mcp_url="http://unused", mcp_token=None,
    session_store="none", session_dir=".data/sessions", session_bucket=None, session_prefix="", port=8080,
)

CHECK_OK = {
    "lines": [{"lineNo": 1, "valid": False, "rule": None, "lockedSapFields": {"purchasingGroup": "P01"}, "problems": ["Cost center is required."]}],
    "sapPrCount": 1,
    "budget": [],
}


def _settings(**changes: Any) -> Settings:
    return dataclasses.replace(BASE, **changes)


@tool
def search_master_data(type: str, query: str = "", max: int = 10) -> str:
    """Search SAP master data (test double)."""
    assert type == "material-groups"
    return json.dumps([{"code": "IT01", "name": "Computers and Peripherals", "itemType": "Material"}])


async def _collect(payload: dict[str, Any], preview=lambda lines, currency: CHECK_OK) -> list[dict[str, Any]]:
    return [event async for event in run_turn(payload, BASE, [search_master_data], preview, model=ScriptedModel())]


# ---- configuration ---------------------------------------------------------------------------


def test_bedrock_requires_a_model_id():
    with pytest.raises(ConfigError, match="AGENT_MODEL_ID is not set"):
        _settings(model_provider="bedrock").validate()


@pytest.mark.parametrize("model_id", ["anthropic.claude-3-5-sonnet-20241022-v2:0", "us.anthropic.claude-sonnet-4-20250514-v1:0", "meta.llama3-70b-instruct-v1:0"])
def test_models_outside_the_nova_family_are_refused(model_id: str):
    with pytest.raises(ConfigError, match="restricted to the Amazon Nova family"):
        _settings(model_provider="bedrock", model_id=model_id).validate()


@pytest.mark.parametrize("model_id", ["amazon.nova-pro-v1:0", "apac.amazon.nova-lite-v1:0", "us.amazon.nova-premier-v1:0"])
def test_nova_models_and_inference_profiles_are_accepted(model_id: str):
    _settings(model_provider="bedrock", model_id=model_id).validate()


def test_guardrail_needs_both_id_and_version():
    with pytest.raises(ConfigError, match="AGENT_GUARDRAIL"):
        _settings(model_provider="bedrock", model_id="amazon.nova-pro-v1:0", guardrail_id="abc").validate()


def test_s3_sessions_need_a_bucket():
    with pytest.raises(ConfigError, match="AGENT_SESSION_BUCKET"):
        _settings(session_store="s3").validate()


# ---- prompt ----------------------------------------------------------------------------------


def test_prompt_tells_the_model_it_cannot_write_to_sap():
    assert "cannot submit a request" in SYSTEM_PROMPT
    assert "Never invent a code" in SYSTEM_PROMPT


def test_prompt_carries_the_current_form_without_empty_fields():
    prompt = system_prompt_for({"title": "Laptops", "plant": "", "lines": [{"lineNo": 1, "shortText": "Laptop", "wbs": None}]}, "2026-11-10", "Rina")
    assert '"title": "Laptops"' in prompt
    assert '"shortText": "Laptop"' in prompt
    assert "wbs" not in prompt.split("# This conversation")[1]
    assert "2026-11-10" in prompt and "Rina" in prompt


def test_prompt_says_when_the_form_is_empty():
    assert "The form is empty." in system_prompt_for(None, None, None)


# ---- proposal tool ---------------------------------------------------------------------------


def test_proposed_line_maps_to_form_field_names_and_drops_blanks():
    line = ProposedLine(line_no=2, short_text="x" * 60, item_type="Service", budget_type="Opex", material_group="IT10", uom="AU", reason="because")
    form = to_form_line(line)
    assert form == {"lineNo": 2, "shortText": "x" * 40, "itemType": "Service", "budgetType": "Opex", "materialGroup": "IT10", "uom": "AU"}


def test_verdict_tells_the_model_about_problems_splits_and_budget():
    verdict = _verdict_for_model(
        {
            "lines": [
                {"lineNo": 1, "rule": "R01", "lockedSapFields": {"prDocType": "NB", "glAccount": None}, "problems": []},
                {"lineNo": 2, "rule": None, "lockedSapFields": {}, "problems": ["Cost center is required."]},
            ],
            "sapPrCount": 2,
            "budget": [{"isOver": True, "text": "Over by 100."}],
        }
    )
    assert 'Line 1: complete. SAP fields set by rule R01: {"prDocType": "NB"}' in verdict
    assert "Line 2: Cost center is required." in verdict
    assert "2 separate SAP PRs" in verdict
    assert "BUDGET WARNING: Over by 100." in verdict


def test_proposal_is_still_shown_when_the_rules_check_is_down():
    sink: list[dict[str, Any]] = []

    def broken(lines, currency):
        raise RuntimeError("connection refused")

    propose = make_propose_tool(broken, sink)
    result = propose(title="Laptops", lines=[{"line_no": 1, "short_text": "Laptop", "item_type": "Material", "reason": "r"}], explanation="e")

    assert "shown to the requestor" in result and "unavailable" in result
    assert sink[0]["lines"] == [{"lineNo": 1, "shortText": "Laptop", "itemType": "Material"}]


# ---- a whole turn ----------------------------------------------------------------------------


async def test_turn_streams_tool_calls_a_checked_proposal_text_and_done():
    seen: dict[str, Any] = {}

    def preview(lines, currency):
        seen["lines"], seen["currency"] = lines, currency
        return CHECK_OK

    events = await _collect({"prompt": "I need 5 laptops for new engineers", "session_id": "s1", "today": "2026-11-10"}, preview)
    types = [e["type"] for e in events]

    assert [e["name"] for e in events if e["type"] == "tool"] == ["search_master_data", "propose_form_fill"]
    assert types[-1] == "done"
    assert types.index("proposal") < types.index("text")

    proposal = next(e["proposal"] for e in events if e["type"] == "proposal")
    line = proposal["lines"][0]
    assert line["materialGroup"] == "IT01"  # came from the search result, not invented
    assert line["quantity"] == 5.0
    assert line["itemType"] == "Material"
    assert proposal["check"] == CHECK_OK
    assert proposal["reasons"]["1"]
    assert seen == {"lines": proposal["lines"], "currency": "IDR"}

    text = "".join(e["delta"] for e in events if e["type"] == "text")
    assert "Scripted assistant" in text


async def test_empty_message_is_an_error_event_not_an_exception():
    assert await _collect({"prompt": "   "}) == [{"type": "error", "message": "The message was empty."}]


async def test_a_failing_model_becomes_a_safe_error_event():
    class Broken(ScriptedModel):
        async def stream(self, *args, **kwargs):  # type: ignore[override]
            raise RuntimeError("AccessDeniedException: secret internal detail")
            yield {}

    events = [e async for e in run_turn({"prompt": "hello"}, BASE, [search_master_data], lambda l, c: CHECK_OK, model=Broken())]

    assert events[-1]["type"] == "error"
    assert "secret internal detail" not in events[-1]["message"]
    assert "Bedrock model access" in events[-1]["message"]


def test_thinking_filter_hides_reasoning_even_when_tags_are_split():
    from pr_agent.app import ThinkingFilter

    f = ThinkingFilter()
    pieces = ["<", "thinking", "> secret ", "plan </thin", "king>", "\nHello ", "<b>there</b>", " <thi"]
    shown = "".join(f.feed(p) for p in pieces) + f.flush()
    assert "secret" not in shown and "thinking" not in shown
    assert shown.strip() == "Hello <b>there</b> <thi"
