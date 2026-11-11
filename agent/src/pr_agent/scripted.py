"""A scripted stand-in for the language model. NOT an LLM.

It follows one fixed script: look up a material group, propose a single line, say so. It exists so
the whole pipeline (API proxy, event stream, tools, form suggestion) can be developed and tested
without AWS credentials. Select it with AGENT_MODEL_PROVIDER=scripted. Its replies say that it is
scripted, so nobody mistakes it for the real assistant.
"""

from __future__ import annotations

import json
import re
import uuid
from collections.abc import AsyncIterable
from typing import Any

from strands.models import Model

_KEYWORDS = [
    (("laptop", "notebook", "monitor", "keyboard", "komputer", "computer", "pc"), "comput", "Material", "EA"),
    (("server", "switch", "router", "network", "jaringan"), "network", "Material", "EA"),
    (("licence", "license", "lisensi", "subscription", "software"), "software", "Material", "EA"),
    (("install", "setup", "implement", "support", "instalasi"), "IT Implementation", "Service", "AU"),
    (("consult", "konsultan", "advisory"), "consult", "Service", "AU"),
    (("training", "pelatihan", "workshop"), "training", "Service", "AU"),
    (("repair", "maintenance", "perbaikan", "service"), "repair", "Service", "AU"),
    (("chair", "desk", "kursi", "meja", "furniture"), "furniture", "Material", "EA"),
    (("paper", "kertas", "toner", "stationery", "atk"), "office", "Material", "EA"),
    (("bearing", "pump", "seal", "gearbox", "spare"), "spare", "Material", "EA"),
]


class ScriptedModel(Model):
    def __init__(self) -> None:
        self._config: dict[str, Any] = {"model_id": "scripted"}

    def update_config(self, **model_config: Any) -> None:
        self._config.update(model_config)

    def get_config(self) -> Any:
        return self._config

    async def structured_output(self, output_model, prompt, system_prompt=None, **kwargs):  # type: ignore[override]
        raise NotImplementedError("The scripted model does not produce structured output.")
        yield {}  # pragma: no cover

    async def stream(self, messages, tool_specs=None, system_prompt=None, **kwargs) -> AsyncIterable[dict[str, Any]]:  # type: ignore[override]
        last = messages[-1]
        results = [block["toolResult"] for block in last.get("content", []) if "toolResult" in block]

        if not results:
            need = _last_user_text(messages)
            query, _, _ = _classify(need)
            async for event in _tool_use("search_master_data", {"type": "material-groups", "query": query, "max": 5}):
                yield event
            return

        called = _tool_name_for(messages, results[0].get("toolUseId"))
        if called == "search_master_data":
            need = _last_user_text(messages)
            _, item_type, uom = _classify(need)
            group = _first_code(results[0])
            quantity = _quantity(need)
            async for event in _tool_use(
                "propose_form_fill",
                {
                    "title": need[:60] or "Purchase request",
                    "justification": need,
                    "explanation": "Scripted suggestion: one line, using the first matching material group.",
                    "lines": [
                        {
                            "line_no": 1,
                            "short_text": need[:40] or "Item",
                            "item_type": item_type,
                            "budget_type": "Opex",
                            "cost_object_type": "CostCenter",
                            "material_group": group,
                            "quantity": quantity,
                            "uom": uom,
                            "reason": "First material group that matched a keyword in the request.",
                        }
                    ],
                },
            ):
                yield event
            return

        async for event in _text(
            "[Scripted assistant, no language model] I pre-filled one line from your description. "
            "Check the material group, then add the cost center, price and delivery date."
        ):
            yield event


def _last_user_text(messages: list[dict[str, Any]]) -> str:
    for message in reversed(messages):
        if message.get("role") != "user":
            continue
        texts = [block["text"] for block in message.get("content", []) if "text" in block]
        if texts:
            return " ".join(texts).strip()
    return ""


def _tool_name_for(messages: list[dict[str, Any]], tool_use_id: str | None) -> str | None:
    for message in reversed(messages):
        for block in message.get("content", []):
            use = block.get("toolUse")
            if use and use.get("toolUseId") == tool_use_id:
                return use.get("name")
    return None


def _classify(need: str) -> tuple[str, str, str]:
    lowered = need.lower()
    for keywords, query, item_type, uom in _KEYWORDS:
        if any(keyword in lowered for keyword in keywords):
            return query, item_type, uom
    return "", "Material", "EA"


def _quantity(need: str) -> float:
    match = re.search(r"\b(\d{1,5})\b", need)
    return float(match.group(1)) if match else 1.0


def _first_code(result: dict[str, Any]) -> str | None:
    text = "".join(block.get("text", "") for block in result.get("content", []))
    try:
        rows = json.loads(text)
        return rows[0]["code"] if rows else None
    except (json.JSONDecodeError, KeyError, IndexError, TypeError):
        return None


async def _tool_use(name: str, arguments: dict[str, Any]) -> AsyncIterable[dict[str, Any]]:
    yield {"messageStart": {"role": "assistant"}}
    yield {"contentBlockStart": {"start": {"toolUse": {"toolUseId": f"scripted-{uuid.uuid4().hex[:12]}", "name": name}}}}
    yield {"contentBlockDelta": {"delta": {"toolUse": {"input": json.dumps(arguments)}}}}
    yield {"contentBlockStop": {}}
    yield {"messageStop": {"stopReason": "tool_use"}}
    yield _METADATA


async def _text(text: str) -> AsyncIterable[dict[str, Any]]:
    yield {"messageStart": {"role": "assistant"}}
    yield {"contentBlockStart": {"start": {}}}
    for word in re.findall(r"\S+\s*", text):
        yield {"contentBlockDelta": {"delta": {"text": word}}}
    yield {"contentBlockStop": {}}
    yield {"messageStop": {"stopReason": "end_turn"}}
    yield _METADATA


_METADATA = {"metadata": {"usage": {"inputTokens": 0, "outputTokens": 0, "totalTokens": 0}, "metrics": {"latencyMs": 0}}}
