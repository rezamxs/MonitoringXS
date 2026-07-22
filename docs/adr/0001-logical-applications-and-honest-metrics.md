# ADR 0001: Report logical applications and honest metric states

- Status: Accepted
- Date: 2026-07-22

## Context

Windows applications often run as several processes. A raw process list makes the user add those values mentally and can mix helpers with unrelated services. Windows counters also fail for normal reasons such as access control, process exit, warm-up, or unavailable providers.

## Decision

Monitoring XS reports logical user-facing applications. Attribution uses package identity, installed-application evidence, executable metadata, process ancestry, known rules, and explicit user overrides. Services and Windows infrastructure stay out of default application totals.

Every metric carries an availability state. A measured zero is valid data. Missing or incomplete data is shown as `WarmingUp`, `Unavailable`, `AccessDenied`, `Unsupported`, `Error`, or `Partial`; it is never replaced with zero. Partial aggregates are lower bounds.

## Consequences

- Users see one application total instead of a confusing process list.
- Attribution must remain conservative and explain its evidence.
- New collectors must preserve availability and lower-bound semantics through aggregation and UI formatting.
- Destructive actions cannot rely on low-confidence attribution.
