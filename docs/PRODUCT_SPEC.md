# Monitoring XS product specification

Monitoring XS is a private-by-default, native Windows resource monitor organized around logical applications. Its primary promise is that a user can understand what applications are running and what they consume without interpreting raw process infrastructure.

## Personas and modes

- Beginner Mode is the default and presents application names, icons, understandable metrics, cautious warnings, and safe actions.
- Advanced Mode progressively reveals process, performance, GPU, network, file, security, and application details.

## Information architecture

Primary navigation: Running Apps, System Overview, History, Diagnostics, Settings, and About. Dashboard and Portable Apps remain visible placeholders. Selecting an application opens one closable logical-application tab. Pinned tabs survive process exit while retained history exists and reconnect by logical application ID.

## Attribution rules

- Multi-process applications are grouped by identity, installation evidence, metadata, and ancestry.
- Games remain separate from launchers.
- Strongly related IDE helper processes can join an IDE; unrelated tools do not.
- Windows infrastructure is hidden, but user-facing Microsoft applications remain visible.
- Installed and package applications are separate from portable/unregistered applications.
- Services are excluded by default and any opt-in changes are explicit.

## Metric truthfulness

Every value is collected from Windows or marked unavailable. Zero means a successful measurement of zero; it never means collection failure. CPU is normalized to total logical processor capacity. Beginner memory is current working set. Disk is expressed as rates, not an unexplained percentage.

## Initial vertical slice

The first slice discovers running processes, filters infrastructure, attributes/group applications, separates portable applications, samples real CPU and working set, displays a running-app list, opens a logical application tab, renders a real bounded one-minute chart, and supports Beginner/Advanced disclosure.
