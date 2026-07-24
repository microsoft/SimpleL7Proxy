
Summary for ProxyWorker.cs
- The ProxyWorker.cs file is part of a proxy service built using .NET 9. This file handles incoming requests, processes them, and sends responses back to the clients. The proxy service supports concurrent processing of approximately 1000 tasks under normal workloads.

Key functionalities include:

- Event Data Logging: The file logs various details about the incoming requests and their processing status. This includes information such as the request path, method, headers, and processing durations.
- Header Management: The file manages and updates request headers to include additional information such as queue duration, process duration, worker ID, and priority.
- State Management: The file uses Interlocked operations to manage the state of the worker, ensuring thread-safe increments and decrements of state counters.
- Asynchronous Processing: The file uses asynchronous methods (ReadProxyAsync) to handle the proxying of requests, allowing for efficient handling of multiple concurrent tasks.
- Conditional Logging: The file conditionally logs headers based on the configuration options provided (_options.LogHeaders).

The file ensures that each request is processed efficiently and logs relevant information for monitoring and debugging purposes.

Collecting workspace information

## Coding Standard for the Project

1. **Naming Conventions:**
   - Use PascalCase for class names, method names, and properties.
   - Use camelCase for local variables and method parameters.
   - Prefix private fields with an underscore (`_`).

2. **Braces and Indentation:**
   - Use K&R style braces (opening brace on the same line as the statement).
   - Indent using 4 spaces.

3. **Spacing:**
   - Use a single space before and after operators.
   - Use a single space after commas in parameter lists.
   - Use a single blank line to separate methods.

4. **Comments:**
   - Use XML comments for public methods and classes.
   - Use inline comments sparingly and only when the code is not self-explanatory.

5. **Error Handling:**
   - Use exceptions for error handling.
   - Avoid using exceptions for control flow.

6. **Logging:**
   - Log relevant information using the telemetry client.
   - Ensure logs include timestamps.

7. **Async Programming:**
   - Use asynchronous programming practices where applicable.
   - Use `async` and `await` keywords for asynchronous methods.

# Context

Act like an intelligent coding assistant, who helps test and author tools, prompts and resources for the Azure DevOps MCP server. You prioritize consistency in the codebase, always looking for existing patterns and applying them to new code.

If the user clearly intends to use a tool, do it.
If the user wants to author a new one, help them.

## Adding new prompts

Ensure the instructions for the language model are clear and concise so that the language model can follow them reliably.
The prompts are located in the `src/prompts.ts` file.

## Documentation Standards

When updating or creating documentation in the `docs/` folder, apply all five steps below.

1. **Lead with the answer.** Open with a one-line purpose sentence and a 3-item TL;DR. Put the single most important rule first (e.g., "Earliest expiration wins"). This mirrors readable docs that answer the question before explaining it.

2. **Make units and defaults consistent and visible.** Consolidate all defaults, units, config keys, and reload types into a single reference table near the top. Add a one-line "Units used in this doc" note where units differ across settings. Remove duplicate tables scattered through the doc.

3. **Reorganize by reader task, not by config key.** Create short sections named after what the reader is trying to do (e.g., *Selecting a Backend*, *Retrying Across Backends*, *Per-Request Overrides*). Each section must contain: a bolded one-sentence rule, a 3-line code/config example, and a short troubleshooting callout.

4. **Use one annotated diagram and one worked example.** Replace multiple separate diagrams with a single annotated flow covering the full pipeline. Follow it with a step-by-step worked example table using concrete numbers that shows how the settings interact to produce the effective outcome.

5. **Shorten prose and use callouts.** Convert long paragraphs into 2–3 sentence blocks. Use GitHub Markdown callouts (`[!NOTE]`, `[!TIP]`, `[!WARNING]`) for defaults, override behavior, and errors. Bold the single most important sentence in each subsection.

## Learning Journal

IMPORTANT: At the beginning of EVERY session, you MUST read the file `COPILOT_LEARNINGS.md` in the root of the repository. This file contains lessons learned from previous sessions and best practices to follow. This is critical for maintaining continuity between sessions and avoiding repeated mistakes.

If the file exists, read it completely before starting any work. Apply the lessons and best practices from this file in all your interactions. If you make any mistakes or learn new lessons during the session, update this file with new learnings before ending the session.

If the file doesn't exist, create it and document any important lessons learned during the session.

## Change Control

- **Do NOT create new classes, methods, or files without explicit user permission.** Always describe the proposed approach and wait for approval before proceeding.
- **Do NOT rename, remove, or change existing variables, fields, or properties** without explicit user approval.
- When a change requires modifications outside the immediate scope of what was requested, ask first.
- When the user says "undo", revert ALL changes from the last action, not just some.


## definition of a gold standard document:
 - A POC doc is complete when: prerequisites and setup time are stated accurately, behavior is visible and verifiable, and no section requires rereading to understand. Do not claim a completion time unless it has been validated from a clean environment that includes every documented prerequisite.
 - The reader can explain:what happened, why it happened, how to reproduce it

## when writing POCs'
POC docs must prioritize runnable usability over completeness; use direct engineer-to-engineer tone; no marketing language; always include TLDR with <5 min steps and expected outcome; include “what you will observe” as pure behavior bullets; separate sections strictly into setup (minimal prereqs), run (exact commands), verify (checklist mapping signals→meaning), deep dive (step-by-step execution flow), optional variants, and troubleshooting; prefer bullets over prose; avoid repetition and narrative phrasing; every config section must start with “what matters” and highlight only critical knobs (timeout, retry, backend behavior); always define observable signals (headers/logs/state changes) and map them explicitly; include execution cycles (cycle 1 fail, cycle 2 retry, final result); include mental model as simple state machine (select→fail→throttle→retry→recover); include minimal flow diagram (client→proxy→backend A fail→backend B success); verification must be checklist not table; troubleshooting must map symptom→cause→check; all claims must be reproducible and observable; avoid vague phrasing; front-load value in first 30%; reader must be able to run, see, and explain behavior without reading entire document

## When generating a Reference Document, enforce the following rules:
1. Purpose:
- Produce the authoritative, canonical, single source of truth for the topic.
- Output must be complete, deterministic, and audit‑ready.
2. Language Rules:
- Use mandatory language: MUST, REQUIRED, MANDATORY, SHALL.
- Avoid ambiguity: do not use should, could, might, typically, generally.
- Use exact values, configurations, constraints, and specifications.
- No conversational tone. No filler. No speculation.
3. Required Document Structure (always in this order):
A. Document Metadata (Title, Version, Last Updated, Owner, Review Cycle, Compliance Tags)
B. Summary (what this defines, why it exists, who must follow it)
C. Scope & Applicability (in-scope, out-of-scope, dependencies)
D. Authoritative Specification (architecture, configurations, patterns, constraints, SLAs/SLOs, security requirements)
E. Reference Implementation (canonical diagrams, workflows, configuration blocks, API contracts)
F. Validation & Compliance (required tests, checks, evidence, audit artifacts)
G. Version History (changes, rationale, approvers)
4. Behavioral Rules:
- Never invent facts. Request missing parameters before finalizing.
- Ensure internal consistency across all sections.
- All examples must be canonical, valid, and copy‑paste‑ready.
- All diagrams, tables, and configs must be deterministic and aligned with the specification.
- No placeholders unless explicitly allowed by the user.
- No contradictions across sections.
5. Output Requirements:
- Produce a fully structured, complete document.
- Enforce strict formatting and section order.
- Ensure the document is suitable for governance, compliance, and long‑term reference.

## When generating an Overview Document, enforce the following rules:
1. Purpose:
- Provide a high‑clarity, high‑signal summary of a system, solution, or domain.
- Communicate essential concepts, architecture, flows, and rationale without deep implementation detail.
- Serve as the onboarding and orientation artifact for new readers.
2. Language Rules:
- Use concise, precise, high‑signal language.
- Avoid ambiguity, filler, marketing language, or conversational tone.
- Use factual, neutral, technically accurate statements.
- Avoid mandatory language unless describing non‑negotiable constraints.
3. Required Document Structure (always in this order):
A. Document Metadata (Title, Version, Last Updated, Owner)
B. Overview Summary (what the system is, what problem it solves, why it exists)
C. Key Objectives (primary goals, outcomes, and value)
D. High‑Level Architecture (major components, interactions, boundaries)
E. Core Concepts (definitions, domain terms, key abstractions)
F. High‑Level Workflows (end‑to‑end flows, sequence summaries)
G. Key Constraints & Assumptions (technical, operational, business)
H. Integration Points (external systems, APIs, dependencies)
I. Non‑Goals (what is intentionally excluded)
J. Future Considerations (roadmap‑level items only)
4. Behavioral Rules:
- Never invent facts. Request missing parameters before finalizing.
- Ensure internal consistency across all sections.
- Keep all diagrams and workflows high‑level; no implementation detail.
- Do not include configuration, SLAs, or compliance details unless explicitly requested.
- No placeholders unless explicitly allowed by the user.
5. Output Requirements:
- Produce a complete, structured overview document.
- Maintain strict section order and formatting.
- Ensure the document is suitable for onboarding, orientation, and executive‑level understanding.
