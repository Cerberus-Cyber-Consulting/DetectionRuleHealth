## What changes for users

<!-- One or two sentences. Add a line to CHANGELOG.md under "Unreleased". -->

## Checks

- [ ] `dotnet test` passes
- [ ] `pwsh tools/Test-BrandCompliance.ps1` passes (UI or docs changes)
- [ ] `pwsh tools/Test-ReleaseReadiness.ps1` passes
- [ ] No tenant data, tokens, customer names or real-tenant screenshots in code, tests, fixtures or docs
- [ ] If this changes what is stored, which hosts are called or which permissions are needed:
      PRIVACY.md, docs/privacy.md and docs/setup.md are updated
