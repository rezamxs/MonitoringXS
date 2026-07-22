# Engineering style

Monitoring XS is maintained by rezaalizadeh (`rezamxs`). The code should be direct, predictable, and honest about Windows limitations. Correct behavior matters more than cleverness.

## Naming and files

- Use file-scoped namespaces under `MonitoringXS.<Layer>`.
- Name types for their domain role. Common suffixes are `Collector`, `Service`, `Reader`, `Snapshot`, `Diagnostics`, and `ViewModel`.
- Keep one main public type per file. Small related records or enums may stay together when splitting them would make the contract harder to follow.
- Keep product terms stable: **logical application**, **Process I/O**, **Physical disk (ETW)**, **Network**, and the names in `MetricAvailability`.

## Failures and logging

- Represent expected states such as process exit, access denied, warming up, unsupported counters, and partial data with typed results. Do not turn them into exceptions or sentinel zeroes.
- Use exceptions for broken invariants and unexpected failures. Preserve cancellation rather than converting it into a generic error.
- Use source-generated `LoggerMessage` methods with stable templates. Do not log complete command lines, paths, endpoints, attribution overrides, or other sensitive values.

## Async work and state

- Keep Windows, storage, discovery, and collection work off the UI thread.
- Pass cancellation tokens through I/O and long-running operations.
- Bound live queues, caches, maps, and history. State the capacity or eviction rule near the owner.
- Keep platform details outside `Core` and keep ViewModels outside collectors.

## Comments and documentation

- Comments explain a reason, invariant, security boundary, or Windows-specific constraint. They do not repeat the next line of code.
- Do not add author headers to source files.
- Record durable design decisions as ADRs. Keep validation records separate and include only commands and results that were actually observed.
- Prefer a small focused change over a cosmetic rewrite of correct code.
