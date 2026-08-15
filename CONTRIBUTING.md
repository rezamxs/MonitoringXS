# Contributing

Thank you for improving Monitoring XS. Keep changes focused, preserve the dependency boundaries in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), and never add fabricated production metrics.

## Product contract

- Monitoring XS is a native Windows 10 (1809+) and Windows 11 application-centric resource monitor.
- Show logical user-facing applications, not raw Windows infrastructure processes.
- Never fabricate production metrics. Represent unavailable data explicitly.
- Keep services excluded from application totals unless the user explicitly enables the advanced option.
- Keep portable and unregistered applications separate from installed applications.

## Reporting issues

- **Bugs** — use the [Bug Report](https://github.com/rezamxs/MonitoringXS/issues/new?template=bug_report.yml) template.
- **Feature requests** — use the [Feature Request](https://github.com/rezamxs/MonitoringXS/issues/new?template=feature_request.yml) template.
- **Security vulnerabilities** — see [SECURITY.md](SECURITY.md). Do not report security issues in public GitHub Issues.

## Getting started

1. Open or reference an issue for behavior changes.
2. Add tests for attribution, retention, safety, or metric semantics.
3. Run restore, Release build, and tests from the repository root:

   ```powershell
   dotnet restore MonitoringXS.sln
   dotnet build MonitoringXS.sln -c Release --no-restore
   dotnet test MonitoringXS.sln -c Release --no-build
   ```

4. Update the relevant documentation when contracts change.
5. Keep diagnostics free of sensitive command lines, tokens, and network credentials.

New application-specific attribution rules should be evidence-based, extensible, and accompanied by positive and negative tests. Do not special-case an executable solely by publisher.

## Coding conventions

- Follow [`docs/ENGINEERING_STYLE.md`](docs/ENGINEERING_STYLE.md) for naming, file organization, expected failures, logging, and comments.
- Use C# with nullable reference types, implicit usings, deterministic builds, and warnings as errors in CI.
- Prefer immutable records and explicit result types for expected failures.
- Use asynchronous APIs and cancellation tokens for I/O and long-running work.
- Do not use exceptions for expected process-exit, access-denied, or unavailable-counter paths.
- Never block the UI thread with discovery, metadata, storage, or metric collection.
- Treat command lines, paths, network endpoints, and diagnostic exports as sensitive.
- Do not execute strings through a shell when a direct Windows API exists.

## UI and accessibility

- Use the Precision Glass design tokens; do not hardcode ad-hoc colors in views.
- Use native WinUI controls and semantics before custom keyboard/focus behavior.
- Give every interactive control an accessible name and a visible keyboard focus state.
- Do not rely on color alone. Support high contrast, system theme, and reduced motion.
- Keep motion purposeful, generally 120–200 ms, and limited to opacity/transform where possible.
- Avoid continuous decorative animation, large animated blur, and full-tree redraws.

## Performance and safety targets

- Target idle CPU below 1% and working set below approximately 200 MB on a typical modern system.
- Default sampling is approximately one second; metadata is not reloaded on every sample.
- Never terminate critical/protected Windows processes.
- Force-stop requires an explicit warning and confident attribution.
- Do not require permanent elevation, a service, or a kernel driver.
- Sanitize logs and never log secrets or complete sensitive command lines.

## Repository hygiene

Use `git --no-pager` for inspection commands that do not need interactive paging. Never commit terminal, pager, command-help, or redirected diagnostic output.

Before every commit, review the worktree and staged file list:

```powershell
git status --short
git diff --check
git diff --stat
git diff --cached --name-only
git diff --cached --stat
```

Do not use broad `git add -A` without first reviewing the filenames that would be staged. Stage only the intended paths, then repeat the cached-name and cached-stat checks.

Do not commit generated `bin/`, `obj/`, packages, local databases, logs, or diagnostic exports. Update architecture, metric semantics, security, troubleshooting, and milestone documentation when behavior changes. Use stable dependencies only; document any unavoidable exception before adding it.
