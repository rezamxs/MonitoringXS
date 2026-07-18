# Security architecture

## Trust boundaries

Process metadata, command lines, executable paths, signatures, network destinations, imported mappings, database contents, and elevated-helper requests are untrusted or sensitive inputs.

## Controls

- Run unelevated by default; no service and no kernel driver.
- Do not invoke a shell for application actions.
- Validate executable identity again immediately before destructive or privileged operations.
- Deny actions against critical/protected Windows processes regardless of UI attribution.
- Avoid unsafe DLL search by using system-qualified paths and safe library-loading flags.
- Sanitize logs; omit command-line arguments, credentials, tokens, and network query strings.
- Store history locally and provide deletion controls.
- Exporting advanced diagnostics requires a sensitivity warning and explicit user action.

## Elevated helper design

The helper will accept a versioned request containing only an operation enum and validated identifiers. It will reject unknown fields/operations, verify the caller and target, perform one allow-listed operation, return one structured response, and terminate. General process launch, arbitrary paths, and shell strings are forbidden.

## Reporting

See the root `SECURITY.md` for responsible disclosure. Do not include sensitive diagnostic data in public issues.
