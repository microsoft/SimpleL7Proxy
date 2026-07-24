# Testing Changes

Run the existing project checks that cover the files you changed.

## TL;DR

- Build the affected .NET project.
- Run its existing tests.
- Verify documentation links when changing Markdown.

## Validate Production Code

**Build and test the affected project before submitting a pull request.**

```bash
dotnet build src/SimpleL7Proxy/SimpleL7Proxy.csproj
dotnet test
```

> [!WARNING]
> Do not remove or weaken unrelated tests to make a change pass.

## Validate Documentation

**Confirm that every relative Markdown link and image target exists.**

```bash
git diff --check
git grep -nE '\\[[^]]+\\]\\([^):#]+\\)' -- '*.md'
```

> [!TIP]
> Also compare configuration claims with [`../../taxonomy/concepts.json`](../../taxonomy/concepts.json).
