"""The agent's tools.

Two come from SAP over MCP and are read-only: search_master_data and get_budget.
One is local: propose_form_fill, which shows a suggestion to the requestor and returns what the
rules engine thinks of it. There is deliberately no tool that saves, submits or creates anything.
"""

from __future__ import annotations

import json
import uuid
from collections.abc import Callable
from typing import Any, Literal

from pydantic import BaseModel, Field
from strands import tool

# The only MCP tools the agent is given, even if the server offers more.
SAP_TOOL_NAMES = ("search_master_data", "get_budget")
PREVIEW_TOOL_NAME = "preview_rules"

# A function that runs the rules engine over draft lines and returns its JSON verdict.
PreviewFn = Callable[[list[dict[str, Any]], str], dict[str, Any]]


class ProposedLine(BaseModel):
    """One line of the purchase request."""

    line_no: int = Field(description="1 for the first line, 2 for the second, and so on")
    short_text: str = Field(description="What is being bought, at most 40 characters")
    item_type: Literal["Material", "Service"] = Field(description="Material for goods, Service for work done by people")
    budget_type: Literal["Opex", "Capex"] | None = Field(default=None, description="Opex or Capex. Leave out if you cannot tell.")
    cost_object_type: Literal["CostCenter", "Wbs"] | None = Field(
        default=None, description="What the line is charged to: CostCenter, or Wbs for a project. Leave out if unknown."
    )
    material_group: str | None = Field(default=None, description="Material group code from search_master_data")
    material_number: str | None = Field(default=None, description="SAP material number, only if found with search_master_data")
    quantity: float | None = Field(default=None, description="How many")
    uom: str | None = Field(default=None, description="Unit of measure code, for example EA. Use AU for services.")
    unit_price: float | None = Field(default=None, description="Price per unit, only if the requestor gave it")
    delivery_date: str | None = Field(default=None, description="yyyy-MM-dd, only if the requestor gave a date")
    cost_center: str | None = Field(default=None, description="Cost center code from search_master_data")
    wbs: str | None = Field(default=None, description="WBS element code from search_master_data")
    asset_number: str | None = Field(default=None, description="Asset number from search_master_data, for CAPEX on a cost center")
    reason: str = Field(description="One sentence: why this material group and budget type")


def to_form_line(line: ProposedLine) -> dict[str, Any]:
    """Converts a proposed line to the field names the form and the rules engine use."""
    form = {
        "lineNo": line.line_no,
        "shortText": line.short_text.strip()[:40],
        "itemType": line.item_type,
        "budgetType": line.budget_type,
        "costObjectType": line.cost_object_type,
        "materialGroup": line.material_group,
        "materialNumber": line.material_number,
        "quantity": line.quantity,
        "uom": line.uom,
        "unitPrice": line.unit_price,
        "deliveryDate": line.delivery_date,
        "costCenter": line.cost_center,
        "wbs": line.wbs,
        "assetNumber": line.asset_number,
    }
    return {k: v for k, v in form.items() if v not in (None, "")}


def make_propose_tool(preview: PreviewFn, sink: list[dict[str, Any]]):
    """Builds propose_form_fill for one turn.

    Every proposal is appended to `sink`; the app streams it to the browser, where the requestor
    accepts or edits it. Nothing is saved by this tool.
    """

    @tool
    def propose_form_fill(title: str, lines: list[ProposedLine], explanation: str, justification: str = "", currency: str = "IDR") -> str:
        """Show the requestor a suggested form fill, and get it checked against the PR rules.

        Nothing is saved or submitted: the requestor sees the suggestion and decides. Call this once
        you know the material group of each line. Include every line the request should have,
        including lines already on the form. Call it again with corrections if the result lists problems
        you can fix by looking something up; ask the requestor for anything only they know.

        Args:
            title: Short title for the whole request, for example "Laptops for new engineers".
            lines: Every line of the request.
            explanation: Two sentences for the requestor on what you filled in and why.
            justification: Why the purchase is needed, in the requestor's own words if they gave a reason.
            currency: Currency of the unit prices. Default IDR.
        """
        parsed = [line if isinstance(line, ProposedLine) else ProposedLine.model_validate(line) for line in lines]
        if not parsed:
            return "Pass at least one line."

        form_lines = [to_form_line(line) for line in parsed]
        try:
            check = preview(form_lines, currency)
        except Exception as error:  # the rules engine is unreachable: still show the suggestion
            check = {"error": f"The rules check is unavailable ({error}). The form will validate the lines itself."}

        sink.append(
            {
                "id": uuid.uuid4().hex,
                "title": title.strip(),
                "justification": justification.strip(),
                "currency": currency,
                "explanation": explanation.strip(),
                "lines": form_lines,
                "reasons": {str(line.line_no): line.reason for line in parsed},
                "check": check,
            }
        )
        return _verdict_for_model(check)

    return propose_form_fill


def _verdict_for_model(check: dict[str, Any]) -> str:
    """Turns the rules engine's JSON into a short, actionable message for the model."""
    if "error" in check:
        return f"The suggestion is shown to the requestor. {check['error']}"

    parts = ["The suggestion is shown to the requestor. Rules check:"]
    for line in check.get("lines", []):
        problems = line.get("problems") or []
        locked = {k: v for k, v in (line.get("lockedSapFields") or {}).items() if v is not None}
        if problems:
            parts.append(f"- Line {line.get('lineNo')}: " + " ".join(problems))
        else:
            parts.append(f"- Line {line.get('lineNo')}: complete. SAP fields set by rule {line.get('rule')}: {json.dumps(locked)}")

    count = check.get("sapPrCount") or 0
    if count > 1:
        parts.append(f"- These lines will become {count} separate SAP PRs. Tell the requestor.")
    for budget in check.get("budget", []):
        if budget.get("isOver"):
            parts.append(f"- BUDGET WARNING: {budget.get('text')} Tell the requestor.")

    parts.append("Tell the requestor what is still missing. Fix with a new call only what you can look up; do not guess codes.")
    return "\n".join(parts)


def mcp_preview(client: Any) -> PreviewFn:
    """A PreviewFn that calls the preview_rules MCP tool on an already started MCP client."""

    def preview(lines: list[dict[str, Any]], currency: str) -> dict[str, Any]:
        result = client.call_tool_sync(
            tool_use_id=f"preview-{uuid.uuid4().hex}",
            name=PREVIEW_TOOL_NAME,
            arguments={"lines": lines, "currency": currency},
        )
        text = "".join(block.get("text", "") for block in result.get("content", []))
        if result.get("status") != "success":
            raise RuntimeError(text or "preview_rules failed")
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return {"error": text}

    return preview
