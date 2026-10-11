# Implementation plan

Spec: `/CLAUDE.md`. Product background: `docs/project-context.md`. This file fixes the
architecture so each milestone can be implemented independently.

## Tooling

- .NET SDK comes from mise (`mise.toml`). Run every dotnet command as `mise exec -- dotnet ...`.
- `mise exec -- dotnet build`, `mise exec -- dotnet test`, `mise exec -- dotnet format --verify-no-changes`
  must all be clean at the end of each milestone.
- Packages already referenced: `Microsoft.Graph` 6.x, `YamlDotNet`, `Microsoft.AspNetCore.Mvc.Testing`, xUnit.

## Layout

```
src/MockGraph.Server/
  Program.cs                 wiring only; `public partial class Program` for WebApplicationFactory
  Options/MockGraphOptions   FixturePath (MOCKGRAPH__FIXTUREPATH, default fixtures/sample.yaml),
                             DefaultPageSize (default 5), MaxPageSize (999)
  Fixtures/                  YAML DTOs + FixtureLoader (YamlDotNet, camelCase)
  State/                     domain records, GraphStore (state + change log), Ids, AccessEvaluator
  Auth/                      CallerContext (App | User), auth middleware
  OData/                     query option parsing/validation, paging, $select, skip/delta token codec
  Serialization/             Kiota writer -> JSON, GraphError results
  Mapping/                   domain -> Microsoft.Graph.Models (User, Group, Site, Drive, DriveItem, Permission)
  Endpoints/                 DirectoryEndpoints, SitesEndpoints, DriveEndpoints, DeltaEndpoints, AdminEndpoints
tests/MockGraph.Tests/       WebApplicationFactory + real GraphServiceClient (the contract)
tests/MockGraph.ContractTests/  (M5) read-only suite runnable against mock OR real tenant via env
fixtures/sample.yaml, fixtures/files/
```

## Design decisions

**Serialization.** Every Graph response body is a `Microsoft.Graph.Models` object (or a
`*CollectionResponse`) written with `Microsoft.Kiota.Serialization.Json.JsonSerializationWriter`.
Annotations that the model has no property for (`@odata.context`, `@odata.type` on directoryObjects,
`@microsoft.graph.downloadUrl`, `@microsoft.graph.sharedChanged`, `members@delta`, `@removed`) go
through `AdditionalData`. No hand-written JSON for Graph entities. Content type
`application/json;odata.metadata=minimal;odata.streaming=true;IEEE754Compatible=false;charset=utf-8`.
`$select` is applied after serialization on the JSON tree: keep selected properties plus `id`-less
strictness is not needed — keep every `@odata.*` / `@microsoft.graph.*` annotation and the selected names.

**IDs.** Fixture ids are human-readable (`alice`, `eng-backend`, `kb`, `kb-docs`). Wire ids are
deterministic: users/groups = UUIDv5 of `"user:alice"` / `"group:eng-backend"` in a fixed namespace;
site id = `"{tenantHost},{guid},{guid}"`; drive id = `"b!" + base64url(guid bytes x2)`; item id =
`"01" + 32 upper-hex chars` from a hash of `"{driveId}:{lowercased path}"`; permission id = hash of
item + principal + role. All in `State/Ids.cs`. Every lookup accepting an id also accepts the fixture
id (and UPN for users) so tests and `user:alice` tokens stay readable. `mail` = UPN.

**State.** `GraphStore` is a singleton guarding everything with one lock. It holds the current model
and an append-only change log with a monotonically increasing sequence number, plus an `epoch` (new
GUID on every load/reset). Change kinds: ItemUpserted, ItemDeleted, ItemSharingChanged,
GroupMemberAdded, GroupMemberRemoved. All admin mutations go through GraphStore methods that update
the model and append to the log in the same critical section. Granting/revoking on a folder marks the
folder and every inheriting descendant as sharing-changed.

**Folders.** A fixture item with neither `content` nor `source` is a folder. Missing parent folders
(e.g. `/Shared`) are created implicitly with no permissions of their own. The drive root is a folder
with no permissions.

**Access.** `AccessEvaluator`: effective permissions of an item = its own grants + grants inherited
from ancestors up to (and excluding everything above) the nearest `breakInheritance: true` node.
A user can read an item if any effective grant names the user or a group the user is a transitive
member of (cycle-safe). App context sees everything. In a user context:
- items the user cannot read are omitted from lists and return `404 itemNotFound` on get/content/permissions;
- the drive root and site/drive metadata are visible to any authenticated caller (children are still
  filtered), so a user with access to one deep file gets an empty-looking tree but can open the file by id
  and sees it in delta. Intermediate folders are NOT made visible by a readable descendant;
- directory endpoints (users, groups) are not filtered.

**Auth.** `Authorization: Bearer app` or `Bearer user:<fixtureId|upn|guid>`. Anything else -> 401
`InvalidAuthenticationToken`. Unknown user -> 401 too. `/me` in app context -> 400 `BadRequest`
("/me request is only valid with delegated authentication flow."). `/_admin/*` and the download
redirect target need no auth.

**Errors.** One helper producing `{error:{code,message,innerError:{"request-id","date","client-request-id"}}}`
plus `request-id` response header. Codes mirror Graph: `Request_ResourceNotFound` (directory),
`itemNotFound` (drives/sites), `BadRequest`, `InvalidAuthenticationToken`, `resyncRequired` (410).
Unmatched Graph routes and non-GET methods on `/v1.0` -> Graph-shaped 404/405, never ASP.NET's default body.

**OData.** Allowed options per endpoint are declared explicitly; anything else (`$filter`, `$expand`,
`$orderby`, `$search`, `$count`, unknown) -> 400 `BadRequest` naming the option. `$top` 1..MaxPageSize.
Paging uses an opaque `$skiptoken` (base64url JSON: offset + epoch); `@odata.nextLink` is an absolute
URL built from the incoming request's scheme/host/path and preserves `$top`/`$select`. `@odata.context`
is emitted on every response, built from the request base URL (`{base}/v1.0/$metadata#users` etc.).

**Content.** `GET .../content` -> `302` with `Location: {base}/_download/{opaqueToken}`; the token
encodes drive+item+epoch and needs no auth (like Graph's pre-authenticated URLs). Driveitems for files
also carry `@microsoft.graph.downloadUrl`. Content type from file extension. `file.hashes`, `size`,
`eTag`, `cTag`, `createdDateTime`, `lastModifiedDateTime`, `parentReference` (driveId, driveType
`documentLibrary`, id, path `/drives/{id}/root:/...`, siteId), `webUrl` are populated. Timestamps: a
fixed fixture base time for seeded items (deterministic), `TimeProvider` for runtime mutations.

**Delta tokens.** Opaque base64url JSON `{k: kind, e: epoch, s: sequence, o: offset}`. Initial
enumeration snapshots at sequence S and pages with nextLinks (`o`), finishing with a deltaLink at S.
A deltaLink call returns the de-duplicated set of entities touched in (S, now], paged the same way.
Token from another epoch (after `/_admin/reset` or restart) -> `410 Gone` `resyncRequired`.
`token=latest` -> empty `value` + deltaLink at current sequence.
- Drive delta (`/drives/{id}/root/delta`, also `/items/{rootId}/delta`): first page starts with the
  root item; deleted items carry `deleted: {state:"deleted"}` and minimal fields. With
  `Prefer: deltashowsharingchanges`, items whose effective permissions changed carry
  `"@microsoft.graph.sharedChanged": "True"` and the response echoes `Preference-Applied`. Without the
  header, sharing-only changes are not reported. In a user context an item that became unreadable is
  reported as deleted. **Verify annotation name/value and header behaviour against the Graph docs
  (driveitem-delta) before implementing.**
- Groups delta (`/groups/delta`): initial sync returns each group with `members@delta`
  (`[{ "@odata.type":"#microsoft.graph.user"|"#microsoft.graph.group", "id": ... }]`); incremental
  returns changed groups with added members and removed ones as `{..., "@removed":{"reason":"deleted"}}`.
  Uses `$deltatoken` / `$skiptoken` query parameters like real Graph. **Verify against group-delta docs.**

**Admin API.** As listed in CLAUDE.md, plain JSON (System.Text.Json, camelCase), no Graph shapes.
Principals/members are `user:<id>` / `group:<id>`. Group-membership cycles are rejected with 400.
`GET /_admin/state` emits YAML in the fixture format, so it can be saved and reloaded.

## Implementation notes (M1)

- Fixture path: a relative `FixturePath` is resolved against the content root, then each parent directory
  (`FixtureLoader.ResolvePath`); in Docker the content root is `/app`, so `/app/fixtures` is found directly.
- `$select` always keeps `id` and all `@` annotations. Unmatched `/v1.0` GET -> 404 `Request_ResourceNotFound`;
  non-GET -> 405 `BadRequest`. `/me/memberOf` and `/me/transitiveMemberOf` are also served.
- Domain records (`State/Records.cs`) already hold sites, drives and items; no endpoints use them yet.

## Implementation notes (M2)

- `GraphModel` gained lookups (`FindDrive/FindSite/FindItem/FindItemByPath/ParentOf/ChildrenOf/DescendantsOf`) and a
  virtual root (`RootOf`, path `/`, id from `Ids.Item(driveId, "/")`, never stored in `Items`). `ItemRecord` gained
  `Modified` and `Version` (init props, for eTag/cTag; M3 bumps them with `with`).
- `AccessEvaluator` is per-request over one model snapshot; `FileMapper.For(ctx, store)` bundles snapshot, caller,
  evaluator and base URL. Collection/Entity helpers moved to `Endpoints/GraphResults`.
- `file.hashes` carries `sha1Hash` and `sha256Hash` only; `quickXorHash` (what SharePoint returns) is not computed.
  Do not rely on it until it is added and verified against known vectors.
- Folder `size` = sum of readable descendants' bytes; `childCount` counts readable children only.
- Path addressing `root:/a/b:`, `root:/a/b:/content|children|permissions` is supported; `/items/{id}:/path:` is not.
- Children/content on the wrong item kind (children of a file, content of a folder) -> 404 `itemNotFound`.
- Permission ids are hashed from the *source* item (where the grant lives), so an inherited permission keeps
  the id it has on the ancestor. `grantedToV2` identities carry `email` in AdditionalData (UPN for users,
  `{groupKey}@{tenantDomain}` for groups).
- Fixture: added `/General/floor-plan.png` (`source:` binary under `fixtures/files/`); visibility table unchanged.

## Fixture note

`fixtures/sample.yaml` had `/Engineering/Backend` inheriting `group:engineering` from `/Engineering`,
which contradicted its own expected-visibility table ("bob: NOT Backend/*"). `breakInheritance: true`
was added to `/Engineering/Backend`. The table at the bottom of the fixture is the test oracle.

## Milestones (one commit each, Conventional Commits)

1. **Skeleton** — options, fixture loader, GraphStore (read side), Ids, auth, error format, OData
   parsing + paging + `$select`, Kiota serialization, users/groups/me/memberOf/transitiveMemberOf/
   members/transitiveMembers. SDK tests incl. `PageIterator` over 5-item pages, `$select`, 401, 404, 400.
2. **Sites & files** — sites, drives, root, children, items, content (302), permissions with
   `inheritedFrom`, AccessEvaluator, user-scoped visibility. Tests assert the fixture's visibility table
   for all five users plus app.
3. **Change tracking** — change log, drive delta, groups delta, the whole `/_admin` API. Tests: full
   sync -> mutate via admin -> incremental sync for every mutation kind, incl. the erin revocation
   scenario, `token=latest`, reset -> 410.
4. **Packaging** — multi-stage Dockerfile (`mcr.microsoft.com/dotnet/sdk:10.0` -> `aspnet:10.0`,
   `--platform=$BUILDPLATFORM` + `-a $TARGETARCH`, port 8080, fixture baked in at `/app/fixtures`,
   overridable by volume), `.dockerignore`, `compose.yaml`, GitHub Actions: `ci.yml` (build, test,
   `dotnet format --verify-no-changes`) and `publish.yml` (buildx amd64+arm64 -> GHCR on main/tags),
   student-facing `README.md`.
5. **Contract suite** — `tests/MockGraph.ContractTests`: read-only, shape-level tests using only the
   SDK. Target chosen by `GRAPH_BASE_URL` / `GRAPH_TOKEN` / `GRAPH_DRIVE_ID`; with no env it boots the
   mock in-process. Documented in README.
