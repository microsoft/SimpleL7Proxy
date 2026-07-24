# Connect to APIM

Point the proxy at an Azure API Management gateway fronting your LLM backends. Use this when you need centralized governance — rate limiting by token count, semantic caching, PII scrubbing, or usage quotas — on top of the proxy's own priority queuing.

After you have the APIM up and running, you can upload bundled policy which makes the proxy and APIM behave seamlessly. 
See [→ APIM Policy Guide](../../APIM-Policy/readme.md) for the policy setup.


```bash
export Host_apim="host=<url>; mode=apim; probe=/status-0123456789abcdef"
```

If your APIM is protected by a subscription key, you will need to give the proxy acccess:

```bash
export Host_apim="host=<url>; path=/; api-key-header=Ocp-Apim-Subscription-Key; api-key=<key>; mode=apim; probe=/status-0123456789abcdef"
```

If your APIM is protected with an JWT token validate policy, you can have the proxy dynamically request a token:

```bash
export Host_apim="host=<url>; path=/; usemi=true; audience=<audience>; mode=apim; probe=/status-0123456789abcdef"
```

Your output will look similar to this screenshot.   Notice that the proxy has begun sending latency probes to the APIM. 


![alt text](S7P-port-apim.png)
---

[← Back to Where are your LLM models?](02-get-it-running.md#step-2-where-are-your-llm-models)
