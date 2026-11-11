# PR Agent

A guided purchase request form with an AI assistant. A requestor describes what they need, the assistant pre-fills the form, a deterministic rules engine locks the SAP fields, a reviewer approves, and only then is the PR created in SAP.

**SAP is not connected yet.** Master data, budgets and PR numbers come from a mock adapter and are labelled SIMULATED everywhere they appear. Connecting the real system is a configuration switch plus one adapter: see [docs/sap-hookup.md](docs/sap-hookup.md).

Background: [process flow](docs/pr_creation_process_as_is_vs_agentic_to_be_flow.md) and [system design](docs/system-design.md).

## What is in the box

| Folder | What | Stack |
| :--- | :--- | :--- |
| `api/` | Business API: rules engine, split, workflow, outbox, SAP adapters, MCP tools for the agent | .NET 10, EF Core, Postgres |
| `agent/` | PR assistant | Python 3.13, Strands, Amazon Bedrock (Nova) |
| `web/` | Guided form, chat panel, review queue, admin | React, Vite, Tailwind |
| `infra/` | Cognito, S3, SES, Bedrock Guardrail | AWS CDK (TypeScript) |

```
web ──▶ api ──▶ Postgres
         │  ├─▶ ISapReadGateway ──▶ Mock | S4Hana (empty)
         │  └─▶ ISapPrWriter    ──▶ Mock | S4Hana (empty)     only PrCreationWorker, only after approval
         ├─ /mcp  read-only SAP tools
         ▼            ▲
       agent ─────────┘     Bedrock Nova + Guardrail
```

Three rules the code enforces, each with a test that fails if it is broken:

- The agent is read-only. Its tools search, read budget and preview rules. Nothing it can call saves, submits or creates.
- SAP is written from one class, `PrCreationWorker`, and it refuses a request no reviewer approved.
- The rules engine runs on the server at save, submit and approval. A wrong suggestion from the agent cannot produce an invalid PR.

## Run it locally (no AWS needed)

Prerequisites: [PostgreSQL](https://www.postgresql.org/download/windows/) (any recent version; only its programs are used), .NET 10 SDK, Node 24, [uv](https://docs.astral.sh/uv/). No Docker.

```powershell
./scripts/dev.ps1
```

This starts Postgres and opens three windows: the API on 5080, the agent on 8080 and the web app on 5173. Open http://localhost:5173.

The app runs its own Postgres cluster in `.data/postgres` on `localhost:5433`, created on first start from your PostgreSQL installation's programs. A PostgreSQL server you already run (normally on 5432) is not touched. Manage the cluster with `./scripts/db.ps1 start | stop | status | reset`; `reset` deletes all data. If the PostgreSQL programs are not in `C:\Program Files\PostgreSQL`, set `PG_BIN` to their `bin` folder.

In this mode you sign in by picking a demo persona:

| Persona | Can do |
| :--- | :--- |
| Rina | Raise requests |
| Budi | Review requests for IT Infrastructure and IT Applications |
| Dewi | Review requests for General Affairs and Engineering |
| Sari | Administration: rules, sections, material group mapping, job monitor, audit |

Without `agent/.env` the assistant runs a **scripted stand-in with no language model**: it proposes one line from keywords and says that it is scripted. That is enough to exercise the whole flow. For the real assistant, see the next section.

To see a failed SAP creation and the retry, put `#SAPFAIL` in a line's description.

## Use real AWS services

The apps keep running on your machine and call AWS in your account.

**1. Sign in and deploy the stack**

```powershell
aws login
cd infra
npm install
npx cdk bootstrap                                   # once per account and region
npx cdk deploy -c senderEmail=you@your-company.com  # senderEmail is optional
```

Note the stack outputs. If you gave a sender address, confirm the verification email SES sends to it.

**2. Point the API at them.** Create `api/src/PrAgent.Api/appsettings.Local.json` (untracked):

```json
{
  "AWS": { "Region": "<Region>" },
  "Auth": { "Mode": "Cognito", "CognitoRegion": "<Region>", "CognitoUserPoolId": "<UserPoolId>", "CognitoClientId": "<UserPoolClientId>" },
  "Storage": { "Mode": "S3", "Bucket": "<BucketName>" },
  "Email": { "Mode": "Ses", "From": "<SenderEmail>", "RedirectAllTo": "<a verified address, while SES is in its sandbox>" }
}
```

Each block is independent. Leave one out and that part stays local (demo sign-in, files on disk, email written to the log).

**3. Point the web app at Cognito.** Copy `web/.env.example` to `web/.env.local` and fill in `CognitoAuthority`, `UserPoolClientId` and `UserPoolDomain`.

**4. Create users.** Nobody can self-register.

```powershell
aws cognito-idp admin-create-user --user-pool-id <UserPoolId> --username you@your-company.com --user-attributes Name=email,Value=you@your-company.com Name=email_verified,Value=true Name=name,Value="Your Name"
aws cognito-idp admin-add-user-to-group --user-pool-id <UserPoolId> --username you@your-company.com --group-name Admin
```

Groups are `Reviewer` and `Admin`. A reviewer must also be listed on a section in Administration → Sections; the seeded sections list the demo personas, so replace them.

**5. Give the assistant Amazon Nova.** Copy `agent/.env.example` to `agent/.env` and set:

```
AGENT_MODEL_PROVIDER=bedrock
AGENT_MODEL_ID=<a Nova model or inference profile you can invoke>
AGENT_AWS_REGION=<Region>
AGENT_GUARDRAIL_ID=<GuardrailId>
AGENT_GUARDRAIL_VERSION=<GuardrailVersion>
AGENT_SESSION_STORE=s3
AGENT_SESSION_BUCKET=<BucketName>
```

List the Nova profiles your account has:

```powershell
aws bedrock list-inference-profiles --query "inferenceProfileSummaries[?contains(inferenceProfileId,'nova')].inferenceProfileId"
```

The agent refuses to start with a model outside the Nova family.

## Tests

| What | Command | Needs |
| :--- | :--- | :--- |
| Rules engine, split, workflow, architecture rules | `dotnet test api/tests/PrAgent.UnitTests` | nothing |
| SAP adapter contract | `dotnet test api/tests/PrAgent.SapContractTests` | nothing |
| API end to end (HTTP, Postgres, outbox, failure and retry) | `dotnet test api/tests/PrAgent.IntegrationTests` | PostgreSQL installed (the tests start and delete their own cluster) |
| Agent plumbing and eval scorer | `cd agent; uv run pytest` | nothing |
| CDK template | `cd infra; npx jest` | nothing |
| Browser end to end | `cd web; npx playwright test` | the stack running, agent in scripted mode |
| **Assistant quality on Nova** | `cd agent; uv run pr-agent-evals` | the API running, Bedrock access |

The evals are the only measure of how well the language model does the job: 20 plain-language needs (English and Bahasa Indonesia) with the form fill each should produce. Run them after any change to the prompt, the tools or the model.

## Configuration reference

API (`appsettings.json`, overridden by `appsettings.Development.json`, then `appsettings.Local.json`, then environment variables):

| Key | Values | Meaning |
| :--- | :--- | :--- |
| `Sap:Mode` | `Mock`, `S4Hana` | Which SAP adapter is used |
| `Auth:Mode` | `Cognito`, `Dev` | `Dev` is refused outside the Development and Testing environments |
| `Storage:Mode` | `S3`, `Local` | Where attachments are kept |
| `Email:Mode` | `Ses`, `Log` | `Log` writes emails to the application log |
| `App:SplitKeys` | field names | What forces lines into separate PRs. The PR document type always does. |
| `Outbox:MaxAttempts` | number | Automatic attempts before a job is parked for a manual retry |
| `Seed:DemoData` | `true`, `false` | Seed demo sections and material group mappings into an empty database |

Agent: see [agent/.env.example](agent/.env.example).

## Before anyone relies on this

- The 16 rules are **placeholders**. Replace them in Administration → Rules once the real table is known.
- Reviewer routing uses a section table kept in the app. The real source has not been decided.
- The OPEX or CAPEX guidance in the assistant's prompt is generic. It needs the company's capitalisation policy.
- Material group mappings, sections and the eval cases match the mock SAP data, not the real system.
