# Schema fixture

`unified-schema.json` is the table schema the extraction golden test binds the HybridBrothers rules
against (`tests/RuleHealth.Tests/GoldenExtractionTests.cs`, skill `kql-literal-extraction`).

- **Source:** the owner's own lab tenant, collected 2026-10-19 (workspace tables through
  Azure Resource Manager + the advanced hunting schema through Microsoft Graph), exported 2026-10-30 with
  `rulehealth schema export --tenant lab --out tests/Fixtures/schema/unified-schema.json`.
- **Content:** Microsoft table names, column names and types only: 876 tables, 27967 columns, including
  the Log Analytics standard columns (`Type`, `_ResourceId`, ...) on the 867 workspace tables, which the
  export adds the same way a collect does since 2026-10-30.
  No tenant id, workspace names, plans or data. Custom log tables (`_CL`), search-job results
  (`_SRCH`) and restored tables (`_RST`) are left out (11 tables).
- **Not third-party:** no schema file from another project is used or committed (CLAUDE.md).

To refresh it: collect the lab tenant in the UI, run the export again, re-generate
`samples/hybridbrothers/candidates.json` (see the skill) and update the numbers in the golden test.
