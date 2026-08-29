# Public Beta Readiness Checklist

## Identity

- [x] Authoritative version source in Directory.Build.props
- [x] Display version: 0.9.0-beta.1
- [x] Numeric version: 0.9.0
- [x] Public Beta channel label
- [x] AppIdentity exposes version from assembly metadata
- [x] No hardcoded version strings in XAML or C# views

## About experience

- [x] About page accessible from navigation
- [x] Product name, display version, Public Beta badge
- [x] Short product description
- [x] Platform information (Windows 10 / Windows 11)
- [x] Open-source status and MIT license
- [x] Privacy summary and diagnostics statement
- [x] Repository link (authoritative)
- [x] Copyright (authoritative)

## What's New

- [x] Localized What's New section (en-US + fa-IR)
- [x] Completed features only — no System Overview UI claim
- [x] User-facing language, no internal jargon

## Beta Limitations

- [x] Localized Beta Limitations section (en-US + fa-IR)
- [x] Truthful metric state descriptions
- [x] Chart gap explanation
- [x] No fake data guarantee
- [x] Balanced tone — not dangerously unstable

## Localization

- [x] All user-visible strings use localization system
- [x] en-US and fa-IR resource parity
- [x] English fallback
- [x] Versions remain LTR
- [x] URLs remain LTR
- [x] Technical identifiers remain LTR

## Documentation

- [x] docs/RELEASE_NOTES.md created
- [x] docs/BETA_CHECKLIST.md created
- [x] No false claims about publication or download availability

## Tests

- [x] Focused tests for version, About content, localization parity
- [x] No weakened existing tests

## Safety

- [x] No MSI ProductCode or UpgradeCode changes
- [x] No installer behavior changes
- [x] No metric collector changes
- [x] No process attribution changes
- [x] No System Overview UI claimed as complete