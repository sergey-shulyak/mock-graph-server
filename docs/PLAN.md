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
  `"@microsoft.graph.sharedChanged": "True"`. Without the
  header, sharing-only changes are still reported, just without the annotation. In a user context an item that became unreadable is
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

## Implementation notes (M3)

- **History, not replay.** `GraphStore` keeps the append-only `ChangeEntry` log plus one immutable `GraphModel` snapshot per
  change (`GraphModel.Sequence`). Delta diffs `ModelAt(since)` against `ModelAt(target)`, so user-context visibility
  (grant, revoke, group membership change) is evaluated on both sides: unreadable-now-but-readable-before -> `deleted`,
  the reverse -> upsert. Reset clears log and history and starts a new epoch.
- **Tokens** (`OData/DeltaTokens.cs`): base64url JSON `{K kind, E epoch, S synced-seq, T pinned-seq, O offset, I initial, P paging, D drive, Sc scope}`.
  An initial enumeration is pinned at T and read from `ModelAt(T)`, so pages stay consistent while admin mutations happen.
  Wrong epoch (or a sequence in the future) -> `410 resyncRequired`; malformed/foreign-drive token -> 400.
  Links are `.../delta?token=<t>` for both nextLink and deltaLink; `$skiptoken`/`$deltatoken` for groups.
- **Routes served:** `/drives/{id}/root/delta`, `/items/{id}/delta` (the SDK uses `items/root/delta()`), the `()` and
  `(token='x')` function forms, plus `?token=x` / `?token=latest`. Folder-scoped delta only reports that subtree.
- **Verified against the docs:** annotation is `"@microsoft.graph.sharedChanged": "True"` (string). The header is
  `Prefer: deltashowsharingchanges` (docs say it is combined with `deltashowremovedasdeleted, deltatraversepermissiongaps`;
  we only look for `deltashowsharingchanges`). The docs describe no `Preference-Applied` echo, so none is sent.
  Sharing-only changes always appear in drive delta (as real Graph does); the header only adds the sharedChanged
  annotation. An item that becomes visible to a user through a permission/group change is an upsert and carries
  sharedChanged only when the header is sent.
  Deleted items are `{id, deleted:{state:"deleted"}, parentReference:{driveId,id}}` (no name, as SharePoint). The 410 has no `Location` header.
  `deltaExcludeParent`, `hierarchicalsharing` and timestamp tokens are not implemented. Incremental pages do not include parent folders.
- **Groups delta** follows the docs: `members@delta` (direct members, typed, `@removed:{reason:"deleted"}`) is present unless
  `$select` is given without `members`; initial sync lists all members, incremental lists the net added/removed members
  of groups whose membership changed. `$deltatoken=latest` is supported; `$filter` is rejected.
- **Admin API:** errors reuse the Graph error body (`{error:{code,message,innerError}}`) with 400/404. Group cycles -> 400. Re-granting or
  re-adding the same thing is a no-op (`changed:false`, no log entry). Grant of a different role for the same principal replaces it;
  revoke without `role` removes all roles of that principal. `/_admin/state` is YAML in the fixture format (binary or
  whitespace-fragile files as `contentBase64`, accepted by the loader); runtime timestamps and eTag versions are not included.

## Implementation notes (M6)

Connector roadblocks: site discovery, directory `$filter`, `$expand`, SharePoint site groups, `$batch`, throttling. Each was checked
against the v1.0 docs; where Graph differs from the brief the docs won.

- **Sites.** `SiteRecord` gained `Path`, `IsRoot`, `IsImplicit`. Without a `root: true` site the loader inserts an implicit one (key `root`,
  displayName = first label of the tenant domain, path `/`, no drives; not written by `/_admin/state`). `GraphModel.FindSite` accepts wire id,
  fixture key, `root`, the bare host, and `host:/path` (URL-encoded or not; the SDK sends `Sites["host:/sites/kb"]` escaped). `siteCollection`/`root`
  are emitted on the root site only (docs: "available only on the root site"). One catch-all `/sites/{**rest}` serves `GET /sites/{id}` and the
  colon forms because a literal `{siteId}` route next to a catch-all trips ASP0022. The SDK sends `$search=` and `getAllSites()`; the docs show `search=`
  and `getAllSites`; both spellings are routed. Plain `GET /sites` skips the implicit root (existing test/contract expectations); `search=*` and `getAllSites` include it.
  `ODataQuery.Parse` now records accepted extra options in `Extras`, and `Paging.Page` re-emits them in `@odata.nextLink`.
- **Directory query.** `OData/DirectoryQuery.cs`: tiny recursive-descent `$filter` (eq, startsWith, and/or, parentheses), `$orderby`, `$count`.
  Docs (aad-advanced-queries): eq/startsWith on these properties work without advanced mode; `$count=true` without `ConsistencyLevel: eventual` is
  silently ignored (not a 400; only the `/$count` URL segment errors); `$filter`+`$orderby` needs advanced mode (`Request_UnsupportedQuery`). Unsupported filter
  parts are `400 BadRequest` as the task asked. `@odata.count` goes through `AdditionalData`.
- **$expand.** `children` only, on driveItem GETs (item, root, path). The driveItem-list-permissions doc states permissions cannot be expanded on a
  get or a collection, so `$expand=permissions` is a 400 rather than something the mock invents. Expanded children use the same visibility filter.
- **Site groups.** New `PrincipalKind.SiteGroup`, `SiteGroupRecord` (wire id = 3 + index in the site), `GraphModel.SiteGroups`. `AccessEvaluator.PrincipalsOf` adds a
  site group when any member is in the user's user+group closure. Permission mapping emits `grantedToV2.siteGroup {id, displayName, loginName=displayName}`
  and no `grantedTo` (consistent with users/groups; real Graph's deprecated `grantedTo` form for site groups could not be verified). Membership
  mutations log `SiteGroupMemberAdded/Removed` plus one `ItemSharingChanged` per item whose effective grants include the group, so drive delta (app and user callers)
  and sequence numbers stay correct even when no item is affected. No v1.0 Graph endpoint lists classic site-group members (the v1.0 `sharePointGroup` members API is for SharePoint Embedded
  containers), so none was added; the README documents the limitation.
- **Batch.** `BatchMiddleware` runs after auth and before `UseRouting()` (called explicitly in `Program.cs`), builds a `DefaultHttpContext` per sub-request with the caller copied
  from the outer request and runs it through `next`, so routing, endpoints and the Graph error mapping are the real ones. `dependsOn` is implemented (DFS, 424 on failed dependency, 400 on cycles).
- **Throttle.** `ThrottleState` singleton; `ThrottleMiddleware` sits before auth and skips the batch envelope; the batch consumes one per sub-request. Body follows the
  throttling doc (`TooManyRequests`, innerError with code/message/status). Reset clears it.
- **Test edits.** `DirectoryTests.UnsupportedFilter_Is400` used `startswith`, which is now supported; it uses `endsWith` instead.

## Fixture note

`fixtures/sample.yaml` had `/Engineering/Backend` inheriting `group:engineering` from `/Engineering`,
which contradicted its own expected-visibility table ("bob: NOT Backend/*"). `breakInheritance: true`
was added to `/Engineering/Backend`. The table at the bottom of the fixture is the test oracle.

## Company fixture

`fixtures/company.yaml` (fictional company Contoso) is the default fixture of the Docker image; `fixtures/sample.yaml`
stays the server's built-in default (`MockGraphOptions.FixturePath`), so `dotnet run` and every existing test are
unchanged. The image sets `MOCKGRAPH__FIXTUREPATH=fixtures/company.yaml` in the Dockerfile and ships both files. This
was chosen over changing the built-in default because the tests assert the sample's exact item counts and visibility.

- Superset of the sample (same ids, paths, content, permissions); 16 users, 15 groups, 4 sites, 6 drives, 79 files, 41 folders.
- Binary documents (16 files: docx, pdf, xlsx, pptx) are generated by `tools/fixture-files/build_fixture_files.py` from
  plain-text sources next to it, with fixed metadata and zip timestamps so rebuilds are byte-identical.
- `fixtures/company.visibility.yaml` is the RBAC oracle, produced by `tools/fixture-visibility/compute_visibility.py`, an
  independent re-implementation of the CLAUDE.md rules (no shared code with `AccessEvaluator`).
- `tests/MockGraph.Tests/CompanyFixtureTests.cs` serves the fixture, walks every principal's tree through the Graph SDK
  (children plus get-by-id) and compares it with the oracle; it also proves the superset property, the
  `/_admin/state` round trip, drive delta completeness and that every `source:` file downloads with the declared size and
  content type. The first run of the two implementations agreed; no server bug was found.
- The students' guide is `fixtures/company.notes.md`.

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
