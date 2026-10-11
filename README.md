# Mock Microsoft Graph server

A small HTTP server that answers the subset of [Microsoft Graph v1.0](https://learn.microsoft.com/graph/api/overview) you need to build and test a document-permissions-aware app (sites, drives, files, permissions, users, groups, delta queries) without a Microsoft 365 tenant.

You point the official `Microsoft.Graph` SDK at it with a different base URL. The paths, JSON property names, `@odata.*` annotations and paging behaviour match Graph, so the same client code works against the real service later. Access control is real: a user only sees the files they can read, through direct grants, group membership, nested groups and folder inheritance.

It is built and maintained by the course mentor. Your team consumes it as a Docker image.

## Contents

- [What it covers](#what-it-covers)
- [What it does not cover](#what-it-does-not-cover)
- [Running it](#running-it)
- [Using the Graph SDK](#using-the-graph-sdk)
- [Authentication](#authentication)
- [Sample data and visibility](#sample-data-and-visibility)
- [Paging](#paging)
- [Change tracking](#change-tracking)
- [Admin API](#admin-api)
- [Writing a fixture](#writing-a-fixture)
- [Known differences from Microsoft Graph](#known-differences-from-microsoft-graph)
- [Development](#development)

## What it covers

All paths are under `/v1.0`. Every `GET` below is supported. Nothing else under `/v1.0` is implemented: other methods return `405`, unknown paths return `404`.

**Directory**

| Path | Notes |
|---|---|
| `GET /users` | Not filtered by visibility |
| `GET /users/{id or upn}` | |
| `GET /users/{id}/memberOf`, `GET /users/{id}/transitiveMemberOf` | Returns `directoryObject`s with `@odata.type` `#microsoft.graph.group` |
| `GET /me`, `GET /me/memberOf`, `GET /me/transitiveMemberOf` | User tokens only. With `Bearer app` they return `400` |
| `GET /groups`, `GET /groups/{id}` | Not filtered by visibility |
| `GET /groups/{id}/members`, `GET /groups/{id}/transitiveMembers` | Nested groups are supported |
| `GET /groups/delta` | Group membership changes, with `members@delta` (see [Change tracking](#change-tracking)) |

**Sites and drives**

| Path | Notes |
|---|---|
| `GET /sites`, `GET /sites/{siteId}` | |
| `GET /sites/{siteId}/drives`, `GET /sites/{siteId}/drive` | |
| `GET /drives/{driveId}` | |

**Files**

| Path | Notes |
|---|---|
| `GET /drives/{driveId}/root` | Visible to every caller |
| `GET /drives/{driveId}/root/children` | Only children the caller can read |
| `GET /drives/{driveId}/items/{itemId}` | `404 itemNotFound` if the caller cannot read it |
| `GET /drives/{driveId}/items/{itemId}/children` | |
| `GET /drives/{driveId}/items/{itemId}/content` | `302` to `/_download/{token}`; the SDK follows it |
| `GET /drives/{driveId}/items/{itemId}/permissions` | Includes `inheritedFrom` for inherited grants |
| `GET /drives/{driveId}/root:/path/to/file.md:` | Path addressing, also `:/children`, `:/content`, `:/permissions` |

**Change tracking**

| Path | Notes |
|---|---|
| `GET /drives/{driveId}/root/delta` | Also `/items/{itemId}/delta` (folder scope), the `delta()` form and `?token=` |
| `GET /groups/delta` | Also the `delta()` form |

**Non-Graph**: `/_admin/*` (see [Admin API](#admin-api)) and `/_download/{token}`.

## What it does not cover

- No real OAuth, Entra ID or token validation. Any `Bearer` value is parsed as described in [Authentication](#authentication).
- No writes through Graph. `PUT`, `PATCH` and `POST` on Graph paths return `405`. All mutations go through `/_admin`.
- No Teams, bot or chat APIs.
- No search (`/search/query`).
- No sharing links, sensitivity labels, file versions or thumbnails.
- No `$filter`, `$orderby`, `$search`, `$expand` or `$count`. Using them returns `400` with a message naming the option.

## Running it

The image is published to GitHub Container Registry as `ghcr.io/sergey-shulyak/mock-graph-server`. Tags: `latest` (default branch), `vX.Y.Z`, `vX.Y` (version tags) and `sha-<commit>`. Platforms: `linux/amd64` and `linux/arm64`.

**Docker**

```bash
docker run --rm -p 8080:8080 ghcr.io/sergey-shulyak/mock-graph-server:latest

curl -H "Authorization: Bearer app" http://localhost:8080/v1.0/users
```

**Compose**: `compose.yaml` in this repository has a service you can paste into your own file:

```bash
docker compose up mock-graph
```

**From source** (the repo pins .NET with mise):

```bash
mise install
mise exec -- dotnet run --project src/MockGraph.Server
```

With `dotnet run` the server listens on `http://localhost:5066` (from `Properties/launchSettings.json`). In the container it listens on port 8080.

**Custom fixture.** The server reads `MockGraph:FixturePath` (environment variable `MOCKGRAPH__FIXTUREPATH`). The default is `fixtures/sample.yaml`. A relative path is searched for under the content root and then each parent directory. In the image the content root is `/app`, so the default resolves to `/app/fixtures/sample.yaml`. To use your own file:

```bash
docker run --rm -p 8080:8080 \
  -e MOCKGRAPH__FIXTUREPATH=/app/my-fixtures/team.yaml \
  -v "$PWD/my-fixtures:/app/my-fixtures:ro" \
  ghcr.io/sergey-shulyak/mock-graph-server:latest
```

If the fixture is missing or invalid (unknown principal, duplicate id, bad role, missing source file), the server fails at startup and prints the reason.

## Using the Graph SDK

Add the package (the server uses the same version, 6.8.0):

```bash
dotnet add package Microsoft.Graph --version 6.8.0
```

Then point the client at the mock. This is the whole setup. `StaticTokenProvider` is the small class below it, which returns a fixed token:

```csharp
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider("user:alice"));
var adapter = new HttpClientRequestAdapter(auth) { BaseUrl = "http://localhost:8080/v1.0" };
var graph = new GraphServiceClient(adapter);

var me = await graph.Me.GetAsync();   // 401 for an unknown user; 200 for user:alice

internal sealed class StaticTokenProvider(string token) : IAccessTokenProvider
{
    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public Task<string> GetAuthorizationTokenAsync(
        Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(token);
}
```

Inside the Compose network, use `http://mock-graph:8080/v1.0` instead of `localhost`.

**Month 3 (real Microsoft 365):** change `BaseUrl` to `https://graph.microsoft.com/v1.0` and replace `StaticTokenProvider` with a provider that returns a real access token. Nothing else in the Graph calls should change. If it does, that is a mismatch the mock should have caught, so report it.

## Authentication

Send `Authorization: Bearer <token>` on every `/v1.0` request. `/_admin/*` and `/_download/*` need no header.

| Token | Context |
|---|---|
| `app` | App-only. Sees everything, like an application permission. `/me` returns `400` |
| `user:<id>` | Runs as that user. `<id>` is the fixture id (`alice`), the UPN (`alice@contoso-mock.onmicrosoft.com`) or the user's GUID |
| anything else | `401` `InvalidAuthenticationToken` |

A missing header, a non-Bearer scheme or an unknown `user:` also return `401`. Error bodies follow Graph:

```json
{ "error": { "code": "itemNotFound", "message": "...", "innerError": { "request-id": "...", "date": "...", "client-request-id": "..." } } }
```

Every response has a `request-id` header.

## Sample data and visibility

`fixtures/sample.yaml` is the data behind the team's RBAC test matrix. It has five users, six groups and one drive (`kb-docs`). `all-staff` contains the group `engineering` plus `carol` and `dave`. `engineering` contains the groups `eng-backend` and `eng-frontend`, which is a nested-group case.

Users:

| Id | Name | Groups |
|---|---|---|
| `alice` | Alice Backend | `eng-backend` (in `engineering`, `all-staff`) |
| `bob` | Bob Frontend | `eng-frontend` (in `engineering`, `all-staff`) |
| `carol` | Carol HR | `hr`, `all-staff` |
| `dave` | Dave Support | `support`, `all-staff` |
| `erin` | Erin Contractor | none; one document granted directly |

Files a user can open (folders are visible where a readable file is below them; see the note below the table):

| User | Can read |
|---|---|
| `alice` | `/General/*`, `/Engineering/*` (including `/Engineering/Backend/*`), `/Shared/contractor-onboarding.md` |
| `bob` | `/General/*`, `/Engineering/local-dev-setup.md`, `/Shared/contractor-onboarding.md`. Not `/Engineering/Backend/*` |
| `carol` | `/General/*`, `/HR/*`. Not `/Engineering/*` |
| `dave` | `/General/*`, `/Support/*` |
| `erin` | `/Shared/contractor-onboarding.md` only |
| `app` | Everything |

Notes:

- Unreadable items are left out of lists and return `404 itemNotFound` on get, content and permissions.
- A readable file does not make its parent folders listable. `erin` can open the onboarding file by id, but `/Shared` is not in her tree.
- The drive root and site and drive metadata are visible to any valid token.
- `/users` and `/groups` are not filtered.

## Paging

Every collection is paged. The default page size is **5**, so client paging bugs show up early.

- `$top=n` sets the page size, from 1 to `MaxPageSize` (default 999). Values outside the range return `400`.
- `@odata.nextLink` is an absolute URL. It keeps `$top` and `$select` and carries an opaque `$skiptoken`. Follow it until there is none.
- A `$skiptoken` from before a reset is rejected with `400`.

Change the default with `MOCKGRAPH__DEFAULTPAGESIZE` (for example `-e MOCKGRAPH__DEFAULTPAGESIZE=2`). Change the maximum with `MOCKGRAPH__MAXPAGESIZE`.

`$select` is accepted on every endpoint. The response keeps the selected properties, `id` and all `@` annotations, and drops the rest.

Unsupported query options return `400` (for example `$filter`). They are not ignored.

Iterating with the SDK's `PageIterator` works, as in the test suite (`tests/MockGraph.Tests/DirectoryTests.cs`).

## Change tracking

### Drive delta

Drive delta returns the first full enumeration, then only what changed. It works like Graph:

1. Call `GET /drives/{driveId}/root/delta` and follow `@odata.nextLink` until you get an `@odata.deltaLink`.
2. Store the `deltaLink`. Later, call it again to get changes since then.
3. A call with `?token=latest` returns no items and a fresh `deltaLink`. Use it to start from "now".

Deleted items come back as `{ "id": ..., "deleted": { "state": "deleted" }, "parentReference": ... }`, with no name.

Permission changes show up as items in the page. Send `Prefer: deltashowsharingchanges` to also get `"@microsoft.graph.sharedChanged": "True"` on those items. Without the header the item is still reported, just without the annotation.

Here is a sync that runs in the middle of an admin change:

```csharp
// graph is the GraphServiceClient from "Using the Graph SDK", created with "app" or a user token.
var site = (await graph.Sites.GetAsync())!.Value!.Single();          // sample.yaml has one site
var driveId = (await graph.Sites[site.Id].Drives.GetAsync())!.Value![0].Id!;

async Task<(List<DriveItem> Items, string DeltaLink)> SyncAsync(string? link, bool sharing = false)
{
    var items = new List<DriveItem>();
    var builder = graph.Drives[driveId].Items["root"].Delta;
    if (link is not null)
    {
        builder = builder.WithUrl(link);
    }

    while (true)
    {
        var page = await builder.GetAsDeltaGetResponseAsync(r =>
        {
            if (sharing)
            {
                r.Headers.Add("Prefer", "deltashowsharingchanges");
            }
        });
        items.AddRange(page!.Value!);
        if (page.OdataDeltaLink is { } done)
        {
            return (items, done);
        }

        builder = graph.Drives[driveId].Items["root"].Delta.WithUrl(page.OdataNextLink!);
    }
}

// 1. Full enumeration. Keep the deltaLink.
var (_, deltaLink) = await SyncAsync(null);

// 2. Change something through the admin API, e.g. revoke erin:
//    curl -X DELETE http://localhost:8080/_admin/permissions -H 'Content-Type: application/json' \
//         -d '{"itemPath":"/Shared/contractor-onboarding.md","principal":"user:erin"}'

// 3. Incremental sync from the saved deltaLink.
var (changes, nextLink) = await SyncAsync(deltaLink, sharing: true);
foreach (var item in changes)
{
    var shared = item.AdditionalData.ContainsKey("@microsoft.graph.sharedChanged");
    Console.WriteLine($"{item.Id} deleted={item.Deleted is not null} sharedChanged={shared}");
}
// Save nextLink for the next sync.
```

Visibility is part of the diff. If a user loses access to an item, the next user-context delta reports it as `deleted`. If a user gains access, it is reported as an upsert.

### Groups delta

Group membership changes are not in drive delta, so use `GET /groups/delta`. It is plain JSON in the sample below because the SDK call shape is not part of the mock's contract.

Each group carries `members@delta`. Typed members are `#microsoft.graph.user` or `#microsoft.graph.group`. A removed member has `"@removed": { "reason": "deleted" }`.

```csharp
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

using var http = new HttpClient { BaseAddress = new Uri("http://localhost:8080/") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "app");

var url = "/v1.0/groups/delta";
string deltaLink;
while (true)
{
    var page = (await http.GetFromJsonAsync<JsonObject>(url))!;
    foreach (var group in page["value"]!.AsArray())
    {
        foreach (var member in group!["members@delta"]?.AsArray() ?? [])
        {
            var removed = member!["@removed"] is not null;
            Console.WriteLine($"{group["id"]} {member["id"]} removed={removed}");
        }
    }

    if (page["@odata.deltaLink"] is { } link)
    {
        deltaLink = link.GetValue<string>();
        break;
    }

    url = page["@odata.nextLink"]!.GetValue<string>();
}
// Keep deltaLink. Pass it as the next url to get changes. Use "/v1.0/groups/delta?$deltatoken=latest" to start from now.
```

`$select` without `members` removes `members@delta` from the response.

### Tokens and resets

Tokens belong to one server "epoch". The epoch changes when the server starts and on every `POST /_admin/reset`. A token from an older epoch gets:

```json
HTTP 410 Gone
{ "error": { "code": "resyncRequired", "message": "The delta token is no longer valid because the server state was reset. ..." } }
```

When you get `410`, start again without a token.

## Admin API

`/_admin` is not part of Graph. It has no authentication and uses plain JSON, not Graph shapes. Every mutation is reflected in delta.

Principals are `user:<id-or-upn>` or `group:<id>`. `driveId` is optional when the server has one drive, which is true of the sample. Items are addressed by `itemPath` (drive-relative, for example `/General/office-wifi.md`) or by `itemId` (the wire id from Graph responses). Use exactly one of them.

**Grant a permission.** Role is `read`, `write` or `owner`. Granting the same role again changes nothing.

```bash
curl -X POST http://localhost:8080/_admin/permissions \
  -H 'Content-Type: application/json' \
  -d '{"itemPath":"/Shared/contractor-onboarding.md","principal":"user:dave","role":"read"}'
```

**Revoke a permission.** `role` is optional. Without it, all direct grants for that principal on the item are removed. An item with no direct grant for the principal returns `404`.

```bash
curl -X DELETE http://localhost:8080/_admin/permissions \
  -H 'Content-Type: application/json' \
  -d '{"itemPath":"/Shared/contractor-onboarding.md","principal":"user:erin"}'
```

Both return JSON with `changed`, `sequence`, `driveId`, `itemId`, `path` and `created`. `changed` is `false` when nothing changed.

**Add a group member.** The member can be a user or a group. Adding a group that would create a cycle returns `400`.

```bash
curl -X POST http://localhost:8080/_admin/groups/eng-frontend/members \
  -H 'Content-Type: application/json' \
  -d '{"member":"user:erin"}'
```

**Remove a group member.** The member must be a direct member, or the call returns `404`.

```bash
curl -X DELETE http://localhost:8080/_admin/groups/eng-frontend/members \
  -H 'Content-Type: application/json' \
  -d '{"member":"user:bob"}'
```

**Add or replace a file.** Multipart fields: `path` (required), `file` (required), `driveId` (optional). Missing parent folders are created with no permissions. Returns `201` when the file is new and `200` when it replaces one. A folder cannot be replaced with a file.

```bash
curl -X POST http://localhost:8080/_admin/files \
  -F path=/General/new-policy.md \
  -F file=@./new-policy.md
```

**Delete a file or folder.** Deleting a folder deletes its contents. Each deleted item is reported in delta. Query parameters: `path` (required), `driveId` (optional). The drive root cannot be deleted.

```bash
curl -X DELETE "http://localhost:8080/_admin/files?path=/General/new-policy.md"
```

**Reset.** Reloads the fixture, clears the change log and starts a new epoch. All outstanding delta tokens become `410`.

```bash
curl -X POST http://localhost:8080/_admin/reset
# {"status":"reset","epoch":"..."}
```

**Dump state.** Returns the live state in the fixture format (YAML). Save it to a file to reuse it as a fixture.

```bash
curl http://localhost:8080/_admin/state
```

## Writing a fixture

A fixture is one YAML file. Start from `fixtures/sample.yaml`. The top-level keys are `tenant`, `users`, `groups`, `sites`.

```yaml
tenant:
  domain: contoso-mock.onmicrosoft.com      # UPNs and group mail addresses use this domain

users:
  - id: alice                               # stable, human-readable id; used in tokens and principals
    displayName: Alice Backend
    upn: alice@contoso-mock.onmicrosoft.com

groups:
  - id: eng-backend
    displayName: Engineering - Backend
    members: [user:alice]                   # user:<id> or group:<id>; nesting is allowed

sites:
  - id: kb
    displayName: Knowledge Base
    drives:
      - id: kb-docs
        name: Documents
        items:
          - path: /General                  # a folder: no content and no source
            permissions:
              - { principal: group:all-staff, role: read }   # role defaults to read
          - path: /General/office-wifi.md
            content: |                      # text file, UTF-8
              # Office Wi-Fi
              Network: ContosoStaff.
          - path: /General/floor-plan.png
            source: floor-plan.png          # file under fixtures/files/ (or next to the fixture)
          - path: /Engineering/Backend
            breakInheritance: true          # ignore grants from ancestors for this subtree
            permissions:
              - { principal: group:eng-backend, role: write }
```

Rules:

- **Ids.** Users, groups, sites and drives use the fixture id (`alice`, `eng-backend`, `kb`, `kb-docs`). These ids are what you put in tokens and principals.
- **Wire ids.** Graph ids are deterministic and derived from the fixture ids. Users and groups are UUIDv5 values of `user:<id>` and `group:<id>`. Drive ids look like `b!...`. Item ids are `01` followed by hex. Your code should read ids from responses, not compute them. The admin API and fixture lookups accept fixture ids for users, groups, sites and drives, and UPNs for users.
- **Content.** An item has at most one of `content` (text), `source` (a file, relative to the fixture directory or to its `files/` subdirectory) or `contentBase64` (binary inline; this is what `/_admin/state` writes for non-text files). An item with none of them is a folder.
- **Paths.** Drive-relative, case-insensitive, with or without the leading slash. Parent folders that are not listed are created implicitly, with no permissions of their own.
- **Permissions.** Each has `principal` (`user:<id>` or `group:<id>`) and `role` (`read`, `write` or `owner`; default `read`). Grants on a folder apply to everything below it.
- **Inheritance.** `breakInheritance: true` on an item stops grants from above it applying to that item and its descendants, as in SharePoint. The item's own grants still apply.
- **Validation.** Unknown principals or group members, duplicate ids or paths, unknown roles and missing source files stop the server at startup with an error that names the problem.

Use `GET /_admin/state` to see how the server stored a fixture.

## Known differences from Microsoft Graph

These are known and deliberate, or not yet implemented:

- **Hashes.** `file.hashes` has `sha1Hash` and `sha256Hash` only. There is no `quickXorHash`, which SharePoint returns.
- **Addressing.** `root:/path:` addressing works. `/items/{id}:/path:` does not.
- **Folder fields.** `size` is the total of the readable descendants. `childCount` counts readable children only. Real Graph counts everything.
- **Visibility of folders.** Intermediate folders are not visible just because a file below them is readable. The drive root and site metadata are visible to any valid token.
- **Delta.**
  - Only `Prefer: deltashowsharingchanges` is recognised. `deltashowremovedasdeleted`, `deltatraversepermissiongaps` and `deltaExcludeParent` are not implemented.
  - Timestamp tokens and `hierarchicalsharing` are not implemented.
  - Incremental pages do not include parent folders.
  - No `Preference-Applied` header is returned.
  - Folder-scoped delta reports only that subtree.
- **Permissions.** Only direct grants with the roles `read`, `write` and `owner`. No sharing links.
- **Timestamps.** Fixture items use a fixed base time. Changes made at runtime use the server clock.
- **Auth.** Tokens are not validated. Any `user:<known id>` works.
- **Tenant.** One tenant. Multiple sites and drives are supported.
- **`/me` in app context** returns `400`, as Graph does for application tokens.

## Development

Prerequisites: [mise](https://mise.jdx.dev/) (it installs the .NET SDK pinned in `mise.toml`), or the .NET SDK from `global.json`.

```bash
mise install
mise exec -- dotnet build MockGraph.sln
mise exec -- dotnet test MockGraph.sln
mise exec -- dotnet format MockGraph.sln --verify-no-changes
docker build -t mock-graph-server:dev .
```

Layout:

- `src/MockGraph.Server/` is the server. `Endpoints/` holds the routes, `State/` the in-memory model and change log, `OData/` the query and token handling, `Mapping/` converts to `Microsoft.Graph.Models`.
- `tests/MockGraph.Tests/` runs the server in process with `WebApplicationFactory` and the real `Microsoft.Graph` SDK. These tests are the contract.
- `fixtures/sample.yaml` and `fixtures/files/` hold the sample data.

Conventions:

- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/).
- Branch names are `<area>-<three-word-description>`.
- CI runs build, tests and `dotnet format --verify-no-changes`. A formatting diff fails the build.
- Releases: push a `v*` tag to publish a versioned image to GHCR. Pushes to `main` publish `latest`.

### Contract tests against a real tenant

`tests/MockGraph.ContractTests` is a read-only suite that runs against the mock by default and against real Microsoft Graph when `GRAPH_BASE_URL` and `GRAPH_TOKEN` are set. See [docs/contract-tests.md](docs/contract-tests.md).
