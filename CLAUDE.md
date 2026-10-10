# Mock Microsoft Graph Server — project brief for Claude Code

## Why this exists

A student team ("Anything But .NET", Lviv Polytechnic) is building an **Enterprise Knowledge & Support Copilot**: employees ask questions in chat (Slack now, Teams later), a RAG pipeline answers from internal documents with citations, and low-confidence questions are escalated to a human. **RBAC is a core requirement**: a user must never get an answer built from a document they can't access.

The original plan was Microsoft 365 (SharePoint + Teams + Entra ID). The university tenant blocks custom apps and the team has no M365 tenant of its own. Google Drive was tried as a replacement, but student accounts give only per-email sharing — no groups — which is useless for testing RBAC.

**Decision:** develop Months 1–2 against this mock; in Month 3 a real Microsoft 365 Business Basic tenant is bought and the students' code is pointed at real Graph.

This repo is the mock. It is maintained by the team's mentor (Serhii), not by the students. The students consume it as a Docker image.

## The single most important design rule

**Be wire-compatible with Microsoft Graph v1.0 for the subset implemented.** Same paths, same JSON property names, same `@odata.*` annotations, same paging behaviour. The students' .NET service will use the **official `Microsoft.Graph` SDK** with only the base URL changed:

```csharp
var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider("user:alice"));
var adapter = new HttpClientRequestAdapter(auth) { BaseUrl = "http://mock-graph:8080/v1.0" };
var graph = new GraphServiceClient(adapter);
```

In Month 3 they switch `BaseUrl` to `https://graph.microsoft.com/v1.0` and swap the auth provider. If their code needs any other change, the mock was wrong.

To guarantee shapes: reference the `Microsoft.Graph` NuGet package in the server and build responses from its model classes (`DriveItem`, `Permission`, `User`, `Group`, collection responses), serialized with Kiota's JSON serialization writer. When unsure of a shape, check the Graph v1.0 docs or the OpenAPI spec in `microsoftgraph/msgraph-metadata` — do not guess.

## Tech

- .NET 10 (LTS), ASP.NET Core minimal APIs.
- Storage: in-memory state seeded from a YAML fixture (YamlDotNet). Optional SQLite persistence is a nice-to-have, not required.
- Tests: xUnit. Integration tests use `WebApplicationFactory` **and the real `Microsoft.Graph` SDK client** pointed at the test server — that's the contract.
- Ships as a multi-arch Docker image (`linux/amd64`, `linux/arm64`) to GHCR. The author develops on Apple Silicon; 5 of 8 students are on Windows x64.

## Scope

### Auth (fake)
- Accept any `Authorization: Bearer <token>` header. Token format: `user:<userId-or-upn>` → the call runs as that user. `app` → app-only context (sees everything, like an application permission). Missing/invalid header → `401` with a Graph-style error body.
- Graph error body shape: `{ "error": { "code": "...", "message": "...", "innerError": { "request-id": "...", "date": "..." } } }`.

### Directory
- `GET /v1.0/users`, `GET /v1.0/users/{id|upn}`, `GET /v1.0/me`
- `GET /v1.0/users/{id}/memberOf`, `GET /v1.0/users/{id}/transitiveMemberOf` (results are directoryObjects with `@odata.type: "#microsoft.graph.group"`)
- `GET /v1.0/groups`, `GET /v1.0/groups/{id}`, `GET /v1.0/groups/{id}/members`, `GET /v1.0/groups/{id}/transitiveMembers`
- `GET /v1.0/groups/delta` — group membership changes (`members@delta`), with delta tokens. Membership changes don't show up in drive delta, so this is how the students detect "user removed from group".
- Nested groups are required.

### Sites and files
- `GET /v1.0/sites`, `GET /v1.0/sites/{siteId}`, `GET /v1.0/sites/{siteId}/drives`, `GET /v1.0/drives/{driveId}`
- `GET /v1.0/drives/{driveId}/root`, `GET /v1.0/drives/{driveId}/root/children`, `GET /v1.0/drives/{driveId}/items/{itemId}`, `GET /v1.0/drives/{driveId}/items/{itemId}/children`
- `GET /v1.0/drives/{driveId}/items/{itemId}/content` — returns file bytes with the right content type. Real Graph returns a `302` to a download URL; the SDK follows redirects, so either `302` to an internal URL or a direct `200` is fine. Prefer `302` for fidelity.
- `GET /v1.0/drives/{driveId}/items/{itemId}/permissions`
  - Each permission: `id`, `roles` (`read` | `write` | `owner`), `grantedToV2` with `user` or `group` (`id`, `displayName`, `email`), and `inheritedFrom` (`driveId`, `id`, `path`) when inherited.
  - **Inheritance:** folder permissions apply to descendants unless the child is marked `breakInheritance: true` in the fixture (SharePoint semantics).
- Visibility: in a `user:` context, list and get endpoints return only items the user can read (directly, via a group, via a nested group, or via inheritance). Otherwise `404` — not `403` — to match Graph's behaviour of not leaking existence.

### Change tracking (critical)
- `GET /v1.0/drives/{driveId}/root/delta` with `@odata.nextLink` and `@odata.deltaLink` (opaque token). First call returns everything; later calls with the token return only changes since then.
- Deleted items come back with a `deleted: {}` facet.
- Support the `Prefer: deltashowsharingchanges` request header: items whose permissions changed appear with the `@microsoft.graph.sharedChanged: "True"` annotation. Verify the exact annotation name and value against the Graph docs before implementing.
- `GET /v1.0/drives/{driveId}/root/delta?token=latest` returns no items and only a fresh `deltaLink`.

### OData
- `$top` and `@odata.nextLink` paging on every collection. Default page size is small and configurable (default **5**), so client paging bugs show up immediately.
- `$select` on users, groups and driveItems.
- `$filter`: skip unless needed. If a request uses an unsupported query option, return `400` with a clear message rather than silently ignoring it.

### Admin API (not Graph; prefix `/_admin`)
For tests and demos to change state at runtime. Every mutation must be reflected in the delta endpoints.
- `POST /_admin/reset` — reload the fixture.
- `POST /_admin/permissions` `{ itemPath | itemId, principal: "user:alice" | "group:eng-backend", role }` — grant.
- `DELETE /_admin/permissions` with the same body — revoke.
- `POST /_admin/groups/{groupId}/members` / `DELETE ...` `{ member: "user:bob" | "group:x" }`.
- `POST /_admin/files` (multipart: path, file) — add or replace a file. `DELETE /_admin/files?path=...` — delete.
- `GET /_admin/state` — dump current state as YAML (handy for debugging tests).

### Non-goals
- No real OAuth, Entra, or token validation.
- No Teams or bot messaging (covered by the Microsoft 365 Agents Playground; the product's primary channel is Slack).
- No writes through Graph endpoints (`PUT`, `PATCH`, `POST` on Graph paths). Mutations go through `/_admin` only.
- No search (`/search/query`) unless the team asks for it.
- No sharing links, sensitivity labels, versions, or thumbnails.

## Fixture format

See `fixtures/sample.yaml`. Paths are drive-relative. File content is either inline `content:` (text) or `source:` pointing to a file under `fixtures/files/`. The fixture is the data behind the team's RBAC test matrix and evaluation dataset, so keep IDs stable and human-readable (`alice`, `eng-backend`). Generate GUID-like IDs deterministically from those names where Graph expects GUIDs.

## Milestones

1. Skeleton: fixture loading, fake auth, users and groups (incl. transitive), paging, error format. Contract tests with the Graph SDK.
2. Sites, drives, items, content, permissions with inheritance and user-scoped visibility.
3. Drive delta + groups delta + admin API mutations reflected in delta.
4. Dockerfile, multi-arch build, GitHub Actions publishing to GHCR, a `compose.yaml` snippet for the students, README with the 10-line SDK setup.
5. Contract test suite that can also run against a real tenant (base URL + token from env). In Month 3 it is run against Business Basic to find mismatches.

## Conventions

- Conventional Commits. Branch names `<area>-<3-word-description>`.
- Formatting: `dotnet format` with an `.editorconfig`; CI fails on formatting diffs.
- README written for the students: what the mock covers, what it doesn't, how to run it, how to point the Graph SDK at it, how to write a fixture.
