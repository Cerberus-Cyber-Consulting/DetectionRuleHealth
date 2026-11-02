# API response fixtures

JSON bodies the client tests parse (no live calls in tests). Tenant, subscription and workspace ids
are fake (`11111111-...`, `22222222-...`).

Until the first live run these are shaped from the Microsoft Learn response examples, not recorded.
After an Update from tenant with "Save raw API responses" on, replace them with the scrubbed raw responses
from `data/<alias>/raw/` (remove tenant ids, user names, workspace names and real queries) and keep
the tests passing.

| File | Call |
|---|---|
| `workspace.json` | ARM `GET .../workspaces/{ws}?api-version=2026-03-01` |
| `sentinel-alertrules-page1.json`, `-page2.json` | ARM `GET .../alertRules?api-version=2025-09-01` (two pages via `nextLink`) |
| `detectionrules.json` | Graph `GET /beta/security/rules/detectionRules` |
| `workspace-tables.json` | ARM `GET .../tables?api-version=2026-03-01` |
| `hunting-schema-tables.json` | Graph `GET /beta/security/getHuntingSchemaTables(workspaceId=...)` |
| `securityincident-query.json` | Log Analytics `POST /v1/workspaces/{id}/query` with `IncidentCollector.SentinelQuery` |
| `graph-incidents.json` | Graph `GET /v1.0/security/incidents?$filter=...&$expand=alerts` |
| `token-success.json` | Entra `POST /{tenant}/oauth2/v2.0/token` (code or refresh grant); `{{id_token}}` is filled by the test with an unsigned JWT, so no token-shaped string is committed |
| `token-invalid-grant.json` | the same, refresh token expired (AADSTS70043): the session ends |
| `token-interaction-required.json` | the same, MFA required (AADSTS50076): the session ends |

The two incident fixtures are constructed, not doc examples: each row exercises one linking rule
(see `IncidentCollectTests`). Keep that coverage when swapping in scrubbed live responses.

`schema/unified-schema.json` is not an API response: it is the table schema of the owner's lab tenant
for the extraction golden test, exported with `rulehealth schema export` (see `schema/README.md`).
