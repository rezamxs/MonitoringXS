# Privacy

Monitoring XS has no telemetry, ads, tracking, remote analytics, or cloud dependency by default. Process names, paths, command lines, network destinations, and history remain local.

Logs are minimal and sanitized. Detailed diagnostic export is user-initiated and warns that process and network information may be sensitive. Settings will include deletion of metric history, cached classifications, and portable mappings.

Network monitoring counts packet bytes from supported Windows kernel TCP/UDP send and receive events. It does not capture packet payloads, inspect TLS, perform deep-packet inspection, resolve addresses to hostnames, display browsing destinations, or persist endpoint history. Local and remote addresses and ports exposed by the underlying event/table formats are deliberately not copied into Monitoring XS models. Only aggregate byte/event counts, process identity, transport/address-family categories, availability, and health diagnostics are retained in memory for the current session.
