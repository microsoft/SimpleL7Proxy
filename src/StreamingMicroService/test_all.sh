#!/usr/bin/env bash

curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file/sample-completions.json&sourceModel=gpt-4o-2024-11-20.Completions&targetModel=gpt-4.1-2025-04-14.Responses'
curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file/sample-completions.json&sourceModel=gpt-4o-2024-11-20.Completions&targetModel=claude-opus-5.Messages'

curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file/sample-responses.json&sourceModel=gpt-4.1-2025-04-14.Responses&targetModel=gpt-4o-2024-11-20.Completions'
curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file/sample-responses.json&sourceModel=gpt-4.1-2025-04-14.Responses&targetModel=claude-opus-5.Messages'

curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file/sample-anthropic-messages.json&sourceModel=claude-opus-5.Messages&targetModel=gpt-4o-2024-11-20.Completions'
curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file/sample-anthropic-messages.json&sourceModel=claude-opus-5.Messages&targetModel=gpt-4.1-2025-04-14.Responses'

curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file-stream/sample-completions-sse.json&sourceModel=gpt-4o-2024-11-20.Completions&targetModel=gpt-4o.Responses'
curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file-stream/sample-completions-sse.json&sourceModel=gpt-4o-2024-11-20.Completions&targetModel=claude-opus-5.Messages'

curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file-stream/sample-responses-sse.json&sourceModel=gpt-4o.Responses&targetModel=gpt-4o-2024-11-20.Completions'
curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file-stream/sample-responses-sse.json&sourceModel=gpt-4o.Responses&targetModel=claude-opus-5.Messages'

curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file-stream/sample-anthropic-messages-sse.json&sourceModel=claude-opus-5.Messages&targetModel=gpt-4o-2024-11-20.Completions'
curl -NsS --fail 'http://localhost:7071/api/process?url=http://localhost:3000/file-stream/sample-anthropic-messages-sse.json&sourceModel=claude-opus-5.Messages&targetModel=gpt-4o.Responses'