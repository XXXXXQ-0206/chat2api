using System.Text.Json.Nodes;

namespace Chat2ApiTray.Services.Api;

public static class ProtocolAdapters
{
    public static ProviderRequest FromOpenAi(JsonObject body)
    {
        var messages = new List<ApiMessage>();
        var fileIds = new List<string>();
        var hasImage = false;

        if (body["messages"] is JsonArray rawMessages)
        {
            foreach (var raw in rawMessages.OfType<JsonObject>())
            {
                var parsed = ParseContent(raw["content"]);
                hasImage |= parsed.HasImage;
                fileIds.AddRange(parsed.FileIds);
                var role = Text(raw["role"]) ?? "user";
                if (role.Equals("function", StringComparison.OrdinalIgnoreCase))
                {
                    role = "tool";
                }

                if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) && raw["tool_calls"] is JsonArray toolCalls)
                {
                    messages.Add(new ApiMessage(
                        Role: "assistant",
                        Content: $"{parsed.Text}\nAssistant tool calls:\n{toolCalls.ToJsonString()}"));
                }
                else
                {
                    messages.Add(new ApiMessage(
                        Role: role,
                        Content: parsed.Text,
                        Name: Text(raw["name"]),
                        ToolCallId: Text(raw["tool_call_id"])));
                }
            }
        }

        var extension = body["chat2api"] as JsonObject;
        fileIds.AddRange(ReadFileIds(body["file_ids"]));
        fileIds.AddRange(ReadFileIds(extension?["file_ids"]));
        var mode = ResolveMode(Text(body["model"]), extension?["mode"] ?? body["mode"], hasImage);
        var webSearch = WebSearchToolContract.Prepare(
            CloneArray(body["tools"]),
            body["tool_choice"]?.DeepClone(),
            BooleanControl(body, extension, "web_search") ?? false,
            WebSearchToolProtocol.OpenAi);
        return new ProviderRequest(
            Model: ApiCatalog.ModelForMode(mode, Text(body["model"]) ?? string.Empty),
            Mode: mode,
            Messages: messages,
            Tools: webSearch.Tools,
            ToolChoice: webSearch.ToolChoice,
            Thinking: BooleanControl(body, extension, "thinking") ?? BooleanControl(body, extension, "deep_thinking") ?? false,
            WebSearch: webSearch.WebSearch,
            ConversationId: Text(extension?["conversation_id"]) ?? Text(body["conversation_id"]),
            MaxTokens: Number(body["max_tokens"]),
            Temperature: Double(body["temperature"]),
            FileIds: fileIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static ProviderRequest FromAnthropic(JsonObject body)
    {
        var messages = new List<ApiMessage>();
        var fileIds = new List<string>();
        var hasImage = false;

        if (body["system"] is not null)
        {
            var parsed = ParseContent(body["system"]);
            fileIds.AddRange(parsed.FileIds);
            messages.Add(new ApiMessage("system", parsed.Text));
        }

        if (body["messages"] is JsonArray rawMessages)
        {
            foreach (var raw in rawMessages.OfType<JsonObject>())
            {
                var parsed = ParseContent(raw["content"]);
                hasImage |= parsed.HasImage;
                fileIds.AddRange(parsed.FileIds);
                var role = Text(raw["role"])?.Equals("assistant", StringComparison.OrdinalIgnoreCase) == true
                    ? "assistant"
                    : "user";
                messages.Add(new ApiMessage(role, parsed.Text));
            }
        }

        var extension = body["chat2api"] as JsonObject;
        fileIds.AddRange(ReadFileIds(body["file_ids"]));
        fileIds.AddRange(ReadFileIds(extension?["file_ids"]));
        var mode = ResolveMode(Text(body["model"]), extension?["mode"] ?? body["mode"], hasImage);
        var webSearch = WebSearchToolContract.Prepare(
            CloneArray(body["tools"]),
            body["tool_choice"]?.DeepClone(),
            BooleanControl(body, extension, "web_search") ?? false,
            WebSearchToolProtocol.Anthropic);
        return new ProviderRequest(
            Model: ApiCatalog.ModelForMode(mode, Text(body["model"]) ?? string.Empty),
            Mode: mode,
            Messages: messages,
            Tools: webSearch.Tools,
            ToolChoice: webSearch.ToolChoice,
            Thinking: BooleanControl(body, extension, "thinking") ?? false,
            WebSearch: webSearch.WebSearch,
            ConversationId: Text(extension?["conversation_id"]) ?? Text(body["conversation_id"]),
            MaxTokens: Number(body["max_tokens"]),
            Temperature: Double(body["temperature"]),
            FileIds: fileIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static ProviderRequest FromResponses(JsonObject body)
    {
        var messages = new List<ApiMessage>();
        var fileIds = new List<string>();
        var hasImage = false;
        var input = body["input"];

        if (input is JsonValue)
        {
            messages.Add(new ApiMessage("user", Text(input) ?? string.Empty));
        }
        else if (input is JsonArray rawInputs)
        {
            foreach (var raw in rawInputs.OfType<JsonObject>())
            {
                var itemType = Text(raw["type"]);
                if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    var callId = Text(raw["call_id"]) ?? Text(raw["id"]) ?? $"call_{Guid.NewGuid():N}";
                    var name = Text(raw["name"]) ?? "unknown";
                    var arguments = Text(raw["arguments"]) ?? "{}";
                    var functionCall = new JsonObject
                    {
                        ["id"] = callId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = name,
                            ["arguments"] = arguments
                        }
                    };
                    messages.Add(new ApiMessage("assistant", $"Assistant tool calls:\n[{functionCall.ToJsonString()}]"));
                    continue;
                }

                if (string.Equals(itemType, "function_call_output", StringComparison.OrdinalIgnoreCase))
                {
                    messages.Add(new ApiMessage(
                        Role: "tool",
                        Content: Text(raw["output"]) ?? string.Empty,
                        ToolCallId: Text(raw["call_id"]) ?? Text(raw["id"])));
                    continue;
                }

                var parsed = ParseContent(raw["content"] ?? raw["text"] ?? raw["input"]);
                hasImage |= parsed.HasImage;
                fileIds.AddRange(parsed.FileIds);
                var role = Text(raw["role"]) ?? "user";
                if (role is not ("assistant" or "system" or "tool"))
                {
                    role = "user";
                }

                messages.Add(new ApiMessage(role, parsed.Text));
            }
        }

        var extension = body["chat2api"] as JsonObject;
        fileIds.AddRange(ReadFileIds(body["file_ids"]));
        fileIds.AddRange(ReadFileIds(extension?["file_ids"]));
        var mode = ResolveMode(Text(body["model"]), extension?["mode"] ?? body["mode"], hasImage);
        var webSearch = WebSearchToolContract.Prepare(
            CloneArray(body["tools"]),
            body["tool_choice"]?.DeepClone(),
            BooleanControl(body, extension, "web_search") ?? false,
            WebSearchToolProtocol.Responses);
        return new ProviderRequest(
            Model: ApiCatalog.ModelForMode(mode, Text(body["model"]) ?? string.Empty),
            Mode: mode,
            Messages: messages,
            Tools: webSearch.Tools,
            ToolChoice: webSearch.ToolChoice,
            Thinking: BooleanControl(body, extension, "thinking") ?? false,
            WebSearch: webSearch.WebSearch,
            ConversationId: Text(extension?["conversation_id"]) ?? Text(body["conversation_id"]),
            MaxTokens: Number(body["max_output_tokens"]),
            Temperature: Double(body["temperature"]),
            FileIds: fileIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static bool WantsStream(JsonObject body)
    {
        return body["stream"]?.GetValueKind() is System.Text.Json.JsonValueKind.True;
    }

    private static ParsedContent ParseContent(JsonNode? content)
    {
        if (content is null)
        {
            return new ParsedContent(string.Empty, false, []);
        }

        if (content is JsonValue)
        {
            return new ParsedContent(Text(content) ?? string.Empty, false, []);
        }

        if (content is not JsonArray array)
        {
            return new ParsedContent(content.ToJsonString(), false, []);
        }

        var parts = new List<string>();
        var fileIds = new List<string>();
        var hasImage = false;
        foreach (var item in array)
        {
            if (item is JsonValue)
            {
                parts.Add(Text(item) ?? string.Empty);
                continue;
            }

            if (item is not JsonObject obj)
            {
                continue;
            }

            var type = Text(obj["type"]) ?? string.Empty;
            if (type is "text" or "input_text")
            {
                parts.Add(Text(obj["text"]) ?? string.Empty);
            }
            else if (type.Contains("image", StringComparison.OrdinalIgnoreCase))
            {
                hasImage = true;
                var imageUrl = obj["image_url"] as JsonObject;
                var fileId = Text(obj["file_id"]) ?? Text(imageUrl?["file_id"]);
                if (!string.IsNullOrWhiteSpace(fileId))
                {
                    fileIds.Add(fileId);
                }

                parts.Add($"[image:{fileId ?? Text(obj["image_url"]) ?? Text(obj["url"]) ?? "attached"}]");
            }
            else if (type is "tool_use")
            {
                var id = Text(obj["id"]) ?? "unknown";
                var name = Text(obj["name"]) ?? "unknown";
                var input = obj["input"]?.ToJsonString() ?? "{}";
                parts.Add($"[tool_use:id={id};name={name}]\n{input}");
            }
            else if (type is "tool_result")
            {
                var toolUseId = Text(obj["tool_use_id"]) ?? Text(obj["id"]) ?? "unknown";
                var result = ParseContent(obj["content"]);
                hasImage |= result.HasImage;
                parts.Add($"[tool_result:{toolUseId}]\n{result.Text}");
            }
            else if (type.Contains("file", StringComparison.OrdinalIgnoreCase))
            {
                var fileId = Text(obj["file_id"]) ?? Text(obj["id"]);
                if (!string.IsNullOrWhiteSpace(fileId))
                {
                    fileIds.Add(fileId);
                }

                parts.Add($"[file:{fileId ?? "attached"}]");
            }
        }

        return new ParsedContent(string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part))), hasImage, fileIds);
    }

    private static string ResolveMode(string? model, JsonNode? explicitMode, bool hasImage)
    {
        var requested = Text(explicitMode)?.Trim().ToLowerInvariant();
        if (string.Equals(requested, "fast", StringComparison.OrdinalIgnoreCase)
            || model?.Contains("fast", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ApiRequestException(400, "unsupported_mode", "The fast mode is disabled; use expert or vision.");
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (ApiCatalog.Modes.ContainsKey(requested))
            {
                return requested;
            }

            throw new ApiRequestException(400, "invalid_mode", "mode must be expert or vision.");
        }

        if (hasImage)
        {
            return "vision";
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            if (model.Contains("expert", StringComparison.OrdinalIgnoreCase))
            {
                return "expert";
            }

            if (model.Contains("vision", StringComparison.OrdinalIgnoreCase))
            {
                return "vision";
            }
        }

        return "expert";
    }

    private static bool? BooleanControl(JsonObject body, JsonObject? extension, string name)
    {
        return Bool(extension?[name]) ?? Bool(body[name]);
    }

    private static JsonArray? CloneArray(JsonNode? node)
    {
        return node is JsonArray array ? (JsonArray)array.DeepClone() : null;
    }

    private static bool? Bool(JsonNode? node)
    {
        return node?.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? Number(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    }

    private static double? Double(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
    }

    private static string? Text(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node.ToJsonString();
    }

    private static IEnumerable<string> ReadFileIds(JsonNode? node)
    {
        return node is JsonArray array
            ? array.Select(Text).Where(value => !string.IsNullOrWhiteSpace(value))!.Cast<string>()
            : [];
    }

    private sealed record ParsedContent(string Text, bool HasImage, List<string> FileIds);
}
