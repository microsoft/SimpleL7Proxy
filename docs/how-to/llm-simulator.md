# Dummy / Mock Backends for Local Development

Purpose: Set up local backend servers for testing the proxy without requiring cloud deployments.

> **TL;DR**
> - **Fastest:** `cd test/nullserver/Python && python streamserver.py` (already in repo)
> - **Standard Python:** `python -m http.server 3000` (built-in)
> - **Full stack example** at bottom of this doc

---

## Included Null Server (Recommended)

**Rule: Use the included null server for the fastest mock backend; it requires only Python and no extra dependencies.**

The proxy repository includes a lightweight mock server in `test/nullserver/Python` that responds to all requests with configurable delays.

```bash
# Terminal 1 — start the included mock backend
cd test/nullserver/Python
python streamserver.py

# Terminal 2 — start the proxy pointing at it
export Port=8080
export Host1=http://localhost:3000
dotnet run
```

> [!NOTE]
> `Host1` must be reachable before the proxy starts or the initial health check will mark it as OPEN.

> [!TIP]
> **Troubleshooting:** Run `curl http://localhost:3000/` to confirm the mock backend is up before starting the proxy.

---

## Standard Python HTTP Server

For simple testing, use Python's built-in HTTP server:

```bash
# Terminal 1
python -m http.server 3000

# Terminal 2
python -m http.server 5000

# Terminal 3
export Port=8080
export Host1=http://localhost:3000
export Host2=http://localhost:5000
dotnet run
```

---

## Testing Scenarios

**Rule: Test the proxy with simple requests before running load tests.**

```bash
# First, verify the backend is serving the file directly (not through proxy)
curl http://localhost:3000/lorem_ipsum.txt

# Then test through the proxy (should return same content)
curl http://localhost:8080/lorem_ipsum.txt

# Simple sequential load test (10 requests, shows status)
for i in {1..10}; do curl -w "Request $i: %{http_code}\n" -o /dev/null http://localhost:8080/lorem_ipsum.txt; done

# Parallel load test with timing (100 requests, 5 concurrent)
time for i in {1..100}; do curl -s http://localhost:8080/lorem_ipsum.txt > /dev/null & [ $((i % 5)) -eq 0 ] && wait; done
```

> [!TIP]
> **Verify backend first:** If `curl http://localhost:3000/lorem_ipsum.txt` returns empty, the backend isn't serving files. Ensure you're running `python -m http.server 3000` **from the `test/nullserver/Python` directory** where lorem_ipsum.txt lives.

> [!WARNING]
> **Error:** A `429` response during load testing means `MaxQueueLength` was reached — increase it or reduce concurrency.

---

## Container Development with Mock Backends

**Rule: Build the image locally and inject environment variables at `docker run` time; do not bake secrets into the image.**

```bash
docker build -t proxy-dev -f Dockerfile .

docker run -p 8080:443 \
  -e Host1=http://host.docker.internal:3000 \
  -e Host2=http://host.docker.internal:5000 \
  -e LogAllRequestHeaders=true \
  -e Workers=5 \
  proxy-dev
```

> [!NOTE]
> Use `host.docker.internal` to reach mock backends running on the host from inside the container.

> [!TIP]
> **Troubleshooting:** If the container exits immediately, check logs with `docker logs <container-id>` — a missing `Host1` value is the most common cause.

---

## Canonical Example — Full Local Stack

> **Goal:** proxy on port 8080 with two mock backends, header logging enabled, and clear reproduce -> inspect -> verify steps.

| Step | Command | Expected result |
|------|---------|----------------|
| Start backend 1 | `python -m http.server 3000` | Listening on :3000 |
| Start backend 2 | `python -m http.server 5000` | Listening on :5000 |
| Export config | `export Port=8080 Host1=http://localhost:3000 Host2=http://localhost:5000 Workers=10 LogAllRequestHeaders=true` | — |
| Start proxy | `dotnet run` | `Listening on port 8080` |
| Reproduce + inspect | `curl -v http://localhost:8080/lorem_ipsum.txt` | Lorem ipsum text from backend 1 or 2 |
| Apply fault + verify failover | Stop backend 1; `curl http://localhost:8080/` | `200 OK` routed to backend 2 |

**Stopping backend 1 while the proxy is running triggers circuit-breaker logic — subsequent requests route automatically to backend 2.**
