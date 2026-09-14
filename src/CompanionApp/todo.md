High: Provider-specific streaming responses are not decoded.
ChatStreamParser.cs:170 only recognizes OpenAI-style delta.content, choices[].delta.content, and Responses API deltas. Anthropic content_block_delta, Cohere v2 content-delta, and native Gemini candidate events are unsupported. Both InvestigatorPage.razor:2196 and ChatPage.razor:697 use this parser, so these models can show an empty live result or raw SSE instead of assistant text. Add provider-specific delta extraction with representative fixtures.

High: Reasoning-model defaults are inconsistent across APIs.
The wildcard defaults in chat-models.json:100 add temperature, top_p, penalties, seed, and streaming to every OpenAI-compatible model. GPT-5 is specialized only for openai-chat, while o3 and o4-mini only override token count. The Responses API receives the generic sampling fields for all three. Depending on the target deployment, these unsupported parameters can produce HTTP 400 responses. Defaults need model-and-API-specific capability profiles, especially for GPT-5, o3, o4-mini, DeepSeek Reasoner, and Phi reasoning models.

Medium: Buffered Chat responses remain OpenAI-centric.
ChatPage.razor:684 calls ChatStreamParser.ExtractDisplayContent, but that parser does not read Anthropic content[], Gemini candidates[].content.parts[], Cohere v2 message.content[], or Cohere v1 text. The investigator partially compensates through VisionResponseParser.cs:93, but the regular Chat page exposes raw JSON for these models. Response interpretation should be shared across both capabilities.

Medium: GPT-5 history restoration selects the wrong UI model.
GPT-5 exists in chat-models.json:75, but not in the hard-coded ModelCatalog.cs:47. RequestBodyPanel.razor:87 still uses that legacy catalog to detect a saved request. A GPT-5 history item can therefore retain a GPT-5 JSON override while the UI falls back to GPT-4o, creating a misleading model/body mismatch. Model detection should use the configuration-backed ModelDefaults catalog.

Low: Model configuration claims live reload but catalogs never refresh.
Program.cs:8 loads model files with reloadOnChange: true, while ModelDefaults and VisionModelCatalog are singleton snapshots populated once during construction. Changes to models, APIs, defaults, or templates require an application restart despite the reload setting.

