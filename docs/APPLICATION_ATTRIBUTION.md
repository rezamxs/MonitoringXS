# Application attribution

The attribution engine maps process instances to logical applications and is independent of the UI.

## Evidence

Evidence may include executable path, package identity/family, AppUserModelID, version metadata, publisher/signature, installation records, main-window ownership, ancestry, creation time, command line/working directory when safely available, known rules, and user overrides.

Each result contains a logical ID, display name, installation disposition, executable set, confidence, and human-readable reason. A process may also be hidden or unresolved.

## Initial rules

- Always hide known critical/session/service infrastructure names.
- Preserve known user-facing Microsoft apps such as Terminal, Notepad, Calculator, Edge, PowerShell, Store apps, and developer tools.
- Group known multi-process products by stable executable identity.
- Attribute generic helpers to a known parent only with ancestry evidence.
- Keep game executables separate from launcher identities, even when parented by the launcher.
- Treat non-system executables outside recognized install roots as portable/unregistered until catalog evidence says otherwise.

## Confidence

- Certain: package/user override or exact verified product rule.
- High: strong executable/installation identity with supporting metadata.
- Medium: main-window identity plus plausible path/metadata.
- Low: weak heuristic; it must not authorize destructive actions.

Classification is cached by executable identity and invalidated when file identity or user mappings change. Contributors add narrow rules with positive and negative tests rather than editing UI code.
