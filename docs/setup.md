---
title: Setup guide
eyebrow: Get started
lede: Everything you need before the first run — the container runtime, the app registration, permissions, roles and network access — and how to upgrade or remove the app later.
description: Install Detection Rule Health with Docker or Podman, create your own app registration and grant the right permissions.
---

## How it fits together

Detection Rule Health is a single container. You open it in your browser at
`http://localhost:5180`. When you sign in, your browser goes to Microsoft Entra ID and comes back
to the container with an authorization code; the container exchanges it for tokens and keeps them
in memory. From then on the container calls Microsoft Graph, Azure Resource Manager and Log
Analytics on your behalf and writes the results to a volume on your computer.

```
your browser ──▶ http://localhost:5180 ──▶ container (rulehealth)
     │                                         │
     └─▶ sign-in: login.microsoftonline.com     ├─▶ Microsoft Graph
                                                ├─▶ Azure Resource Manager
                                                ├─▶ Log Analytics
                                                └─▶ volume drh-data (/data)
```

There is no Cerberus service in between and no Cerberus app registration. You sign in either with
an app registration you create yourself (recommended) or with Microsoft's own public apps (quick
start): see [choose how to sign in](#choose-how-to-sign-in).

## Requirements

| Need | Details |
|---|---|
| Computer | Windows 10/11, macOS or Linux, x64 or Arm64, 2 GB free memory |
| Container runtime | one of the [options below](#container-runtime) |
| Browser | any current browser on the same computer |
| Microsoft tenant | Microsoft Sentinel and/or Microsoft Defender XDR |
| To create the app (own app registration only) | Application Developer (or "users can register applications" allowed) |
| To grant consent (both sign-in options) | Global Administrator, Privileged Role Administrator, or Cloud Application Administrator / Application Administrator (enough for delegated permissions like these) |
| To sign in | Security Reader + Microsoft Sentinel Reader (see [roles](#roles-for-users)) |

## Container runtime

Any OCI-compatible runtime works. Pick what your organisation allows:

| Runtime | Platforms | Licence note |
|---|---|---|
| [Docker Desktop](https://docs.docker.com/desktop/) | Windows, macOS, Linux | Free for personal use, education, non-commercial open source and small businesses (fewer than 250 employees **and** less than USD 10 million annual revenue). Larger organisations need a paid subscription. |
| [Podman Desktop](https://podman-desktop.io/) | Windows, macOS, Linux | Free and open source. Use `podman` instead of `docker` in the commands. |
| [Rancher Desktop](https://rancherdesktop.io/) | Windows, macOS, Linux | Free and open source. Choose the *dockerd (moby)* engine to use the `docker` commands unchanged. |
| Docker Engine | Linux, or Ubuntu in WSL2 on Windows | Free and open source. In WSL2, `localhost:5180` is forwarded to Windows by default. |

{: .note}
Check your organisation's policy before installing a container runtime on a managed device.

## Start the container

```sh
docker run -d --name detectionrulehealth \
  -p 127.0.0.1:5180:5180 -v drh-data:/data \
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  --restart unless-stopped \
  ghcr.io/cerberus-cyber-consulting/detectionrulehealth:beta
```

What each part does:

| Option | Why |
|---|---|
| `-p 127.0.0.1:5180:5180` | Only your own computer can reach the app. **Keep `127.0.0.1:`** — without it, anyone on your network could open it. The port must be 5180: the sign-in redirect URI points to it. |
| `-v drh-data:/data` | Results are stored in the Docker volume `drh-data` and survive upgrades. |
| `--read-only --tmpfs /tmp` | The container cannot change its own files; only `/data` and a temporary `/tmp` are writable. |
| `--cap-drop ALL --security-opt no-new-privileges` | No Linux capabilities and no privilege escalation. The app already runs as a non-root user. |
| `:beta` | The latest beta. Pin a version such as `:0.1.0-beta.1` if you prefer. |

Or with Docker Compose, using the
[`compose.yaml`](https://github.com/{{ site.repository }}/blob/main/compose.yaml) from the repository,
which sets the same options:

```sh
docker compose up -d
```

Podman (rootless) works with the same command; for Compose use `podman compose up -d`.

## Choose how to sign in

You choose per tenant in the setup wizard. Both use the same browser sign-in (authorization code
with PKCE), both are read-only, and in both **tokens stay in memory only**: neither uses the Azure
CLI, PowerShell modules or a token cache on disk.

| | Your own app registration (recommended) | Microsoft's apps (quick start) |
|---|---|---|
| Setup | create an app registration once ([below](#create-the-app-registration), about a minute with the script) | none |
| Signs in as | your app registration | *Microsoft Graph Command Line Tools* for Microsoft Graph and *Microsoft Azure PowerShell* for Microsoft Sentinel and Log Analytics: the apps behind `Connect-MgGraph` and `Connect-AzAccount` |
| Sign-ins | one | two in a row (one per Microsoft app); the second is usually one click |
| Consent | applies to this tool only; you can require user assignment and delete the app when done | is given to Microsoft's apps, so it also applies to everyone's use of Graph PowerShell in your tenant |
| Depends on | Microsoft Entra ID only | what Microsoft preauthorizes for those apps (Microsoft can change it), and on your Conditional Access policies allowing them |
| Good for | regular use, shared use, customer tenants | a first look in your own or a lab tenant |

**Quick start, consent.** The security permissions (`CustomDetection.Read.All`, `ThreatHunting.Read.All`,
`SecurityIncident.Read.All`, `SecurityAlert.Read.All`) need admin consent for *Microsoft Graph
Command Line Tools*, by a Global, Privileged Role, Cloud Application or Application Administrator.
In the wizard that administrator uses **Sign in and consent**; Microsoft shows the consent prompt.
**Consent on behalf of your organization** creates a tenant-wide grant on Graph Command Line Tools:
from then on everyone's `Connect-MgGraph` in your tenant can get these read permissions (still
limited by each user's own roles). Only tick it if that is acceptable; otherwise create your own app
registration. Do not use the **Grant admin consent** button on the Graph Command Line Tools enterprise
application for this: it can replace permissions others already rely on. Azure PowerShell needs no
extra consent.

**Quick start, what Microsoft says.** Microsoft builds its own apps for its own tools; when another
tool uses their app IDs, Microsoft's guidance is to "move to another app ID they register". The quick
start therefore only works within what Microsoft allows these apps, may stop working when Microsoft
changes them, and is subject to your Conditional Access policies (a policy on *Windows Azure Service
Management API* also covers the Azure PowerShell sign-in and Log Analytics). Your sign-in logs show
sign-ins to both Microsoft apps from a browser; tell your SOC if they watch for that. Your own app
registration is the supported way.

## Create the app registration

For the recommended sign-in option. The app signs in with an app registration **you create in your
own tenant**. It is a public client:
no secret, no certificate. It only asks for delegated, read-only permissions, so it can never do more
than the signed-in user may do.

| Setting | Value |
|---|---|
| Supported account types | Accounts in this organizational directory only (single tenant) |
| Platform | **Mobile and desktop applications** (not *Web*, not *Single-page application*) |
| Redirect URI | `http://localhost:5180/auth/callback` |
| Allow public client flows | No (not needed) |
| Certificates & secrets | none |

### Option A: script (Azure CLI)

Requires [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell)
and the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli). The script uses the
Azure CLI only to call Microsoft Graph with your sign-in, and looks up every permission by name.

```powershell
az login --tenant <your-tenant>.onmicrosoft.com
pwsh ./New-DrhAppRegistration.ps1 -RestrictToAssignedUsers
```

| Parameter | Effect |
|---|---|
| `-RestrictToAssignedUsers` | Recommended. Only users and groups assigned to the enterprise application can sign in; you are assigned automatically. |
| `-GrantAdminConsent` | Grants admin consent directly (needs an admin role). Without it, the script prints the consent link. |
| `-DisplayName` | Name of the app registration. Default: *Detection Rule Health*. |
| `-TenantId` | Safety check that the Azure CLI is signed in to the tenant you expect. |

Download: [`tools/New-DrhAppRegistration.ps1`](https://github.com/{{ site.repository }}/blob/main/tools/New-DrhAppRegistration.ps1).
Read it before you run it.

### Option B: Microsoft Entra admin center

1.  Go to **Entra ID → App registrations → New registration**. Name: *Detection Rule Health*.
    Supported account types: **Accounts in this organizational directory only**. Leave Redirect URI
    empty and select **Register**.

2.  Copy the **Application (client) ID** and **Directory (tenant) ID** from the Overview page. You
    enter both in the setup wizard.

3.  **Authentication → Add a platform → Mobile and desktop applications**. Under *Custom redirect
    URIs* enter `http://localhost:5180/auth/callback` and select **Configure**. Leave *Allow public
    client flows* on **No**.

4.  **API permissions → Add a permission**, choose **Delegated permissions** each time, and add:

    - **Microsoft Graph**: `CustomDetection.Read.All`, `ThreatHunting.Read.All`,
      `SecurityIncident.Read.All`, `SecurityAlert.Read.All`
    - **Azure Service Management** (under *Microsoft APIs*): `user_impersonation`
    - **Log Analytics API** (under *APIs my organization uses*, search "Log Analytics API"): `Data.Read`

    You can remove the default `User.Read` permission; it is not used.

5.  Select **Grant admin consent for &lt;your tenant&gt;** (or ask an administrator) and confirm that
    every permission shows *Granted*.

6.  Recommended: **Enterprise applications → Detection Rule Health → Properties → Assignment
    required = Yes**, then add the users or groups that may use the app under **Users and groups**.
{: .steps}

## Permissions

| API | Delegated permission | What the app does with it |
|---|---|---|
| Microsoft Graph | `CustomDetection.Read.All` | read Defender XDR custom detection rules |
| Microsoft Graph | `ThreatHunting.Read.All` | read the advanced hunting schema; run validation queries on Defender XDR tables |
| Microsoft Graph | `SecurityIncident.Read.All` | read incidents and their classification |
| Microsoft Graph | `SecurityAlert.Read.All` | link alerts in incidents to custom detection rules |
| Azure Service Management | `user_impersonation` | list subscriptions and Sentinel workspaces; read analytics rules and workspace tables |
| Log Analytics API | `Data.Read` | read `SecurityIncident`; run validation queries on workspace tables |

At sign-in the app also requests `openid`, `profile` and `offline_access` (your name, and a refresh
token that stays in memory). Every request is read-only.

## Roles for users

Delegated permissions never exceed what the signed-in user may do. The user also needs:

| Where | Role | Needed for |
|---|---|---|
| Microsoft Defender XDR | **Security Reader** (Entra role), or a Microsoft Defender XDR unified RBAC role with read access to security data (incidents, alerts, advanced hunting, detection rules) | custom detections, incidents, advanced hunting |
| Each Sentinel workspace | **Microsoft Sentinel Reader** (includes Log Analytics read) | analytics rules, `SecurityIncident`, validation queries |
| Subscriptions | Reader is enough to list them; Sentinel Reader on the workspace also lists it | workspace discovery in the wizard |

A user with only one of the two works too: the app collects what it can and tells you which part it
could not read and why.

## Network access

The container needs outbound HTTPS (443) to these hosts. Nothing else.

| Host | Purpose |
|---|---|
| `ghcr.io` (and `pkg-containers.githubusercontent.com`) | downloading the image (your container runtime, not the app) |
| `login.microsoftonline.com` | sign-in, token refresh, and looking up a tenant ID from a domain in the wizard |
| `graph.microsoft.com` | Defender XDR rules, incidents, alerts, advanced hunting |
| `management.azure.com` | Sentinel rules, workspaces and tables |
| `api.loganalytics.azure.com` | workspace queries |
| `api.github.com` | daily update check (optional, see below) |

Behind a proxy, pass it to the container: `-e HTTPS_PROXY=http://proxy.example:8080`.
If your organisation inspects TLS, see [troubleshooting]({{ '/troubleshooting/#corporate-proxy-or-tls-inspection' | relative_url }}).

## Configuration

Set with `-e NAME=value` on `docker run` or under `environment:` in `compose.yaml`.

| Variable | Default | Meaning |
|---|---|---|
| `RULEHEALTH_UPDATE_CHECK` | `true` | `false` turns off the daily check for a newer release on GitHub |
| `RULEHEALTH_LOG_LEVEL` | `info` | `debug` also logs why an update check failed |
| `HTTPS_PROXY`, `NO_PROXY` | none | outbound proxy |

The port (5180), data folder (`/data`) and listen address are fixed in the image.

## Upgrade

The app shows a notice when a newer release exists. It never updates itself.

```sh
docker pull ghcr.io/cerberus-cyber-consulting/detectionrulehealth:beta
docker rm -f detectionrulehealth
# run the same docker run command as before; your data stays in the drh-data volume
```

With Compose: `docker compose pull && docker compose up -d`.
Release notes: [GitHub Releases](https://github.com/{{ site.repository }}/releases).

## Verify the image

Release images are built by GitHub Actions in the public repository, signed with Sigstore (keyless)
and published with SBOM and build provenance. To check that an image really came from this
repository's release workflow, with the [GitHub CLI](https://cli.github.com/):

```sh
gh attestation verify oci://ghcr.io/cerberus-cyber-consulting/detectionrulehealth:beta \
  -R Cerberus-Cyber-Consulting/DetectionRuleHealth
```

## Remove

```sh
docker rm -f detectionrulehealth      # the app
docker volume rm drh-data             # all collected data
```

Then remove the app's access to your tenant:

- **Own app registration:** delete it in Microsoft Entra ID (**App registrations → Detection Rule
  Health → Delete**).
- **Microsoft's apps (quick start):** remove the permissions you consented to from the *Microsoft
  Graph Command Line Tools* enterprise application (**Enterprise applications → Microsoft Graph
  Command Line Tools → Permissions**). Other Graph PowerShell users in your tenant may rely on them,
  so check first.
