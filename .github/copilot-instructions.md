
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

