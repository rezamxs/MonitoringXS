# Codebase Context Evaluation

A/B evaluation of repository context engines for Monitoring XS.
Goal: Select ONE primary context engine to reduce AI agent token waste.

## Candidates

| Candidate | Status | Notes |
|-----------|--------|-------|
| Codebase-Memory MCP | MANUAL SETUP REQUIRED | Read-only repository access recommended |
| Serena MCP | MANUAL SETUP REQUIRED | C# symbol/navigation focus |
| GitNexus | **BLOCKED** | License review required before any evaluation |
| Repomix | KEEP EXPERIMENTAL | Context packaging utility (different purpose) |

## Evaluation Criteria

For each candidate, measure against these Monitoring XS queries:

1. **Localization dependency discovery** — Find all .resw files and LocalizationTests.cs
2. **Installer lifecycle discovery** — Find installer scripts, WiX sources, validation tests
3. **Broker availability flow** — Find PrivilegedBroker, named pipe IPC, broker management scripts
4. **History persistence flow** — Find SqliteMetricHistoryStore, Storage interfaces, migration code
5. **Process Actions end-to-end** — Find ProcessActionService, ViewModel, integration tests

### Metrics Per Query

```
Correct relevant files discovered:  [count]
Missed relevant files:              [count]
Noise files returned:               [count]
Number of retrieval/tool steps:     [count]
Approximate context size:           [KB/tokens if measurable]
Setup complexity:                   Low / Medium / High
Index/update latency:               [seconds if measurable]
Windows compatibility:              Yes / Partial / No
Security permissions:               Read-only / Read-write / Shell / Network
Reliability:                        Consistent / Intermittent / Unreliable
```

## Evaluation Procedure

### Prerequisites

Each MCP server must be installed and configured locally first.
This evaluation cannot be completed objectively without running the tools.

### Codebase-Memory Setup

1. Install per official documentation
2. Configure read-only repository access
3. Add index/cache to `.gitignore`
4. Run each query and record metrics

### Serena Setup

1. Install per official MCP/agent integration docs
2. Verify C# symbol resolution
3. Run each query and record metrics

### GitNexus

**BLOCKED — LICENSE REVIEW REQUIRED**

Do not install until:
- Current license verified
- Commercial/open-source compatibility reviewed
- Local data handling reviewed
- MCP security reviewed
- Maintenance/activity reviewed

## Results

> ⚠️ **PENDING**: This evaluation requires manual setup and execution of each MCP server.
> Results will be populated after each tool is installed and tested.

### Codebase-Memory

| Query | Correct | Missed | Noise | Steps | Size | Notes |
|-------|---------|--------|-------|-------|------|-------|
| Localization | — | — | — | — | — | Pending |
| Installer | — | — | — | — | — | Pending |
| Broker | — | — | — | — | — | Pending |
| History | — | — | — | — | — | Pending |
| Process Actions | — | — | — | — | — | Pending |

### Serena

| Query | Correct | Missed | Noise | Steps | Size | Notes |
|-------|---------|--------|-------|-------|------|-------|
| Localization | — | — | — | — | — | Pending |
| Installer | — | — | — | — | — | Pending |
| Broker | — | — | — | — | — | Pending |
| History | — | — | — | — | — | Pending |
| Process Actions | — | — | — | — | — | Pending |

## Recommendation

> Cannot select a winner without measurement. Do not choose by subjective preference.

After evaluation completes, classify each:

```
ADOPT            — Primary context engine
KEEP EXPERIMENTAL — Available but not mandatory
REJECT           — Does not meet requirements
BLOCKED          — License/security concerns prevent evaluation
```

Repomix remains as a separate context-packaging utility regardless of MCP selection,
because it serves a different purpose (packaging vs. retrieval).