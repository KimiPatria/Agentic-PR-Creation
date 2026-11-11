<#
.SYNOPSIS
  Starts the whole PR Agent stack on this machine: Postgres, the API, the agent and the web app.

.DESCRIPTION
  Postgres runs from your local PostgreSQL installation (see scripts/db.ps1). The API, the agent
  and the web app each open in their own PowerShell window so their logs stay readable. Close a
  window to stop that service; stop Postgres with ./scripts/db.ps1 stop.

  The agent uses agent/.env when it exists. Without it, the agent runs its scripted stand-in
  (no language model) so the rest of the stack can still be used.

.EXAMPLE
  ./scripts/dev.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& "$PSScriptRoot/db.ps1" start

function Start-Window([string]$title, [string]$directory, [string]$command) {
    Start-Process powershell -WorkingDirectory $directory -ArgumentList '-NoExit', '-Command', "`$host.UI.RawUI.WindowTitle = '$title'; $command"
}

Start-Window 'PR Agent: API (5080)' "$root/api/src/PrAgent.Api" 'dotnet run --launch-profile http'

if (Test-Path "$root/agent/.env") {
    Start-Window 'PR Agent: agent (8080)' "$root/agent" 'uv run pr-agent'
} else {
    Write-Warning 'agent/.env not found: the assistant will run its scripted stand-in, not Amazon Nova. Copy agent/.env.example to agent/.env to use Bedrock.'
    Start-Window 'PR Agent: agent (8080, scripted)' "$root/agent" "`$env:AGENT_MODEL_PROVIDER = 'scripted'; `$env:SAP_MCP_TOKEN = 'dev-only-agent-token'; uv run pr-agent"
}

Start-Window 'PR Agent: web (5173)' "$root/web" 'npm run dev'

Write-Host ''
Write-Host 'Open http://localhost:5173 once the three windows have finished starting.'
