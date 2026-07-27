# Use the LLM Simulator

Don't have an endpoint yet? The included LLM Simulator returns realistic Azure OpenAI / OpenAI / Anthropic / Gemini-shaped responses — including `429` throttling and streaming — so you can validate the full proxy pipeline first.

```bash
cd test/LLMSimulator && func start
```


```bash
export Host1="host=http://localhost:7071;probe=/api/health"
```

Point your existing client code at the proxy unchanged — the simulator mirrors real provider URL shapes, so nothing else needs to change when you swap in a real endpoint later.

See [→ LLM Simulator](../../test/LLMSimulator/Readme.md) for the full list of simulated models and error scenarios, and [→ Dummy Backend](../how-to/DUMMY_BACKEND.md) for an even simpler mock option.

---

[← Back to Where are your LLM models?](README.md#3-connect-a-backend)
