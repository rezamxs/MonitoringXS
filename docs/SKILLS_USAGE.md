# Skills usage

## Selected skills

| Skill | Purpose in Monitoring XS |
| --- | --- |
| `ui-skills-root` | Routed the UI task to the smallest relevant local skill set using the repository's UI Skills CLI. |
| `baseline-ui` | Enforces restrained hierarchy, reusable primitives, compact layout, bounded decoration, and honest empty/error states. Web-only Tailwind rules are not applied to WinUI. |
| `fixing-accessibility` | Applied by platform analogy to accessible names, keyboard access, native semantics, focus visibility, dialog safety, and non-color status encoding. HTML-specific rules are translated to WinUI AutomationProperties. |
| `fixing-motion-performance` | Keeps WinUI motion short and compositor-friendly, avoids layout animation and continuous blur, and requires reduced-motion behavior. |

## Skills intentionally not selected

- `improve-ui` is a read-only audit-and-plan workflow and conflicts with the requested implementation.
- `fixing-metadata` applies to HTML/social metadata rather than a native Windows desktop application.
- The Codex Companion skills under `skills/codex-plugin-cc` are internal Claude Code forwarding/result contracts, not implementation guidance for this repository.
- No installed skill specifically covers C#, .NET, WinUI 3, Windows internals, security engineering, Git, or open-source setup. Those areas follow official Microsoft guidance and documented engineering practices.
- Milestone 3A did not select an additional skill because the available session skills cover image generation, OpenAI products, plugins, and skill management rather than Windows ETW network collection.

## Application notes

Skill guidance is subordinate to native Windows conventions and this product contract. For example, HTML ARIA guidance maps to WinUI `AutomationProperties`, and browser animation advice maps to compositor-friendly WinUI transitions. The selected skills influenced the Precision Glass tokens, native control selection, explicit accessible names, reduced-motion stance, and the decision to avoid decorative live animation.

## 2026-07-22 network stabilization

- `fixing-accessibility` was applied directly to the stale application-card accessible name. The WinUI `ListViewItem` now uses a one-way binding to the observable view-model name instead of a one-time container assignment. Keyboard behavior and native list semantics were preserved.
- The requested `context-engineering`, `test-driven-development`, `debugging-and-error-recovery`, `code-review-and-quality`, `performance-optimization`, `documentation-and-adrs`, and `git-workflow-and-versioning` skills were not installed in this session. Their engineering practices were followed manually; they are not claimed as executed skills.

## 2026-07-22 authorship and engineering style

- `fixing-accessibility` was used as a constraint while reviewing authorship and About-related metadata. No control, focus behavior, accessible name, or keyboard path was changed.
- The requested `context-engineering`, `code-review-and-quality`, `documentation-and-adrs`, `code-simplification`, and `git-workflow-and-versioning` skills were not installed in this session. Their names are not recorded as executed skills.
