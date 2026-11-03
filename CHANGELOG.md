# Changelog

All notable changes to Detection Rule Health are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/). The release workflow copies the section of a version
into its GitHub Release.

## [Unreleased]

## [0.1.0-beta.2] - 2026-11-03

### Changed

- The documentation site moved to <https://detectionhealth.cerberuscyberconsulting.com>. Links in
  the app, README, container image label, release notes and setup script point there; the privacy
  statement names the new address.

### Fixed

- Styles, scripts and images on the documentation site did not load on the custom domain.

## [0.1.0-beta.1] - 2026-11-02

First public beta.

### Added

- Overview of Microsoft Sentinel analytics rules and Defender XDR custom detection rules in one place.
- Efficacy per rule: incidents created and the true positive, benign positive, false positive,
  undetermined and open split, with counts next to every rate.
- Value checks: literal values in rule queries are extracted with Microsoft's KQL parser; you choose
  which values must always occur, and the app checks that they still do in your data. This includes
  values in `graph-match` conditions of Exposure Management rules (`SourceNode.NodeLabel == "device"`).
  Standard Log Analytics columns such as `Type` and `_ResourceId` are known on every workspace table,
  so rules that use them no longer show a false bind error.
- Container image for linux/amd64 and linux/arm64 on GitHub Container Registry, signed, with SBOM
  and provenance.
- Browser sign-in (authorization code with PKCE) with your own app registration (recommended), or
  without one through Microsoft's own apps, Microsoft Graph Command Line Tools and Microsoft Azure
  PowerShell (quick start). The setup guide explains what each choice means for consent. Tokens stay
  in memory only in both cases.
- Setup wizard: app registration details, sign-in, workspace selection and first data collection.
  A tenant ID can be entered as a domain (`contoso.onmicrosoft.com`). The wizard picks up where you
  left off.
- Data collection shows each stage as it runs. A source your account may not read (for example no
  Microsoft Sentinel role) is reported with the missing role or permission; the other sources are
  still collected.
- Settings page per tenant: display name, incident and validation windows, workspaces, sign-in and
  sign-out, collect again, and remove the tenant with its data.
- Demo data to explore the app before connecting a tenant. It is read-only.
- Daily check for a newer release on GitHub (turn off with `RULEHEALTH_UPDATE_CHECK=false`).
- Documentation site, privacy statement and security policy.

[Unreleased]: https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth/compare/v0.1.0-beta.1...HEAD
[0.1.0-beta.1]: https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth/releases/tag/v0.1.0-beta.1
