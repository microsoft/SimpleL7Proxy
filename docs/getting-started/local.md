# Run the Proxy Locally

Before the proxy can accept traffic, it needs a port to listen on. Set that with the `Port` environment variable, then start the proxy either from source or in Docker.

## Run from Source

```bash
export Port=<port>
cd src/SimpleL7Proxy && dotnet run
```

## Run in Docker

```bash
export Port=<port>
docker run -p ${Port}:443 simplel7proxy:latest
```

---

You should see something like this. Note, the setup is not yet complete. We still need to configure the backends, but you can see that the server port is listening. You can stop the proxy with: `CTRL-C`.

![alt text](image-1.png)

[← Back to Choose Your Setup](02-get-it-running.md#step-1-choose-your-setup)
