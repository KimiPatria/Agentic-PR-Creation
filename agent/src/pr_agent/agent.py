"""Builds the Strands agent for one turn of a conversation."""

from __future__ import annotations

import re
import threading
from typing import Any

from strands import Agent
from strands.agent.conversation_manager import SlidingWindowConversationManager
from strands.models import Model

from .config import Settings
from .prompts import system_prompt_for
from .tools import SAP_TOOL_NAMES, PreviewFn, make_propose_tool, mcp_preview


def build_model(settings: Settings) -> Model:
    if settings.model_provider == "scripted":
        from .scripted import ScriptedModel

        return ScriptedModel()

    from strands.models.bedrock import BedrockModel

    config: dict[str, Any] = {
        "model_id": settings.model_id,
        "temperature": settings.temperature,
        "max_tokens": settings.max_tokens,
    }
    if settings.guardrail_id:
        config["guardrail_id"] = settings.guardrail_id
        config["guardrail_version"] = settings.guardrail_version
        config["guardrail_trace"] = "enabled"
    return BedrockModel(region_name=settings.region, **config)


def build_session_manager(settings: Settings, session_id: str):
    """Conversation memory for one session, or None when it is switched off."""
    safe_id = re.sub(r"[^A-Za-z0-9_-]", "_", session_id)[:120]
    if settings.session_store == "file":
        from strands.session.file_session_manager import FileSessionManager

        return FileSessionManager(session_id=safe_id, storage_dir=settings.session_dir)
    if settings.session_store == "s3":
        from strands.session.s3_session_manager import S3SessionManager

        return S3SessionManager(session_id=safe_id, bucket=settings.session_bucket, prefix=settings.session_prefix, region_name=settings.region)
    return None


class SapTools:
    """The connection to the SAP read tools over MCP.

    Opened on first use and kept for the life of the process. If the MCP server is not up yet, the
    next turn simply tries again.
    """

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._lock = threading.Lock()
        self._client: Any = None
        self._tools: list[Any] = []

    def connect(self) -> tuple[list[Any], PreviewFn]:
        with self._lock:
            if self._client is None:
                from strands.tools.mcp import MCPClient

                headers = {"X-Service-Token": self._settings.mcp_token} if self._settings.mcp_token else None
                client = MCPClient(url=self._settings.mcp_url, headers=headers, startup_timeout=15)
                client.start()
                try:
                    offered = client.list_tools_sync()
                    tools = [t for t in offered if t.tool_name in SAP_TOOL_NAMES]
                    missing = set(SAP_TOOL_NAMES) - {t.tool_name for t in tools}
                    if missing:
                        raise RuntimeError(f"The MCP server at {self._settings.mcp_url} does not offer: {', '.join(sorted(missing))}")
                except BaseException:
                    client.stop(None, None, None)
                    raise
                self._client, self._tools = client, tools
            return self._tools, mcp_preview(self._client)

    def close(self) -> None:
        with self._lock:
            if self._client is not None:
                self._client.stop(None, None, None)
                self._client, self._tools = None, []


def build_agent(
    settings: Settings,
    session_id: str,
    sap_tools: list[Any],
    preview: PreviewFn,
    proposals: list[dict[str, Any]],
    *,
    form: dict[str, Any] | None = None,
    today: str | None = None,
    user_name: str | None = None,
    model: Model | None = None,
) -> Agent:
    return Agent(
        model=model or build_model(settings),
        system_prompt=system_prompt_for(form, today, user_name),
        tools=[*sap_tools, make_propose_tool(preview, proposals)],
        session_manager=build_session_manager(settings, session_id),
        conversation_manager=SlidingWindowConversationManager(window_size=30),
        callback_handler=None,
    )
