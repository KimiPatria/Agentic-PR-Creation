"""Agent settings, read from the environment (and from a .env file next to pyproject.toml)."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


class ConfigError(RuntimeError):
    """The agent is misconfigured. The message says which variable to fix."""


def load_dotenv(path: Path | None = None) -> None:
    """Loads KEY=VALUE lines from .env without overriding variables that are already set."""
    path = path or Path(__file__).resolve().parents[2] / ".env"
    if not path.is_file():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        os.environ.setdefault(key.strip(), value.strip().strip('"').strip("'"))


@dataclass(frozen=True)
class Settings:
    # "bedrock" calls Amazon Bedrock. "scripted" is a fixed, rule-based stand-in with no LLM,
    # for developing and testing the plumbing without AWS credentials.
    model_provider: str
    model_id: str | None
    region: str | None
    temperature: float
    max_tokens: int
    guardrail_id: str | None
    guardrail_version: str | None

    # Where the SAP read tools are served over MCP. Locally this is the .NET API; in AWS it is
    # the AgentCore Gateway URL. Nothing else changes when it moves.
    mcp_url: str
    mcp_token: str | None

    # Conversation memory: "file" (local folder), "s3" (real AWS) or "none".
    session_store: str
    session_dir: str
    session_bucket: str | None
    session_prefix: str

    port: int

    @staticmethod
    def from_env() -> "Settings":
        load_dotenv()
        env = os.environ.get

        settings = Settings(
            model_provider=(env("AGENT_MODEL_PROVIDER") or "bedrock").lower(),
            model_id=env("AGENT_MODEL_ID") or None,
            region=env("AGENT_AWS_REGION") or env("AWS_REGION") or env("AWS_DEFAULT_REGION") or None,
            temperature=float(env("AGENT_TEMPERATURE") or "0.2"),
            max_tokens=int(env("AGENT_MAX_TOKENS") or "2000"),
            guardrail_id=env("AGENT_GUARDRAIL_ID") or None,
            guardrail_version=env("AGENT_GUARDRAIL_VERSION") or None,
            mcp_url=env("SAP_MCP_URL") or "http://localhost:5080/mcp",
            mcp_token=env("SAP_MCP_TOKEN") or None,
            session_store=(env("AGENT_SESSION_STORE") or "file").lower(),
            session_dir=env("AGENT_SESSION_DIR") or ".data/sessions",
            session_bucket=env("AGENT_SESSION_BUCKET") or None,
            session_prefix=env("AGENT_SESSION_PREFIX") or "agent-sessions/",
            port=int(env("AGENT_PORT") or "8080"),
        )
        settings.validate()
        return settings

    def validate(self) -> None:
        if self.model_provider not in ("bedrock", "scripted"):
            raise ConfigError("AGENT_MODEL_PROVIDER must be 'bedrock' or 'scripted'.")

        if self.model_provider == "bedrock":
            if not self.model_id:
                raise ConfigError(
                    "AGENT_MODEL_ID is not set. Use an Amazon Nova model or inference profile your account can call, "
                    "for example apac.amazon.nova-pro-v1:0. See agent/.env.example."
                )
            # This account may only use the Amazon Nova family on Bedrock. Fail here, with a clear
            # message, instead of at the first user message with an access-denied error.
            if "amazon.nova" not in self.model_id.lower():
                raise ConfigError(
                    f"AGENT_MODEL_ID '{self.model_id}' is not an Amazon Nova model. "
                    "This deployment is restricted to the Amazon Nova family on Bedrock."
                )
            if bool(self.guardrail_id) != bool(self.guardrail_version):
                raise ConfigError("Set both AGENT_GUARDRAIL_ID and AGENT_GUARDRAIL_VERSION, or neither.")

        if self.session_store not in ("file", "s3", "none"):
            raise ConfigError("AGENT_SESSION_STORE must be 'file', 's3' or 'none'.")
        if self.session_store == "s3" and not self.session_bucket:
            raise ConfigError("AGENT_SESSION_BUCKET is required when AGENT_SESSION_STORE is 's3'.")
