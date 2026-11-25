# Inspecting Request Details

Follow a captured request from enqueue through backend attempts to its final outcome.

## TL;DR

- Inspect the enqueue event for request and queue details.
- Review each recorded backend attempt.
- Check the final outcome and backend log when available.

## Follow the request

**Read the events together to understand how the request was processed.**

![Request detail showing the Enqueue event fields](../assets/companionapp/requests/chat-enqueue.png)

The Enqueue view shows the request as it entered the queue, including its path, priority, queue length, and request identity fields.

![Request detail showing a backend Attempt event](../assets/companionapp/requests/chat-attempt.png)

An Attempt view shows backend-specific details such as the selected host, attempt number, request timing, and success status.

![Request detail with the final event and backend log](../assets/companionapp/requests/chat-final.png)

The final view shows the terminal request outcome. When available, the backend log explains the attempts and response handling that led to that outcome.

> [!TIP]
> If an event or backend log is missing, check which telemetry was captured for the request before drawing conclusions about its processing.

[All CompanionApp visual guides](companionapp-visual-guide.md)
