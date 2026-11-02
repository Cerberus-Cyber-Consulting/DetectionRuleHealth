---
title: Troubleshooting
eyebrow: Help
lede: The most common problems, by symptom. Still stuck? Open an issue on GitHub with the error code and the relevant log lines, without tenant data.
description: Fix sign-in errors, port conflicts, permissions and data collection problems in Detection Rule Health.
---

## First, look at the logs

```sh
docker ps --filter name=detectionrulehealth      # is it running, and healthy?
docker logs --tail 100 detectionrulehealth       # what did it say?
```

The logs show one line per request and when a job starts and ends, never tokens, query results or
tenant details. What a job did step by step is in the app: **Data** shows each run with its log.
For a failing update check, recreate the container with `-e RULEHEALTH_LOG_LEVEL=debug`.

## The page does not load

| Symptom | Cause and fix |
|---|---|
| `ERR_CONNECTION_REFUSED` | The container is not running. `docker start detectionrulehealth`, then check `docker logs`. |
| `docker run` fails with *port is already allocated* or *address already in use* | Something else uses port 5180. Find it (`netstat -ano \| findstr :5180` on Windows, `lsof -i :5180` on macOS/Linux) and stop it. The app must use 5180, because the sign-in redirect URI points to it. |
| You opened `http://127.0.0.1:5180` and land on `localhost:5180` | Expected. Sign-in only works on `localhost`, so the app redirects there. |
| *Misdirected request (421)* | You opened the app by another host name or IP address. Use `http://localhost:5180`. |
| Works on the host, not from another computer | By design: the port is published on `127.0.0.1` only. The app is meant for the computer it runs on. |
| WSL2: works inside WSL, not in the Windows browser | Make sure `localhostForwarding` is not disabled in `.wslconfig`, then `wsl --shutdown` and start again. |

## Sign-in errors

Microsoft shows these codes on its sign-in page or sends them back to the app.

| Code | Meaning | Fix |
|---|---|---|
| `AADSTS50011` | The redirect URI does not match. | In the app registration, under **Authentication → Mobile and desktop applications**, add exactly `http://localhost:5180/auth/callback`. |
| `AADSTS9002327` | The redirect URI is registered as *Single-page application*. | Remove it there and add it under **Mobile and desktop applications**. |
| `AADSTS7000218` | The redirect URI is registered under *Web*, which expects a secret. | Move it to **Mobile and desktop applications**. Do not add a secret. |
| `AADSTS700016` | Application not found in this tenant. | Check the client id and tenant id in **Settings** of the app; the app registration must be in the tenant you sign in to. |
| `AADSTS90002` | Tenant not found. | Use the **Tenant ID** (Entra ID → Overview), not a user's object ID, an app's client ID or a subscription ID. A domain such as `contoso.onmicrosoft.com` also works. |
| `AADSTS65001` / `AADSTS90094` / `AADSTS90095` | Consent is missing, or your tenant requires admin approval. | An administrator grants admin consent: **API permissions → Grant admin consent**, or the consent link from the script. |
| `AADSTS650057` | The app registration does not list one of the APIs the app asks for. | The app names the missing one. Add it under **API permissions → Add a permission** (delegated, see [permissions]({{ '/setup/#permissions' | relative_url }})), then grant admin consent. |
| `AADSTS50105` | You are not assigned to the app. | Assignment is required on the enterprise application: add your user or a group under **Users and groups**. |
| `AADSTS53003` | Blocked by Conditional Access. | Talk to your identity team; the policy blocks this app, device or location. |
| `AADSTS50076` / `AADSTS50079` | MFA is required or must be registered. | Complete MFA and try again. |
| *Signed in to a different tenant* | Your account belongs to another tenant than the one configured. | Use **Sign out**, then choose the right account in the account picker. |
| `AADSTS65002` | Microsoft's apps (quick start): Microsoft does not allow that app to call this API. | Microsoft changed what its apps may do, or your tenant restricts them. Use your own app registration (**Settings → Change sign-in**). |
| `AADSTS65001` with Microsoft's apps | Nobody consented the security permissions for *Microsoft Graph Command Line Tools*. | An administrator signs in through the wizard once and consents; or use your own app registration so the consent covers only this tool. |
| `AADSTS90094` with Microsoft's apps | Admin approval needed: nobody consented for Graph Command Line Tools yet. | An administrator uses **Sign in and consent** in the wizard (or your tenant's admin consent workflow). |
| `AADSTS7000112` | Your organisation disabled Graph Command Line Tools or Azure PowerShell. | Use your own app registration. |
| `AADSTS50105` with Microsoft's apps | Your organisation allows Graph Command Line Tools or Azure PowerShell only for assigned users. | Ask for access, or use your own app registration. |
| *Signed in with two different accounts* | Microsoft's apps: the second sign-in (Azure PowerShell) used another account than the first. | Sign in again and pick the same account both times. |

After an administrator consented with the consent link, the browser returns to
`localhost:5180/auth/callback` (with Microsoft's apps: `localhost:5180/`). If the app was not
running at that moment the page fails to load, but the consent is saved. Start the app and sign in.

## Data collection

| Symptom | Cause and fix |
|---|---|
| Stage *Defender XDR custom detections* fails with 403 | Missing `CustomDetection.Read.All` consent, or the user lacks a Defender XDR role with read access. See [roles]({{ '/setup/#roles-for-users' | relative_url }}). |
| Stage *Incidents* fails with 403 | Missing `SecurityIncident.Read.All` consent (Defender) or Sentinel Reader on the workspace (Sentinel). |
| No workspaces listed in the wizard | The user cannot see the subscription, or Sentinel is not enabled on any workspace they can read. Grant Microsoft Sentinel Reader on the workspace. |
| *Signed out* during a long collection | The refresh failed, often because a Conditional Access sign-in frequency policy expired. Sign in again; collected stages are kept. |
| Many rules have bind errors in Value checks | The rule uses tables or columns the tenant's schema does not have (a connector not enabled, a custom table, a watchlist). The rule may fail at runtime too; check it. |
| A value shows *table empty* | The table had no rows in the window: connector not configured, data not retained, or a Basic/Auxiliary log table that analytics rules cannot use. |
| Validation is slow or throttled | Advanced hunting allows about 45 calls per minute per tenant. The app waits and retries; large rule sets take a while. |

## Data volume and permissions

The app runs as user id 1654, not as root.

- A **named volume** (`-v drh-data:/data`, the default in the docs) just works.
- A **bind mount** of a host folder (`-v /srv/drh:/data`) needs that folder writable for uid 1654:
  `sudo chown -R 1654:1654 /srv/drh`. With rootless Podman add `:U`: `-v ./drh:/data:U`.
- On Windows, prefer the named volume: bind mounts from the Windows file system are slow.

## Corporate proxy or TLS inspection

Pass the proxy to the container:

```sh
docker run ... -e HTTPS_PROXY=http://proxy.example:8080 -e NO_PROXY=localhost,127.0.0.1 ...
```

If a TLS-inspecting proxy re-signs traffic with a corporate root certificate, the container does not
trust it by default. Mount a PEM bundle that contains the public root certificates **and** your
corporate root, and point to it:

```sh
docker run ... -v /path/ca-bundle.pem:/etc/ssl/drh/ca-bundle.pem:ro \
  -e SSL_CERT_FILE=/etc/ssl/drh/ca-bundle.pem ...
```

Better: ask for an exception for the hosts in the [network list]({{ '/setup/#network-access' | relative_url }}).

## Update notice

- **No notice although a new version exists**: the check runs at start-up and every 24 hours, only
  offers betas to beta users, and stays silent when GitHub cannot be reached or rate-limits your
  network (60 anonymous requests per hour per IP address, shared with everything else on that address).
- **Turn it off**: recreate the container with `-e RULEHEALTH_UPDATE_CHECK=false`.

## Reset

```sh
docker rm -f detectionrulehealth && docker volume rm drh-data
```

Then start again with the [quick start]({{ '/quick-start/' | relative_url }}). To remove a single
tenant, use **Settings → Remove tenant** in the app.
