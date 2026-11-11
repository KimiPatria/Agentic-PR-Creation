# PR Agent System Design (semi high level)

Source: WCS proposal. The AWS RFP deck was not readable.

## Actors

- **Requestor:** describes the need, submits.
- **PR reviewer (Section Manager):** approves.
- **PR admin:** maintains rules and master data.

## Layers

### 1. Experience (React SPA, SSO via Cognito to corporate IdP)

- Guided PR form: cascading, locked fields
- Agent chat panel: describe the need
- Review and admin views: approve, rules, monitor

### 2. Application

.NET Core API (ECS Fargate)

- Rules engine: locks the 16 combinations (PR type, account assignment, item category, G/L)
- Split and workflow: split rule, drafts, approval state

Strands agent (Python, AgentCore Runtime)

- PR assistant: suggests budget/PR type, pre-fills, explains
- Model and guardrails: Bedrock, content safety, session memory

### 3. Integration

- AgentCore Gateway: SAP read tools as MCP (master data, budget). Read only.
- PR create adapter: writes the PR to SAP, runs only after reviewer approval
- Email service: SES or corporate SMTP

### 4. SAP S/4HANA (via VPN or Direct Connect)

- Master data: cost center, WBS, material, G/L, purchasing group, UoM
- Budget: remaining credit (likely custom RFC / OData)
- PR create API: BAPI or PR API, returns PR number
- Attachments: linked to PR (TBC)
- Out of scope: MOA approval and procurement stay SAP standard

### 5. Shared services

- Aurora PostgreSQL: drafts, split cross-reference, audit trail
- S3 and cache: attachments, master data
- Security and ops: IAM, KMS, Secrets Manager, CloudWatch, CDK + CI/CD

## Request flow

1. Requestor describes the need in plain language, attaches quotation
2. Agent suggests budget type, PR type, material group; pre-fills the form
3. Rules engine locks derived fields; budget check warns if over remaining credit
4. Requestor confirms and submits
5. Reviewer approves or sends back
6. Adapter creates PR in SAP (split, attachment), PR number returned
7. Email to requestor and approval line
8. MOA approval and procurement in SAP (not built)

## Key design decisions

- Agent is read-only on SAP; it suggests, it never posts.
- Writes happen only via the adapter after reviewer approval. (Reading of the proposal; confirm before go-live.)
- Rules engine is deterministic and locks fields, so a wrong agent suggestion cannot create an invalid PR.
- Agent in Python (Strands has no .NET SDK); business API in .NET Core.

## Open items

1. Budget source (PS or CO) and whether a custom RFC is needed
2. SAP release (ECC vs S/4HANA) and the AWS-to-SAP network path
3. The 16-combination rules are not yet written down (possibly the missing "slide 7")
4. CAPEX / asset numbers and material master readiness

## First build (no SAP connection)

The first build runs on a developer laptop against real AWS services (Cognito, S3, SES, Bedrock Nova, Bedrock Guardrails) and a local Postgres. SAP sits behind two ports, `ISapReadGateway` and `ISapPrWriter`, with a mock adapter today and an empty `S4Hana` adapter to fill in later. See `docs/sap-hookup.md` once written.
