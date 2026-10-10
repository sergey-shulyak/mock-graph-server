# Project context (for the mock Graph server repo)

Background for anyone (human or Claude Code) working on this repo. The mock's own spec is in `/CLAUDE.md`; this file explains the product it supports and the decisions behind it. Snapshot: 2026-10-10.

## The product
"Enterprise Knowledge & Support Copilot", built by the student team "Anything But .NET" (Lviv Polytechnic): 8 students, a PM, and a technical mentor (Serhii — maintainer of this repo). Three-month project, final demo in week 12.

- Employees ask questions in chat; answers come from internal documents with a citation to the source.
- Low-confidence questions are escalated to a human (Support/SME) as an internal ticket, behind a confirmation gate so Support isn't flooded.
- **RBAC is a must**: retrieval is filtered by the asking user's permissions *before* anything reaches the LLM. If the user lacks access, the system may say a relevant document exists and suggest requesting access, but never reveals its content.
- Later features: knowledge-gap dashboard, feedback, conflict detection between documents.

SMART targets: faithfulness >85%, citation precision >90%, escalation recall >90%, p95 latency 10 s.

## Architecture
- **.NET service** — owns *all* external document-source integrations: connectors, change detection, permission sync. Publishes normalized documents plus access lists to Kafka ("New File Data"). **This is the main consumer of the mock**, via the official Microsoft Graph SDK.
- **Java service** (Spring) — core: users and identity mapping, ticket and escalation workflow, REST API for the Web UI, PostgreSQL.
- **Python service** — RAG: chunking, embeddings, vector search with ACL-metadata filtering, LLM generation (Gemini, provided by the university).
- **Kafka** — async events between services. **Web UI** — React, separate repo.
- Chat channel: **Slack** (Socket Mode) for Months 1–2; Teams adapter in Month 3.
- Everything runs locally via Docker Compose. Images are built in CI and pushed to GHCR. The team uses 5 Windows, 2 macOS and 1 Linux machine, so images must be multi-arch.

## Decisions made on 2026-10-10 (and why)
1. **No real Microsoft 365 for Months 1–2.** The university's M365 tenant (`@edu.lpnu.ua`) blocks custom app upload. The M365 Developer Program sandbox is no longer granted automatically. Students have no tenant of their own.
2. **Google Workspace rejected as a document source.** Student `@lpnu.ua` accounts allow only per-email sharing, with no usable groups, so RBAC can't be tested.
3. **Confluence Free rejected.** Permissions aren't customizable on the Free plan.
4. **This mock replaces Graph for Months 1–2.** It is Graph-compatible, so the .NET connector written against it works unchanged on real Graph.
5. **Month 3: buy Microsoft 365 Business Basic** (monthly commitment, about 3–5 licensed test users). Teams adapter is a must; SharePoint on the real tenant is a stretch goal. Time-boxed to about 2 weeks, starting around week 8–9.
6. **Abstractions from day one:** `IDocumentProvider`, a chat-channel adapter, and source-agnostic access lists (principal → document, principals keyed by email). These keep the M365 switch a configuration change, not a rewrite.
7. **Fixtures stay in CI after Month 3.** The RBAC test matrix and evaluation dataset need reproducible data that no real tenant gives.

## What the students' tests need from the mock
From their JIRA backlog (project SCRUM):
- **RBAC test matrix:** has access / no access / access via group / access revoked / rephrased question / prompt injection ("ignore access rules"). The mock must support the first four, including revocation at runtime reflected in delta.
- **Evaluation dataset:** 40 answerable and 20 unanswerable questions with expected answers and citations. The fixture documents are the corpus, so content should be realistic enough to ask questions about.
- **Identity:** a user's identity in Slack maps by email to a principal in the mock.

## Team roles (for context on who calls what)
- Team lead — Java core, service contracts.
- .NET developer — document integrations, later the Teams bot. **Primary user of this mock.**
- Three Java backend developers — tickets and escalation, notifications, storage and feedback.
- Python developers — RAG, retrieval-time RBAC filtering, analytics.
