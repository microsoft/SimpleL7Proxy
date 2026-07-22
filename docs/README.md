# SimpleL7Proxy Documentation

Use this page to find the shortest path from evaluating SimpleL7Proxy to operating or contributing to it.

## TL;DR

- New users start with [Getting Started](getting-started/README.md).
- Operators use [How-to Guides](how-to/README.md), [Reference](reference/README.md), and [Troubleshooting](troubleshooting/README.md).
- Contributors use [Concepts](concepts/README.md) and [Contributing](contributing/README.md).

## Choose Your Goal

| Goal | Start here |
|------|------------|
| Run the proxy for the first time | [Getting Started](getting-started/README.md) |
| Understand the architecture and request flow | [Concepts](concepts/README.md) |
| Complete an operational task | [How-to Guides](how-to/README.md) |
| Look up an exact setting, header, or behavior | [Reference](reference/README.md) |
| Validate behavior with a runnable scenario | [Proofs of Concept](pocs/README.md) |
| Diagnose a failure | [Troubleshooting](troubleshooting/README.md) |
| Build, test, or contribute | [Contributing](contributing/README.md) |

## Documentation Model

**Each topic has one canonical page; other pages link to it instead of repeating it.**

```text
README → getting-started → how-to → reference
                    ├──→ concepts
                    ├──→ pocs
                    └──→ troubleshooting
```

> [!NOTE]
> [`../taxonomy/concepts.json`](../taxonomy/concepts.json) is the machine-readable source for concept relationships, settings, units, defaults, statuses, and protocol headers.

> [!TIP]
> Files under [`internal/`](internal/) are documentation-maintenance notes, not product guidance.
