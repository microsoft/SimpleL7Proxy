# Troubleshooting Backlog

Purpose: Track recommended troubleshooting guides not yet authored.

## Prioritized guide backlog

| Priority | Status | Proposed guide | Primary symptom | Notes |
|----------|--------|----------------|-----------------|-------|
| P1 | DONE | `requests-400-invalid-ttl.md` | `400` with malformed TTL / `InvalidTTL` | Proxy-originated validation failure |
| P1 | TODO | `requests-403-auth-profile.md` | `403` (`DisallowedAppID`, `UnknownProfile`) | AuthAppID / user profile path |
| P1 | TODO | `requests-417-header-validation.md` | `417` missing required header / invalid header | Request validation and header rules |
| P2 | TODO | `requests-408-backend-timeout.md` | `408` backend I/O cancellation / timeout | Distinguish proxy timeout vs backend timeout |
| P2 | TODO | `requests-500-internal-error.md` | `500` unhandled exception / content too large | Include immediate triage signals |
| P2 | TODO | `startup-no-active-hosts.md` | Readiness stays `503`, active hosts `0` | Startup/bootstrap host validation |
| P2 | TODO | `appconfig-label-mismatch.md` | Keys exist but not loaded | `AZURE_APPCONFIG_LABEL` mismatch |
| P3 | TODO | `eventhub-startup-disabled.md` | Event Hub backend silently disabled at startup | File logging continues, Event Hub absent |
| P3 | DONE | `async-202-never-issued.md` | Expected async but always sync | Trigger timeout + header + profile conditions |
| P3 | TODO | `high-latency-without-errors.md` | High queue latency without 5xx | Queue pressure and CB delay band |

## Authoring contract reminder

All new guides should follow the troubleshooting guide contract in `.github/copilot-instructions.md`:
- failure-first
- runtime behavior explained
- operator audience
- diagnosis checklist
- one canonical reproduce/inspect/fix/verify example
