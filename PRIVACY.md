# Privacy statement

*Detection Rule Health — effective for version 0.1.0-beta.1 and later, last updated 2026-11-01.*

Detection Rule Health is software you run yourself. Cerberus Cyber Consulting BV ("we") publishes
the source code and the container image. **We do not operate any service for it, we do not receive
any of your data, and we cannot see how or where you use it.**

## The short version

- The app runs in a container on your own computer and is only reachable from that computer
  (when started as documented, `127.0.0.1:5180`).
- It reads data from **your** Microsoft tenant with **your** sign-in, and stores the results in
  **your** Docker volume. You sign in either through **your own** app registration (recommended) or
  through Microsoft's own public apps (quick start); see [Sign-in options](#sign-in-options).
- Nothing is sent to us. There is no telemetry, no analytics, no crash reporting, no account.
- Sign-in tokens are kept in memory only and are gone when the container stops.
- Once a day the app asks GitHub whether a newer release exists. You can turn that off.

## What the app processes and where it stays

| Data | Source | Where it is kept | How long |
|---|---|---|---|
| Analytics rules and custom detection rules (names, queries, settings) | Microsoft Sentinel (Azure Resource Manager), Microsoft Defender XDR (Microsoft Graph) | `/data/<tenant>/rules.json` in your volume | until you delete it |
| Table and column names of your workspace and advanced hunting schema | Microsoft Graph, Azure Resource Manager | `/data/<tenant>/schema.json` | until you delete it |
| Incidents: id, number, title, severity, status, classification, created and closed time, related rule ids | Log Analytics (`SecurityIncident`), Microsoft Graph | `/data/<tenant>/incidents.json`, `rule-stats.json` | until you delete it |
| Values extracted from the rule queries (table, column, compared value, the comparison text) and parse errors | calculated locally from the rules | `/data/<tenant>/candidates.json` | until you delete it |
| Value-check results: whether a value occurred, match count, table row count, last seen time, and the KQL that was run | your workspace and advanced hunting | `/data/<tenant>/validation.json` | until you delete it |
| Your decisions (which values to validate), rules you marked OK with their note, issue categories, and investigation notes with the author name you typed | you, in the app | `/data/<tenant>/selections.json` | until you delete it |
| Raw API responses, **only when you tick *Save raw API responses*** for a run (troubleshooting): the full response bodies of that run, which can include incident and alert details, plus the request URLs | Microsoft Graph, Azure Resource Manager, Log Analytics | `/data/<tenant>/raw/<time of the run>/` | until you delete the folder |
| Tenant id, sign-in option, app registration (client) id when you use your own, workspace ids and names, display name | you, in the setup wizard | `/data/<tenant>/tenant.json` | until you delete it |
| Signed-in user's name (UPN) and tenant id from the sign-in | Microsoft Entra ID | container memory only | until sign-out or container stop |
| Access and refresh tokens | Microsoft Entra ID | container memory only, **never written to disk or logs** | until sign-out or container stop |
| Update check result: newest stable and newest release (version, release URL, release date, first 500 characters of the release notes), time of check, ETag | GitHub | `/data/.app/update-check.json` | overwritten daily |

Every file in a tenant folder keeps one previous version next to it (`*.bak`), so a failed write never
loses data; removing the tenant deletes those too.

Incident titles can contain personal data (some rules put a user or device name in the title).
The app copies only the fields listed above, and only into your own volume. Alerts are read to link
incidents to custom detection rules; only the rule link is kept (unless you save raw API responses for
that run, see the table).

Container logs (`docker logs`, which your container runtime keeps on your computer) contain one line
per request (method, path, status), the start and end of each job with the tenant's alias, and error
messages. They do not contain tokens, authorization codes, query results, incident details, workspace
names or the signed-in account. The step-by-step log of a job is kept in memory and shown in the app.

## Sign-in options

Both options use the same sign-in in your browser (authorization code with PKCE), read only, with
your own account and its roles. In both, **tokens stay in container memory only**; neither uses the
Azure CLI, PowerShell modules or any other token cache.

| | Your own app registration (recommended) | Microsoft's apps (quick start) |
|---|---|---|
| Which app signs in | the app registration you created in your tenant | Microsoft Graph Command Line Tools (Microsoft Graph) and Microsoft Azure PowerShell (Microsoft Sentinel, Log Analytics), Microsoft's public client apps |
| What consent covers | this tool only; you can limit it to assigned users and delete it when done | Microsoft's apps: a consent for the security permissions also applies to everyone's use of Graph PowerShell (`Connect-MgGraph`) in your tenant |
| What your sign-in logs show | sign-ins to your app registration | sign-ins to "Microsoft Graph Command Line Tools" and "Microsoft Azure PowerShell" from a browser |

The quick start depends on what Microsoft allows these apps to do; Microsoft can change that (its
guidance for tools that use Microsoft's app IDs is to register their own), and some organisations
block these apps with Conditional Access. A token for Graph Command Line Tools can also carry
permissions consented to that app earlier for other work; Detection Rule Health only sends the
read requests listed in the setup guide. A consent given to Graph Command Line Tools stays until an
administrator removes it; Microsoft's app itself cannot be deleted. For regular or shared use,
create your own app registration.

## Network connections the app makes

| Host | When | Why |
|---|---|---|
| `login.microsoftonline.com` | sign-in, token refresh, tenant lookup in the wizard | Microsoft Entra ID sign-in |
| `graph.microsoft.com` | data collection, validation | Defender XDR rules, incidents, alerts, advanced hunting |
| `management.azure.com` | data collection, workspace discovery | Microsoft Sentinel rules and workspaces |
| `api.loganalytics.azure.com` | data collection, validation | queries in your Log Analytics workspace |
| `api.github.com` | at start-up and once every 24 hours, unless disabled | check for a newer release |

Calls to Microsoft are made with your delegated sign-in, inside your own tenant, and are subject to
your Microsoft agreements. The update check sends no tenant data: GitHub receives your public IP
address and a `User-Agent` header with the app version (for example `DetectionRuleHealth/0.1.0-beta.1`),
as with any request to GitHub; [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)
applies to that request. Turn it off with the environment variable `RULEHEALTH_UPDATE_CHECK=false`.

Downloading the image from GitHub Container Registry (`ghcr.io`) is done by your container runtime,
not by the app, and is also covered by GitHub's privacy statement.

## Who is responsible for the data

Because the app runs entirely under your control, **you (or your organisation) decide what data is
processed and for what purpose**. Under the GDPR, that makes you the controller for the data the app
processes. We are not a processor: we have no access to the data and process none of it on your behalf.
If you run the app for a customer's tenant, agree with that customer how their data in your volume
is handled, as you would for any other tool you run.

## Deleting data

- One tenant: **Settings → Remove tenant** in the app (deletes `/data/<tenant>/`).
- Everything: `docker rm -f detectionrulehealth` and `docker volume rm drh-data`.
- Tokens: sign out, or stop the container.
- To revoke the app's access in your tenant: with your own app registration, delete it (or its
  enterprise application) in Microsoft Entra ID. With Microsoft's apps, remove the permissions you
  consented to from the *Microsoft Graph Command Line Tools* enterprise application (Permissions);
  remember that other Graph PowerShell users in your tenant may rely on them.

## The documentation website

The documentation site (`cerberus-cyber-consulting.github.io/DetectionRuleHealth`) is hosted by
GitHub Pages. It uses no cookies, analytics or third-party scripts, and loads no external fonts.
GitHub may log technical data such as IP addresses when serving it; see GitHub's privacy statement.

## Issues and contributions

Anything you post in a GitHub issue or pull request is public. Do not include tenant ids, user
names, workspace names, rule queries of customers, incident details or screenshots of real data.

## Changes

Changes to this statement are listed in the [changelog](https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth/blob/main/CHANGELOG.md) and dated at the top.
If a new version starts calling a new host or storing a new kind of data, this statement is updated
in the same release.

## Contact

Cerberus Cyber Consulting BV, Belgium — <https://cerberuscyberconsulting.com>.
Questions about this statement: open a GitHub issue (no tenant data, please). Security problems:
see the [security policy](https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth/blob/main/SECURITY.md).
