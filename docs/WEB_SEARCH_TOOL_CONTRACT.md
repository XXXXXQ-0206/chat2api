# Web Search Tool Contract

`web_search` is a common external tool contract for OpenAI Chat Completions, Anthropic Messages, and the Responses API. It is not a DeepSeek webpage control.

When a caller requests `web_search`, chat2api declares a required `web_search` function for the model while keeping the provider-facing webpage search switch disabled. The model decides whether and how to call the tool; a caller-configured MCP server or external executor performs any search and returns a normal tool result in the next request.

chat2api does not execute a network search and does not persist search results.

## Protocol Shapes

| Protocol | Tool call | Result continuation |
| --- | --- | --- |
| OpenAI Chat Completions | `tools[].type = function`, `function.name = web_search` | `role = tool` message with `tool_call_id` |
| Anthropic Messages | `tools[].name = web_search` | `tool_result` content block |
| Responses API | `function_call` with `name = web_search` | `function_call_output` item |

All three interfaces use the same tool name and executor boundary. Callers should validate tool arguments, enforce their own search-policy controls, and return only result data they intend to expose to the model.
