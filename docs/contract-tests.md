# Contract tests

`tests/MockGraph.ContractTests` is a read-only, shape-level suite written only against the official
`Microsoft.Graph` SDK (plus raw `HttpClient` through the same bearer token where an annotation must be
checked on the wire). It asserts the Graph contract, not fixture data, so the **same tests run against the
mock and against a real Microsoft 365 tenant**. If a test passes on the mock and fails on the tenant, the
mock is wrong.

It covers: users/groups (required properties, `$top` + nextLink via `PageIterator`, `$select`,
`@odata.type` on members/memberOf so the SDK yields `User`/`Group`), sites, drives, driveItem facets,
content download (`length == size`), permissions shape, drive delta (full enumeration, re-query of the
deltaLink, `token=latest`, `Prefer: deltashowsharingchanges`), groups delta, and the error contract
(`Request_ResourceNotFound`, `itemNotFound`, 401 body). Mock-only features (`/_admin`, `user:` tokens,
the 5-item default page size) are deliberately not used here.

## Run against the mock (default)

```sh
mise exec -- dotnet test tests/MockGraph.ContractTests
```

With no environment variables the mock is booted in-process (`WebApplicationFactory`) and called as `app`.

## Run against a real tenant

You obtain the bearer token yourself (the suite never fetches or prints it). It must be an **app-only**
(application permission) token for `https://graph.microsoft.com`, for example from a client-credentials
flow with an app registration in your tenant.

```sh
export GRAPH_BASE_URL=https://graph.microsoft.com/v1.0
export GRAPH_TOKEN=<access token>
# optional; discovered through the API when omitted (first user / group / site / drive of that site)
export GRAPH_USER_ID=...     # id or UPN
export GRAPH_GROUP_ID=...
export GRAPH_SITE_ID=...     # e.g. contoso.sharepoint.com,<guid>,<guid>
export GRAPH_DRIVE_ID=...    # b!...
mise exec -- dotnet test tests/MockGraph.ContractTests
```

`GRAPH_BASE_URL` and `GRAPH_TOKEN` must be set together. Access tokens expire (about an hour), so
re-export when you see 401s.

### Application permissions (admin consent required)

| Permission | Used for |
| --- | --- |
| `User.Read.All` | users, user by id/UPN, `memberOf` |
| `Group.Read.All` | groups, `groups/delta` |
| `GroupMember.Read.All` | group `members`, `transitiveMembers` |
| `Sites.Read.All` | sites, site drives |
| `Files.Read.All` | drives, items, content, permissions, drive delta |

`Files.Read.All` is documented as sufficient for `/permissions`; if the permissions test is skipped or gets 403 on your
tenant, check the app's consent first.
`/me` is not exercised because it needs a delegated token.

## Reading results

- **Skipped** tests are not failures: the tenant lacks the data (fewer than 2 users/groups/children to
  page, no group with members, no small file, no deleted items). Create that data to cover the test.
- **Failed against the mock**: a regression in the mock. Fix it and add a test to `tests/MockGraph.Tests`.
- **Failed against the real tenant**: the mock's behaviour differs from Graph. File it with the failing test
  name and the message (never paste the token). Check first that it is not a missing permission (401/403 on
  the first call) or an expired token.
- Assertions are intentionally tolerant of real-Graph extras (more properties, package/remoteItem
  facets, site/siteGroup grantees, link permissions, non-empty re-query of a delta link). Each loosening is
  commented in the test source with the reason.
- Delta tests enumerate the whole drive and groups delta, capped at 500 pages. On a large tenant point
  `GRAPH_DRIVE_ID` at a small drive.
