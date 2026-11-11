"""AgentCore-compatible entrypoint.

Serves POST /invocations and GET /ping on port 8080, the contract of Bedrock AgentCore Runtime, so
the same code runs on a laptop today and on AgentCore Runtime later without changes.

Request payload (sent by the .NET API, never by the browser):
    {"prompt": "...", "session_id": "...", "user": {"name": "..."}, "form": {...}, "today": "yyyy-MM-dd"}

Response: a server-sent event stream of JSON objects:
    {"type": "text", "delta": "..."}            assistant text, in pieces
    {"type": "tool", "name": "..."}             a tool call started (for a "looking up..." hint)
    {"type": "proposal", "proposal": {...}}     a suggested form fill for the requestor to accept
    {"type": "error", "message": "..."}         the turn failed; the message is safe to show
    {"type": "done"}
"""

from __future__ import annotations

import logging
import uuid
from collections.abc import AsyncIterator
from typing import Any

from bedrock_agentcore.runtime import BedrockAgentCoreApp
from strands.models import Model

from .agent import SapTools, build_agent
from .config import Settings

log = logging.getLogger("pr_agent")
app = BedrockAgentCoreApp()

_settings: Settings | None = None
_sap: SapTools | None = None


class ThinkingFilter:
    """Removes <thinking>...</thinking> blocks from streamed text.

    Amazon Nova writes its reasoning in these tags. It is not meant for the requestor. Tags can
    arrive split across deltas, so a possible partial tag is held back until it is resolved.
    """

    _OPEN, _CLOSE = "<thinking>", "</thinking>"

    def __init__(self) -> None:
        self._buffer = ""
        self._inside = False

    def feed(self, text: str) -> str:
        self._buffer += text
        out: list[str] = []
        while self._buffer:
            tag = self._CLOSE if self._inside else self._OPEN
            at = self._buffer.find(tag)
            if at >= 0:
                if not self._inside:
                    out.append(self._buffer[:at])
                self._buffer = self._buffer[at + len(tag):]
                self._inside = not self._inside
                continue
            # No full tag. Keep the longest tail that could still become one.
            hold = next((n for n in range(min(len(tag) - 1, len(self._buffer)), 0, -1) if tag.startswith(self._buffer[-n:])), 0)
            if not self._inside:
                out.append(self._buffer[: len(self._buffer) - hold])
            self._buffer = self._buffer[len(self._buffer) - hold:]
            break
        return "".join(out)

    def flush(self) -> str:
        rest, self._buffer = ("" if self._inside else self._buffer), ""
        return rest


def _runtime() -> tuple[Settings, SapTools]:
    global _settings, _sap
    if _settings is None:
        _settings = Settings.from_env()
        _sap = SapTools(_settings)
    assert _sap is not None
    return _settings, _sap


async def run_turn(
    payload: dict[str, Any],
    settings: Settings,
    sap_tools: list[Any],
    preview,
    *,
    model: Model | None = None,
) -> AsyncIterator[dict[str, Any]]:
    """Runs one user message through the agent and yields the events described in the module docstring."""
    prompt = str(payload.get("prompt") or "").strip()
    if not prompt:
        yield {"type": "error", "message": "The message was empty."}
        return

    proposals: list[dict[str, Any]] = []
    agent = build_agent(
        settings,
        str(payload.get("session_id") or uuid.uuid4().hex),
        sap_tools,
        preview,
        proposals,
        form=payload.get("form"),
        today=payload.get("today"),
        user_name=(payload.get("user") or {}).get("name"),
        model=model,
    )

    announced: set[str] = set()
    emitted = 0
    thinking = ThinkingFilter()
    try:
        async for event in agent.stream_async(prompt):
            if text := event.get("data"):
                if visible := thinking.feed(text):
                    yield {"type": "text", "delta": visible}

            use = event.get("current_tool_use") or {}
            tool_id = use.get("toolUseId")
            if tool_id and tool_id not in announced:
                announced.add(tool_id)
                yield {"type": "tool", "name": use.get("name")}

            while emitted < len(proposals):
                yield {"type": "proposal", "proposal": proposals[emitted]}
                emitted += 1
    except Exception as error:  # noqa: BLE001 - every failure must reach the user as an event
        log.exception("Agent turn failed")
        yield {"type": "error", "message": _friendly(error)}
        return

    if tail := thinking.flush():
        yield {"type": "text", "delta": tail}
    while emitted < len(proposals):
        yield {"type": "proposal", "proposal": proposals[emitted]}
        emitted += 1
    yield {"type": "done"}


def _friendly(error: Exception) -> str:
    text = f"{type(error).__name__}: {error}"
    if "ExpiredToken" in text or "expired" in text.lower() and "token" in text.lower():
        return "The assistant's AWS session has expired. Ask the administrator to sign in again (aws login)."
    if "AccessDenied" in text or "not authorized" in text.lower():
        return "The assistant is not allowed to call its language model. Check Bedrock model access for AGENT_MODEL_ID."
    if "NoCredentials" in text or "Unable to locate credentials" in text:
        return "The assistant has no AWS credentials. Ask the administrator to sign in (aws login)."
    if "Throttling" in text or "TooManyRequests" in text:
        return "The assistant is busy right now. Try again in a moment."
    return "The assistant ran into a problem and could not finish. You can still fill in the form by hand."


@app.entrypoint
async def invoke(payload: dict[str, Any], context: Any = None):
    try:
        settings, sap = _runtime()
        sap_tools, preview = sap.connect()
    except Exception as error:  # noqa: BLE001
        log.exception("Agent could not start a turn")
        from .config import ConfigError

        message = str(error) if isinstance(error, ConfigError) else "The assistant cannot reach the SAP lookup tools right now. You can still fill in the form by hand."
        yield {"type": "error", "message": message}
        return

    if not payload.get("session_id") and context is not None and getattr(context, "session_id", None):
        payload = {**payload, "session_id": context.session_id}

    async for event in run_turn(payload, settings, sap_tools, preview):
        yield event


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    settings, _ = _runtime()  # fail fast on bad configuration
    log.info(
        "PR assistant starting on port %s (model provider: %s, model: %s, SAP tools: %s)",
        settings.port, settings.model_provider, settings.model_id or "-", settings.mcp_url,
    )
    if settings.model_provider == "scripted":
        log.warning("AGENT_MODEL_PROVIDER is 'scripted': replies come from a fixed script, not a language model.")
    app.run(port=settings.port)


if __name__ == "__main__":
    main()
