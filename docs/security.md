---
title: Security policy
eyebrow: Project
description: 'Supported versions, how to report a vulnerability, and how Detection Rule Health protects you.'
# Generated from SECURITY.md by tools/Sync-DocsPages.ps1. Edit SECURITY.md, not this file.
---

## Supported versions

Detection Rule Health is in public beta. Only the **latest release** receives fixes; there are no
backports. Upgrade with `docker pull ghcr.io/cerberus-cyber-consulting/detectionrulehealth:beta`
and recreate the container (your data volume is kept).

## Reporting a vulnerability

Please report privately through GitHub: **Security → Report a vulnerability** on
<https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth/security/advisories/new>.
Do not open a public issue, and do not include real tenant data in the report.

Useful to include: version, container runtime and OS, steps to reproduce, impact, and whether you
want to be credited.

What to expect (best effort, this is a one-person open source project):

- acknowledgement within 5 working days;
- an assessment and, where needed, a fix plan within 15 working days;
- a GitHub security advisory and a fixed release; credit if you want it.

## Scope

In scope: the app and container image published from this repository, for example token handling,
the local web server (request forgery, Host header handling, path traversal), sign-in flow,
data written to the volume, the release pipeline and image signing.

Out of scope: Microsoft services and APIs, your container runtime, vulnerabilities that need an
attacker who already controls your computer or Docker daemon, and running the app in ways the docs
advise against (for example publishing the port to a network interface other than `127.0.0.1`).

## How the app protects you

- Delegated sign-in with your own single-tenant app registration, authorization code flow with PKCE.
  No client secrets. Tokens are held in memory only and never written to disk or logs.
- The local web server accepts requests only for `localhost:5180`, requires an anti-forgery token
  and a matching `Origin` for every change, sends no CORS headers and uses a strict
  Content-Security-Policy.
- The image runs as a non-root user on a minimal ("chiseled") base without a shell or package
  manager, and the documented run command adds a read-only root filesystem, drops all capabilities
  and blocks privilege escalation.
- Release images are built in GitHub Actions, carry an SBOM and SLSA provenance, are signed with
  Sigstore cosign (keyless) and have a GitHub build attestation. Verify before use:

  ```
  gh attestation verify oci://ghcr.io/cerberus-cyber-consulting/detectionrulehealth:0.1.0-beta.1 \
    -R Cerberus-Cyber-Consulting/DetectionRuleHealth
  ```
