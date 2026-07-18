# Contributing

Thank you for improving Monitoring XS. Keep changes focused, preserve the dependency boundaries in `AGENTS.md`, and never add fabricated production metrics.

1. Open or reference an issue for behavior changes.
2. Add tests for attribution, retention, safety, or metric semantics.
3. Run restore, Release build, and tests from the repository root.
4. Update the relevant documentation when contracts change.
5. Keep diagnostics free of sensitive command lines, tokens, and network credentials.

New application-specific attribution rules should be evidence-based, extensible, and accompanied by positive and negative tests. Do not special-case an executable solely by publisher.
