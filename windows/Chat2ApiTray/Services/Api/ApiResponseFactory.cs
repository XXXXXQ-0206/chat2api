using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Chat2ApiTray.Services.Api;

public static class ApiResponseFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEnumerable<string> SplitTextForStreaming(string? text, int maxChars = 32)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        maxChars = Math.Max(1, maxChars);
        for (var offset = 0; offset < text.Length; offset += maxChars)
        {
            yield return text.Substring(offset, Math.Min(maxChars, text.Length - offset));
        }
    }

    public static object OpenAi(ProviderResult result)
    {
        return new
        {
            id = $"chatcmpl_{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = result.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = result.Content,
                        tool_calls = result.ToolCalls?.Count > 0 ? result.ToolCalls : null
                    },
                    finish_reason = result.ToolCalls?.Count > 0 ? "tool_calls" : "stop"
                }
            },
            usage = new
            {
                prompt_tokens = result.Usage.InputTokens,
                completion_tokens = result.Usage.OutputTokens,
                total_tokens = result.Usage.TotalTokens
            },
            chat2api = new
            {
                mode = result.Mode,
                context = result.Context
            }
        };
    }

    public static object Anthropic(ProviderResult result)
    {
        var content = new List<object>();
        if (!string.IsNullOrEmpty(result.Content) || result.ToolCalls is not { Count: > 0 })
        {
            content.Add(new { type = "text", text = result.Content });
        }

        if (result.ToolCalls is { Count: > 0 })
        {
            content.AddRange(ToolEnvelopeParser.ToAnthropicToolUse(result.ToolCalls));
        }

        return new
        {
            id = $"msg_{Guid.NewGuid():N}",
            type = "message",
            role = "assistant",
            model = result.Model,
            content,
            stop_reason = result.ToolCalls?.Count > 0 ? "tool_use" : "end_turn",
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = result.Usage.InputTokens,
                output_tokens = result.Usage.OutputTokens
            },
            chat2api = new
            {
                mode = result.Mode,
                context = result.Context
            }
        };
    }

    public static object Responses(ProviderResult result, string? responseId = null)
    {
        var output = new List<object>();
        if (!string.IsNullOrEmpty(result.Content) || result.ToolCalls is not { Count: > 0 })
        {
            output.Add(new
            {
                id = $"msg_{Guid.NewGuid():N}",
                type = "message",
                role = "assistant",
                content = new[] { new { type = "output_text", text = result.Content } }
            });
        }

        if (result.ToolCalls is { Count: > 0 })
        {
            output.AddRange(result.ToolCalls.Select(call => new
            {
                type = "function_call",
                id = call.Id,
                call_id = call.Id,
                name = call.Function.Name,
                arguments = call.Function.Arguments
            }).Cast<object>());
        }

        return new
        {
            id = responseId ?? $"resp_{Guid.NewGuid():N}",
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "completed",
            model = result.Model,
            output,
            output_text = result.Content,
            usage = new
            {
                input_tokens = result.Usage.InputTokens,
                output_tokens = result.Usage.OutputTokens,
                total_tokens = result.Usage.TotalTokens
            },
            chat2api = new
            {
                mode = result.Mode,
                context = result.Context
            }
        };
    }

    public static async Task<StreamSession> StartOpenAiStreamAsync(HttpResponse response, string model)
    {
        response.Headers.ContentType = "text/event-stream";
        var session = new StreamSession($"chatcmpl_{Guid.NewGuid():N}", model);
        await WriteDataAsync(response, new
        {
            id = session.Id,
            @object = "chat.completion.chunk",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = session.Model,
            choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } }
        });
        return session;
    }

    public static async Task WriteOpenAiTextDeltaAsync(HttpResponse response, StreamSession session, string delta)
    {
        session.TextEmitted = true;
        await WriteDataAsync(response, new
        {
            id = session.Id,
            @object = "chat.completion.chunk",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = session.Model,
            choices = new[] { new { index = 0, delta = new { content = delta }, finish_reason = (string?)null } }
        });
    }

    public static async Task FinishOpenAiStreamAsync(HttpResponse response, StreamSession session, ProviderResult result)
    {
        if (!session.TextEmitted && !string.IsNullOrEmpty(result.Content))
        {
            foreach (var chunk in SplitTextForStreaming(result.Content))
            {
                await WriteOpenAiTextDeltaAsync(response, session, chunk);
            }
        }

        if (result.ToolCalls?.Count > 0)
        {
            await WriteDataAsync(response, new
            {
                id = session.Id,
                @object = "chat.completion.chunk",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = session.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            tool_calls = result.ToolCalls.Select((call, index) => new
                            {
                                index,
                                id = call.Id,
                                type = call.Type,
                                function = new { name = call.Function.Name, arguments = call.Function.Arguments }
                            })
                        },
                        finish_reason = (string?)null
                    }
                }
            });
        }

        await WriteDataAsync(response, new
        {
            id = session.Id,
            @object = "chat.completion.chunk",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = session.Model,
            choices = new[] { new { index = 0, delta = new { }, finish_reason = result.ToolCalls?.Count > 0 ? "tool_calls" : "stop" } }
        });
        await response.WriteAsync("data: [DONE]\n\n");
        await response.Body.FlushAsync();
    }

    public static Task FailOpenAiStreamAsync(HttpResponse response, string code, string message)
    {
        return WriteDataAsync(response, new
        {
            error = new
            {
                message,
                type = "server_error",
                param = (string?)null,
                code
            }
        });
    }

    public static async Task<StreamSession> StartAnthropicStreamAsync(HttpResponse response, string model, int inputTokens)
    {
        response.Headers.ContentType = "text/event-stream";
        var session = new StreamSession($"msg_{Guid.NewGuid():N}", model);
        await WriteEventAsync(response, "message_start", new
        {
            type = "message_start",
            message = new
            {
                id = session.Id,
                type = "message",
                role = "assistant",
                model = session.Model,
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = inputTokens, output_tokens = 0 }
            }
        });
        return session;
    }

    public static async Task WriteAnthropicTextDeltaAsync(HttpResponse response, StreamSession session, string delta)
    {
        if (!session.TextBlockStarted)
        {
            session.TextBlockStarted = true;
            await WriteEventAsync(response, "content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        }

        await WriteEventAsync(response, "content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = delta } });
    }

    public static async Task FinishAnthropicStreamAsync(HttpResponse response, StreamSession session, ProviderResult result)
    {
        if (!session.TextBlockStarted && !string.IsNullOrEmpty(result.Content))
        {
            foreach (var chunk in SplitTextForStreaming(result.Content))
            {
                await WriteAnthropicTextDeltaAsync(response, session, chunk);
            }
        }

        if (session.TextBlockStarted)
        {
            await WriteEventAsync(response, "content_block_stop", new { type = "content_block_stop", index = 0 });
        }

        if (result.ToolCalls?.Count > 0)
        {
            var blocks = ToolEnvelopeParser.ToAnthropicToolUse(result.ToolCalls);
            for (var i = 0; i < blocks.Length; i++)
            {
                var index = (session.TextBlockStarted ? 1 : 0) + i;
                await WriteEventAsync(response, "content_block_start", new { type = "content_block_start", index, content_block = blocks[i] });
                await WriteEventAsync(response, "content_block_stop", new { type = "content_block_stop", index });
            }
        }
        else if (!session.TextBlockStarted)
        {
            await WriteAnthropicTextDeltaAsync(response, session, string.Empty);
            await WriteEventAsync(response, "content_block_stop", new { type = "content_block_stop", index = 0 });
        }

        await WriteEventAsync(response, "message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = result.ToolCalls?.Count > 0 ? "tool_use" : "end_turn", stop_sequence = (string?)null },
            usage = new { output_tokens = result.Usage.OutputTokens }
        });
        await WriteEventAsync(response, "message_stop", new { type = "message_stop" });
    }

    public static Task FailAnthropicStreamAsync(HttpResponse response, string message)
    {
        return WriteEventAsync(response, "error", new
        {
            type = "error",
            error = new { type = "api_error", message }
        });
    }

    public static async Task<StreamSession> StartResponsesStreamAsync(HttpResponse response, string model, string? responseId = null)
    {
        response.Headers.ContentType = "text/event-stream";
        var session = new StreamSession(responseId ?? $"resp_{Guid.NewGuid():N}", model);
        await WriteEventAsync(response, "response.created", new
        {
            type = "response.created",
            response = new { id = session.Id, status = "in_progress", model = session.Model }
        });
        return session;
    }

    public static Task WriteResponsesTextDeltaAsync(HttpResponse response, StreamSession session, string delta)
    {
        return WriteResponsesTextDeltaCoreAsync(response, session, delta);
    }

    public static async Task FinishResponsesStreamAsync(HttpResponse response, StreamSession session, ProviderResult result)
    {
        if (!session.TextEmitted && !string.IsNullOrEmpty(result.Content))
        {
            foreach (var chunk in SplitTextForStreaming(result.Content))
            {
                await WriteResponsesTextDeltaCoreAsync(response, session, chunk);
            }
        }

        var includesTextItem = !string.IsNullOrEmpty(result.Content) || result.ToolCalls is not { Count: > 0 };
        if (includesTextItem)
        {
            await EnsureResponsesTextItemAsync(response, session);
            var text = result.Content;
            await WriteEventAsync(response, "response.output_text.done", new
            {
                type = "response.output_text.done",
                item_id = session.TextItemId,
                output_index = 0,
                content_index = 0,
                text
            });
            await WriteEventAsync(response, "response.content_part.done", new
            {
                type = "response.content_part.done",
                item_id = session.TextItemId,
                output_index = 0,
                content_index = 0,
                part = new { type = "output_text", text }
            });
            await WriteEventAsync(response, "response.output_item.done", new
            {
                type = "response.output_item.done",
                output_index = 0,
                item = new
                {
                    id = session.TextItemId,
                    type = "message",
                    role = "assistant",
                    content = new[] { new { type = "output_text", text } }
                }
            });
        }

        if (result.ToolCalls is { Count: > 0 })
        {
            var toolOutputOffset = includesTextItem ? 1 : 0;
            foreach (var (call, outputIndex) in result.ToolCalls.Select((call, index) => (call, toolOutputOffset + index)))
            {
                var item = new { type = "function_call", id = call.Id, call_id = call.Id, name = call.Function.Name, arguments = call.Function.Arguments };
                await WriteEventAsync(response, "response.output_item.added", new
                {
                    type = "response.output_item.added",
                    output_index = outputIndex,
                    item
                });
                await WriteEventAsync(response, "response.output_item.done", new
                {
                    type = "response.output_item.done",
                    output_index = outputIndex,
                    item
                });
            }
        }

        await WriteEventAsync(response, "response.completed", new
        {
            type = "response.completed",
            response = new { id = session.Id, status = "completed", model = session.Model }
        });
    }

    private static async Task WriteResponsesTextDeltaCoreAsync(HttpResponse response, StreamSession session, string delta)
    {
        await EnsureResponsesTextItemAsync(response, session);
        session.TextEmitted = true;
        await WriteEventAsync(response, "response.output_text.delta", new
        {
            type = "response.output_text.delta",
            item_id = session.TextItemId,
            output_index = 0,
            content_index = 0,
            delta
        });
    }

    private static async Task EnsureResponsesTextItemAsync(HttpResponse response, StreamSession session)
    {
        if (session.TextItemId is not null)
        {
            return;
        }

        session.TextItemId = $"msg_{Guid.NewGuid():N}";
        await WriteEventAsync(response, "response.output_item.added", new
        {
            type = "response.output_item.added",
            output_index = 0,
            item = new { id = session.TextItemId, type = "message", role = "assistant", content = Array.Empty<object>() }
        });
        await WriteEventAsync(response, "response.content_part.added", new
        {
            type = "response.content_part.added",
            item_id = session.TextItemId,
            output_index = 0,
            content_index = 0,
            part = new { type = "output_text", text = string.Empty }
        });
    }

    public static Task FailResponsesStreamAsync(HttpResponse response, StreamSession session, string code, string message)
    {
        return WriteEventAsync(response, "response.failed", new
        {
            type = "response.failed",
            response = new
            {
                id = session.Id,
                status = "failed",
                model = session.Model,
                error = new { code, message }
            }
        });
    }

    public static async Task StreamOpenAiAsync(HttpResponse response, ProviderResult result)
    {
        var session = await StartOpenAiStreamAsync(response, result.Model);
        await FinishOpenAiStreamAsync(response, session, result);
    }

    public static async Task StreamAnthropicAsync(HttpResponse response, ProviderResult result)
    {
        var session = await StartAnthropicStreamAsync(response, result.Model, result.Usage.InputTokens);
        await FinishAnthropicStreamAsync(response, session, result);
    }

    public static async Task StreamResponsesAsync(HttpResponse response, ProviderResult result)
    {
        var session = await StartResponsesStreamAsync(response, result.Model);
        await FinishResponsesStreamAsync(response, session, result);
    }

    private static async Task WriteDataAsync(HttpResponse response, object payload)
    {
        await response.WriteAsync("data: ");
        await JsonSerializer.SerializeAsync(response.Body, payload, JsonOptions);
        await response.WriteAsync("\n\n");
        await response.Body.FlushAsync();
    }

    private static async Task WriteEventAsync(HttpResponse response, string eventName, object payload)
    {
        await response.WriteAsync($"event: {eventName}\n");
        await response.WriteAsync("data: ");
        await JsonSerializer.SerializeAsync(response.Body, payload, JsonOptions);
        await response.WriteAsync("\n\n");
        await response.Body.FlushAsync();
    }

    public sealed class StreamSession
    {
        public StreamSession(string id, string model)
        {
            Id = id;
            Model = model;
        }

        public string Id { get; }

        public string Model { get; }

        public bool TextBlockStarted { get; set; }

        public bool TextEmitted { get; set; }

        public string? TextItemId { get; set; }
    }
}
