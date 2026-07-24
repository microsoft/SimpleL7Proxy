# Copilot Learnings

- Treat `taxonomy/concepts.json` as the canonical source for documentation concept definitions, defaults, units, reload types, status values, and protocol headers.
- Validate taxonomy-linked docs for behavioral semantics as well as literal setting values; stale descriptions can remain even when defaults match.
- The `docs/nav/*.md` content-brief files treat their question checklists as content contracts, so when filling gaps, keep the original guidance text and add the answer directly under each question for easy review.
- In `docs/nav/*.md`, Quick Answers headings are often duplicated in Full Answers; use `#...-1` anchors in quick links so they jump to the full-answer section instead of self-linking to the quick tile.
- In `test/chat_tester`, the error dashboard behavior is owned primarily by `Components/Shared/Response/ErrorResultPanel.razor`; keep status-text normalization, chart behavior, hover previews, and error-detail modal behavior in that component instead of spreading UI logic into `InvestigatorPage.razor`.
- A focused validation command for chat tester UI changes is `dotnet build test/chat_tester/chat_tester.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`.
- When splitting a Razor page into separate pages, remove old backing members/events in the original page in the same edit to avoid orphaned references that can trigger misleading design-time Razor errors.
- Check that a search command exists before using it as an `if` condition; a command-not-found result can enter the `else` branch and print a false validation success.
