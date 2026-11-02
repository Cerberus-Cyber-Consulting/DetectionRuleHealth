---
title: Quick start
eyebrow: Get started
lede: From nothing to your first overview in about fifteen minutes. The setup guide has the details behind each step.
description: Run Detection Rule Health in Docker, create your app registration and collect your first data.
---

## Before you start

- A container runtime: Docker Desktop, Docker Engine, Podman or Rancher Desktop
  ([which one?]({{ '/setup/#container-runtime' | relative_url }})).
- An administrator who can grant admin consent (may be you). For your own app registration
  (recommended) also permission to create one; without it, use the quick start with Microsoft's apps
  ([compare the options]({{ '/setup/#choose-how-to-sign-in' | relative_url }})).
- An account with **Security Reader** and **Microsoft Sentinel Reader** to sign in with.

## Steps

1.  **Start the container**

    ```sh
    docker run -d --name detectionrulehealth \
      -p 127.0.0.1:5180:5180 -v drh-data:/data \
      --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
      --restart unless-stopped \
      ghcr.io/cerberus-cyber-consulting/detectionrulehealth:beta
    ```

    Using Compose? Save [`compose.yaml`](https://github.com/{{ site.repository }}/blob/main/compose.yaml)
    and run `docker compose up -d`. Using Podman? Replace `docker` with `podman`.

2.  **Create your app registration** (recommended; skip for the quick start)

    With the Azure CLI and PowerShell 7:

    ```powershell
    az login --tenant <your-tenant>.onmicrosoft.com
    pwsh ./New-DrhAppRegistration.ps1 -RestrictToAssignedUsers
    ```

    Download [`New-DrhAppRegistration.ps1`](https://github.com/{{ site.repository }}/blob/main/tools/New-DrhAppRegistration.ps1)
    from the repository. It prints the **tenant id**, **client id** and an admin consent link.
    Prefer the Entra admin center? Follow the [portal steps]({{ '/setup/#option-b-microsoft-entra-admin-center' | relative_url }}).

3.  **Grant admin consent**

    An administrator opens the consent link from the script, or selects **Grant admin consent** under
    **API permissions** of the app registration.

4.  **Open the app and follow the wizard**

    Go to **<http://localhost:5180>**. Choose how to sign in, enter the tenant id (and client id), sign in with Microsoft,
    select your Sentinel workspaces and start the collection. The [first run]({{ '/first-run/' | relative_url }})
    page walks through each screen.
{: .steps}

{: .note}
**Just looking?** Choose **Explore with demo data** on the first screen. No app registration or
sign-in needed.

## Next

- [Pick which values must always occur]({{ '/first-run/#after-the-first-collection' | relative_url }}) and run a validation.
- Something not working? See [troubleshooting]({{ '/troubleshooting/' | relative_url }}).
- Stop the app with `docker stop detectionrulehealth`, start it again with `docker start detectionrulehealth`.
  You sign in again after every start: tokens are never saved.
