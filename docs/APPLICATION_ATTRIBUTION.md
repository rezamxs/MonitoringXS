# Application attribution

The attribution engine maps process instances to logical applications and is independent of the UI.

## Evidence

Evidence may include executable path, package identity/family, AppUserModelID, version metadata, publisher/signature, installation records, main-window ownership, ancestry, creation time, command line/working directory when safely available, known rules, and user overrides.

Milestone 1 obtains installed Win32 evidence from the per-machine and per-user 32/64-bit uninstall registry views. It obtains packaged application evidence from the current user's MSIX package/app-list catalog and maps running processes by package family, package full name, and AppUserModelID. Broad roots such as `Program Files` are never sufficient directory evidence by themselves.

Each result contains a logical ID, display name, installation disposition, executable set, confidence, and human-readable reason. A process may also be hidden or unresolved.

## Initial rules

- Always hide known critical/session/service infrastructure names.
- Preserve known user-facing Microsoft apps such as Terminal, Notepad, Calculator, Edge, PowerShell, Store apps, and developer tools.
- Group known multi-process products by stable executable identity.
- Attribute generic helpers to a known parent only with ancestry evidence.
- Keep game executables separate from launcher identities, even when parented by the launcher.
- Treat non-system executables outside recognized install roots as portable/unregistered until catalog evidence says otherwise.
- Apply a persistent user override before package, catalog, signature, or heuristic rules. Overrides are keyed by normalized executable path and stored under `%LocalAppData%\MonitoringXS\attribution-overrides.json`.

## Confidence

- Certain: package/user override or exact verified product rule.
- High: strong executable/installation identity with supporting metadata.
- Medium: main-window identity plus plausible path/metadata.
- Low: weak heuristic; it must not authorize destructive actions.

Supporting file/package evidence is cached by stable identity and revalidated or replaced when file identity, catalog snapshots, process instances, or user mappings change. Contributors add narrow rules with positive and negative tests rather than editing UI code.

## Bounded caches

- Win32 and MSIX catalogs retain at most 4,096 entries and refresh after ten minutes; an empty result is cached too.
- Process package identities use a 2,048-entry LRU keyed by PID plus start time.
- Executable metadata uses a 512-entry file-identity LRU and discovery revalidates live visible executable metadata after ten minutes.
- Embedded Authenticode certificate inspection uses a 256-entry file-identity LRU. Certificate presence and signer identity are reported without claiming trust validation.
- Extracted icons use a 128-entry file-identity-and-size LRU and reject payloads over 2 MB.
- Persistent user overrides are limited to 1,024 entries. Capacity failure is explicit and never evicts an existing override silently.

Catalog match indexes are rebuilt only when a catalog snapshot changes. Process-detail caches retain only live PIDs, verify start time on every discovery pass to reject PID reuse, and evict exited processes.
