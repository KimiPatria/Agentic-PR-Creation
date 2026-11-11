# Connecting SAP

The application reaches SAP through two interfaces and nothing else. Today a mock sits behind them. This page is the checklist for replacing the mock with the real system.

## The seam

Both interfaces are in [SapPorts.cs](../api/src/PrAgent.Application/Sap/SapPorts.cs).

| Interface | What it does | Who calls it |
| :--- | :--- | :--- |
| `ISapReadGateway` | Master data (plants, units, purchasing groups, material groups, G/L accounts, materials, cost centers, WBS elements, assets) and budget | The form, the rules engine, and the agent's tools |
| `ISapPrWriter` | Creates a PR and links attachments to it | `PrCreationWorker` only, after a reviewer approved |

`Sap:Mode` in the API configuration picks the implementation:

| Value | Project | State |
| :--- | :--- | :--- |
| `Mock` | `api/src/PrAgent.Sap.Mock` | Working. Sample data, simulated PR numbers in the 90xxxxxxxx range. |
| `S4Hana` | `api/src/PrAgent.Sap.S4Hana` | Empty. Every method throws "SAP is not connected". |

## Steps

1. **Settle the open items** (below). They decide which SAP interfaces you call.
2. **Implement** `S4HanaReadGateway` and `S4HanaPrWriter` in [S4HanaSap.cs](../api/src/PrAgent.Sap.S4Hana/S4HanaSap.cs). The settings class `S4HanaOptions` already has the base URL, client, credentials secret and timeout. Read credentials from AWS Secrets Manager; do not put them in configuration.
3. **Certify the adapter** against a SAP sandbox. In [Adapters.cs](../api/tests/PrAgent.SapContractTests/Adapters.cs), enable the two commented-out test classes, fill in `SapSamples` with codes that exist in the sandbox, and run:
   ```
   dotnet test api/tests/PrAgent.SapContractTests
   ```
   The writer tests create real PRs. Run them against a sandbox only.
4. **Switch**: set `Sap:Mode` to `S4Hana`.
5. **Replace the demo data**: the placeholder rules, the material group mappings and the sections were seeded to match the mock data. Set `Seed:DemoData` to `false` on a fresh database and enter the real values in the admin view.
6. **Rewrite the agent evals** in `agent/evals/cases.jsonl` with real needs and real codes.

No other code changes. `SapSeamTests` proves the switch is configuration only, and `ArchitectureTests` fails the build if anything other than `PrCreationWorker` gains access to the write interface.

## What the contract requires

The contract tests are the specification. The parts that are easy to get wrong:

- **Idempotent create.** A second `CreatePurchaseRequisitionAsync` with the same `IdempotencyKey` must return the first PR number and create nothing. SAP's PR create has no idempotency of its own. Write the key (or the request reference plus split number) into a PR field you can search on, such as the requirement tracking number, and look it up before creating. Without this, a lost reply from SAP produces a duplicate PR on retry.
- **Unknown keys return null.** `GetMaterialAsync` and its siblings return `null` for a code that does not exist. They throw `SapException` only when SAP cannot be reached or rejects the call.
- **Transient or not.** Set `SapException.IsTransient` for timeouts and locks. A rejected PR (bad data) is not transient.
- **Search** matches code or name, ignores case, and honours `max`. Materials and cost centers are searched, never listed whole.
- **Blank is a value.** An empty `AcctAssignmentCategory` or `ItemCategory` means SAP blank (stock item, standard item). Pass it through as blank; do not treat it as missing.
- **Item numbers** arrive as 10, 20, 30. **Attachments** are linked after the PR exists; a failed link is logged and does not fail the PR.

## Fields that are not SAP master data

Three things the app needs are not attributes SAP hands over directly. Decide where each comes from:

| Field | Today | To decide |
| :--- | :--- | :--- |
| Material group → item type, default G/L account, purchasing group | Maintained by the PR admin in the app (`material_group_mapping`) | Keep in the app, or derive from SAP account determination |
| `SapWbsElement.BudgetType` (CAPEX or OPEX) | Set in the mock data | Derive from the project or investment profile, or return `null` to skip the check |
| `SapMaterial.IsStock` | Set in the mock data | Derive from material type and valuation |

## Open items

1. **Budget source.** PS (WBS budget) or CO (cost center plan), and whether a custom RFC or OData service is needed. This shapes `GetBudgetAsync`; the fields it must return are budget, commitments and actuals.
2. **SAP release and network path.** ECC or S/4HANA, and VPN or Direct Connect from AWS. This decides OData or BAPI over RFC.
3. **The real rule table.** The 16 seeded rules are placeholders (see `PlaceholderRules.cs`). The dimensions themselves (budget type, item type, cost object, stock flag) are a guess. If the real rules use another dimension, add it to `RuleFacts`.
4. **CAPEX asset numbers and material master readiness.**
5. **Approval line.** The "created" email goes to the requestor and the reviewer. The SAP release strategy's approvers are not read yet.
6. **Attachments in SAP.** Whether files are linked through GOS, DMS or not at all.

## The agent and AgentCore Gateway

The agent reads SAP through MCP tools served by the API at `/mcp`, which call `ISapReadGateway`. So the agent sees the real system the moment `Sap:Mode` changes, with no change on its side.

When the apps move into AWS, put AgentCore Gateway in front of the same read tools and set the agent's `SAP_MCP_URL` to the Gateway URL. Keep the tool set read-only: `search_master_data`, `get_budget`, `preview_rules`.
