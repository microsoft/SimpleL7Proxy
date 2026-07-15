# Copilot Learnings — SimpleL7Proxy

This file records lessons learned, conventions, and best practices discovered during Copilot sessions on this repository. Read this file at the start of every session before making changes.

---

## Documentation Structure

- The primary documentation folder is `docs/`. It contains ~60 documents covering 10 taxonomy domains defined in `taxonomy/concepts.json`.
- The machine-readable taxonomy lives at `taxonomy/concepts.json` and defines domains (`d01`–`d10`), subdomains, concepts, settings cross-references, and response code mappings.
- `docs/TABLE_OF_CONTENTS.md` is a concept-level exhaustive index.
- `docs/Readme-domain.md` is the audience-oriented navigation entry point (created 2026-07-15).
- Troubleshooting guides live in `docs/troubleshooting/`. Code internals docs live in `docs/code/`.
- POC guides (POC-*.md) live at the docs/ root. A recommendation exists to move them to `docs/poc/`.

## Naming Convention for docs/

Core docs use UPPERCASE_WITH_UNDERSCORES.md (e.g., CIRCUIT_BREAKER.md, LOAD_BALANCING.md).
The following files violate this convention (tracked in doc-recommendations.txt):
  - AsyncOperation.md (should be ASYNC_OPERATION.md)
  - design.md (should be DESIGN.md)
  - Glossary.md (should be GLOSSARY.md)
  - StorageBlobConfig.md (should be ASYNC_BLOB_STORAGE.md)

## Known Duplicate / Internal-Only Files

These files exist but should not be treated as authoritative or public-facing:
  - `docs/OVERVIEW copy.md` — duplicate of OVERVIEW.md; recommended for deletion
  - `docs/BRANCH_CHANGES_vs_feature_async.md` — internal branch diff; recommended for deletion
  - `docs/troubleshooting/TROUBLESHOOTING_TODO.md` — internal backlog; recommended to move to .github/

## Taxonomy Domains (from taxonomy/concepts.json)

| ID | Name |
|----|------|
| d01 | Request Lifecycle |
| d02 | Backend Management |
| d03 | Reliability |
| d04 | Request Governance |
| d05 | Async Mode |
| d06 | Observability |
| d07 | Configuration Management |
| d08 | Authentication and Security |
| d09 | Deployment Architecture |
| d10 | Protocol and Headers |

## Deployment Facts

- ACA ingress must target port 8000.
- Backend hosts are configured via Host1..Host9 as connection strings with a probe path (not bare URLs).
- Minimum required settings: Port, Workers, and at least one Host1 with `host=` and `probe=` keys.

## Key Configuration Facts

- Warm settings reload live via Azure App Configuration (Sentinel key change triggers reload within ~30s, no restart).
- Cold settings require a container restart.
- Async mode has a three-level opt-in: proxy-wide flag + user profile `async-config` block + per-request `S7PAsyncMode` header.
- Max 9 backend hosts (Host1–Host9).
- Circuit breaker state is per-instance and in-memory (not shared across instances).

## Code Build & Test

- Project is .NET 9/10.
- Standard dotnet commands apply: `dotnet build`, `dotnet test`.
- Source lives in `src/SimpleL7Proxy/`.

## Session History

### 2026-07-15
- Reviewed all ~60 docs in docs/ folder.
- Created `docs/Readme-domain.md`: audience-oriented navigation organized by taxonomy domain.
- Created `doc-recommendations.txt`: 20 specific recommendations for hierarchy improvements, naming, content gaps, and cross-linking.
- Taxonomy domains from concepts.json used as the organizing principle throughout.
