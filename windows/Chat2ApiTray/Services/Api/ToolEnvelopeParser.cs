using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chat2ApiTray.Services.Api;

public static class ToolEnvelopeParser
{
    private const string ToolOpen = "<chat2api_tool_calls>";
    private const string ToolClose = "</chat2api_tool_calls>";

    public static ParsedToolEnvelope Parse(string content)
    {
        var start = content.IndexOf(ToolOpen, StringComparison.Ordinal);
        var end = content.IndexOf(ToolClose, StringComparison.Ordinal);
        var hasOpen = start >= 0;
        var hasClose = end >= 0;

        if (!hasOpen && !hasClose)
        {
            return new ParsedToolEnvelope(content, null, null);
        }

        if (!hasOpen || !hasClose || end <= start)
        {
            return new ParsedToolEnvelope(content, null, "Malformed tool-call envelope.");
        }

        var before = content[..start].Trim();
        var after = content[(end + ToolClose.Length)..].Trim();
        var jsonText = StripJsonFence(content[(start + ToolOpen.Length)..end].Trim());

        try
        {
            var node = JsonNode.Parse(jsonText);
            var calls = NormalizeCalls(node);
            if (calls.Count == 0)
            {
                return new ParsedToolEnvelope(JoinText(before, after), null, "No valid tool calls found in envelope.");
            }

            return new ParsedToolEnvelope(JoinText(before, after), calls, null);
        }
        catch (Exception ex)
        {
            return new ParsedToolEnvelope(content, null, ex.Message);
        }
    }

    public static object[] ToAnthropicToolUse(List<ToolCall> calls)
    {
        return calls.Select(call => new
        {
            type = "tool_use",
            id = call.Id,
            name = call.Function.Name,
            input = SafeJson(call.Function.Arguments)
        }).Cast<object>().ToArray();
    }

    private static List<ToolCall> NormalizeCalls(JsonNode? node)
    {
        var source = node switch
        {
            JsonArray array => array,
            JsonObject obj when obj["calls"] is JsonArray calls => calls,
            _ => []
        };

        var result = new List<ToolCall>();
        foreach (var raw in source.OfType<JsonObject>())
        {
            var function = raw["function"] as JsonObject;
            var name = Text(raw["name"]) ?? Text(function?["name"]);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var arguments = raw["arguments"] ?? raw["input"] ?? function?["arguments"];
            result.Add(new ToolCall(
                Id: Text(raw["id"]) ?? $"call_{Guid.NewGuid():N}",
                Type: "function",
                Function: new ToolFunction(name, ArgumentText(arguments))));
        }

        return result;
    }

    private static string ArgumentText(JsonNode? node)
    {
        if (node is null)
        {
            return "{}";
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node.ToJsonString();
    }

    private static object SafeJson(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<object>(text) ?? text;
        }
        catch
        {
            return text;
        }
    }

    private static string StripJsonFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline
            ? text[(firstNewline + 1)..lastFence].Trim()
            : text;
    }

    private static string JoinText(string before, string after)
    {
        return string.Join("\n\n", new[] { before, after }.Where(part => !string.IsNullOrWhiteSpace(part)));
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
}
