using System.Text.Json.Nodes;

namespace Chat2ApiTray.Services.Api;

public enum WebSearchToolProtocol
{
    OpenAi,
    Anthropic,
    Responses
}

public sealed record PreparedWebSearchToolRequest(JsonArray? Tools, JsonNode? ToolChoice, bool WebSearch);

public static class WebSearchToolContract
{
    public const string Name = "web_search";

    public static PreparedWebSearchToolRequest Prepare(
        JsonArray? tools,
        JsonNode? toolChoice,
        bool requested,
        WebSearchToolProtocol protocol)
    {
        if (!requested)
        {
            return new PreparedWebSearchToolRequest(tools, toolChoice, WebSearch: false);
        }

        var preparedTools = tools is null
            ? new JsonArray()
            : (JsonArray)tools.DeepClone();
        if (!ContainsWebSearch(preparedTools))
        {
            preparedTools.Add(Definition(protocol));
        }

        return new PreparedWebSearchToolRequest(
            Tools: preparedTools,
            ToolChoice: RequiredChoice(protocol),
            WebSearch: false);
    }

    private static JsonObject Definition(WebSearchToolProtocol protocol)
    {
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The concise search query."
                }
            },
            ["required"] = new JsonArray("query"),
            ["additionalProperties"] = false
        };

        return protocol == WebSearchToolProtocol.Anthropic
            ? new JsonObject
            {
                ["name"] = Name,
                ["description"] = "Search the configured MCP or tool-executor backend.",
                ["input_schema"] = parameters
            }
            : new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = Name,
                    ["description"] = "Search the configured MCP or tool-executor backend.",
                    ["parameters"] = parameters
                }
            };
    }

    private static JsonObject RequiredChoice(WebSearchToolProtocol protocol)
    {
        return protocol == WebSearchToolProtocol.Anthropic
            ? new JsonObject { ["type"] = "tool", ["name"] = Name }
            : new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = Name }
            };
    }

    private static bool ContainsWebSearch(JsonArray tools)
    {
        return tools.OfType<JsonObject>().Any(tool =>
            string.Equals(Text(tool["name"]), Name, StringComparison.Ordinal)
            || string.Equals(Text((tool["function"] as JsonObject)?["name"]), Name, StringComparison.Ordinal));
    }

    private static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
