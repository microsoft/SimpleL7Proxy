# Run SimpleL7Proxy Locally

Start the proxy from source with one backend and verify that it accepts traffic.

## TL;DR

- Install Git and the .NET 10 SDK.
- Set `Port` and `Host1`.
- Run the proxy from `src/SimpleL7Proxy`.

| Setting | Value used here | Unit | Reload |
|---------|-----------------|------|--------|
| `Port` | `8000` | TCP port | Startup |
| `Host1` | `host=http://localhost:9000;probe=/health` | Connection string | Startup |

## Run from Source

**Set one listening port and one reachable backend before starting the proxy.**

```bash
export Port=8000
export Host1="host=http://localhost:9000;probe=/health"
cd src/SimpleL7Proxy && dotnet run
```

The startup banner confirms that the listener is running. The proxy also writes `eventslog.json` in its working directory.

> [!WARNING]
> If the backend does not expose `/health`, use a valid probe path or configure `mode=direct`. See [Backend Hosts](../reference/backend-hosts.md).

## Run in Docker

**Build with `src/` as the Docker context because the image needs both project directories.**

```bash
docker build -t simplel7proxy:latest -f src/SimpleL7Proxy/Dockerfile src
docker run --rm -p 8000:443 \
  -e 'Host1=host=http://host.docker.internal:9000;probe=/health' simplel7proxy:latest
```

> [!NOTE]
> The container's port `443` carries plain HTTP. TLS terminates at the ingress layer.

## Next Step

Continue with [Connect a Backend](connect-backend.md), then [Verify the Proxy](verify.md).
