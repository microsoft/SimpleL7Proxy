# Copilot Learnings

- Treat `taxonomy/concepts.json` as the canonical source for documentation concept definitions, defaults, units, reload types, status values, and protocol headers.
- Validate taxonomy-linked docs for behavioral semantics as well as literal setting values; stale descriptions can remain even when defaults match.
- The `docs/nav/*.md` content-brief files treat their question checklists as content contracts, so when filling gaps, keep the original guidance text and add the answer directly under each question for easy review.
- In `docs/nav/*.md`, Quick Answers headings are often duplicated in Full Answers; use `#...-1` anchors in quick links so they jump to the full-answer section instead of self-linking to the quick tile.

