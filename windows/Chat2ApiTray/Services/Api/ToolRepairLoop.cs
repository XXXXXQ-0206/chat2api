using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Chat2ApiTray.Services.Api;

public static class ToolRepairLoop
{
    private const int MaxRepairAttempts = 2;

    public static async Task<ParsedToolEnvelope> CompleteWithRepairAsync(
        ProviderRequest request,
        string rawOutput,
        Func<string, Task<string>> sendRepairPromptAsync,
        CancellationToken cancellationToken)
    {
        var parsed = ParseForRequest(request, rawOutput, requireEnvelope: false);
        if (ToolCallsForbidden(request)
            || request.Tools is null
            || request.Tools.Count == 0
            || string.IsNullOrWhiteSpace(parsed.ParseError))
        {
            return parsed;
        }

        var original = parsed;
        var malformedOutput = rawOutput;
        for (var attempt = 0; attempt < MaxRepairAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repairOutput = await sendRepairPromptAsync(BuildRepairPrompt(request, malformedOutput, parsed.ParseError!));
            var repaired = ParseForRequest(request, repairOutput, requireEnvelope: true);
            if (string.IsNullOrWhiteSpace(repaired.ParseError))
            {
                return repaired;
            }

            malformedOutput = repairOutput;
            parsed = repaired;
        }

        return original;
    }

    public static void ValidateDeclaredToolSchemas(JsonArray? tools)
    {
        foreach (var tool in DeclaredTools(tools).Values)
        {
            if (tool.Schema is not null)
            {
                BuildSchema(tool.Name, tool.Schema);
            }
        }
    }

    private static ParsedToolEnvelope ParseForRequest(ProviderRequest request, string output, bool requireEnvelope)
    {
        var parsed = ToolEnvelopeParser.Parse(output);
        if (!string.IsNullOrWhiteSpace(parsed.ParseError))
        {
            return parsed;
        }

        if (parsed.ToolCalls is { Count: > 0 } && (request.Tools is null || request.Tools.Count == 0))
        {
            return parsed with { ParseError = "Tool calls are not allowed because no tools were declared." };
        }

        if (parsed.ToolCalls is { Count: > 0 } && ToolCallsForbidden(request))
        {
            return parsed with { ParseError = "Tool calls are forbidden by tool_choice=none." };
        }

        var requiredName = RequiredToolName(request);
        if (parsed.ToolCalls is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(requiredName)
                && parsed.ToolCalls.Any(call => !string.Equals(call.Function.Name, requiredName, StringComparison.Ordinal)))
            {
                return parsed with { ParseError = $"Tool choice requires function '{requiredName}'." };
            }

            var validationError = ValidateToolCalls(request.Tools, parsed.ToolCalls);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return parsed with { ParseError = validationError };
            }

            return parsed;
        }

        if (requireEnvelope || RequiresToolCall(request))
        {
            return parsed with { ParseError = "Tool choice requires a valid non-empty tool-call envelope." };
        }

        return parsed;
    }

    private static string? ValidateToolCalls(JsonArray? tools, List<ToolCall> calls)
    {
        var declaredTools = DeclaredTools(tools);
        if (declaredTools.Count == 0)
        {
            return "No declared function names are available for the tool-call envelope.";
        }

        foreach (var call in calls)
        {
            if (!declaredTools.TryGetValue(call.Function.Name, out var tool))
            {
                return $"Tool call '{call.Function.Name}' is undeclared.";
            }

            try
            {
                if (JsonNode.Parse(call.Function.Arguments) is not JsonObject arguments)
                {
                    return $"Tool call '{call.Function.Name}' arguments must be a JSON object.";
                }

                if (tool.Schema is not null && !SchemaAccepts(tool, arguments))
                {
                    return $"Tool call '{call.Function.Name}' arguments do not satisfy the declared JSON schema.";
                }
            }
            catch (JsonException)
            {
                return $"Tool call '{call.Function.Name}' arguments are not valid JSON.";
            }
        }

        return null;
    }

    private static Dictionary<string, DeclaredTool> DeclaredTools(JsonArray? tools)
    {
        var declared = new Dictionary<string, DeclaredTool>(StringComparer.Ordinal);
        foreach (var tool in tools?.OfType<JsonObject>() ?? [])
        {
            var function = tool["function"] as JsonObject;
            var name = Text(function?["name"]) ?? Text(tool["name"]);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ApiRequestException(400, "invalid_tool_schema", "Each declared tool must have a function name.");
            }

            var schema = function?["parameters"] ?? tool["parameters"] ?? tool["input_schema"];
            if (!declared.TryAdd(name, new DeclaredTool(name, schema)))
            {
                throw new ApiRequestException(400, "invalid_tool_schema", $"Tool '{name}' is declared more than once.");
            }
        }

        return declared;
    }

    private static bool SchemaAccepts(DeclaredTool tool, JsonObject arguments)
    {
        var schema = BuildSchema(tool.Name, tool.Schema!);
        using var instance = JsonDocument.Parse(arguments.ToJsonString());
        return schema.Evaluate(instance.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.Flag,
            RequireFormatValidation = true
        }).IsValid;
    }

    private static JsonSchema BuildSchema(string toolName, JsonNode schemaNode)
    {
        if (ContainsExternalReference(schemaNode))
        {
            throw new ApiRequestException(400, "invalid_tool_schema", $"Tool '{toolName}' schema may only use local references.");
        }

        try
        {
            return JsonSchema.FromText(
                schemaNode.ToJsonString(),
                new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or RefResolutionException)
        {
            throw new ApiRequestException(400, "invalid_tool_schema", $"Tool '{toolName}' schema is invalid.");
        }
    }

    private static bool ContainsExternalReference(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key is "$ref" or "$dynamicRef" or "$recursiveRef"
                    && property.Value is JsonValue value
                    && value.TryGetValue<string>(out var reference)
                    && !string.IsNullOrEmpty(reference)
                    && !reference.StartsWith('#'))
                {
                    return true;
                }

                if (property.Value is not null && ContainsExternalReference(property.Value))
                {
                    return true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.Where(item => item is not null))
            {
                if (ContainsExternalReference(item!))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool RequiresToolCall(ProviderRequest request)
    {
        if (string.Equals(Text(request.ToolChoice), "required", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (request.ToolChoice is JsonObject choice
            && string.Equals(Text(choice["type"]), "any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(RequiredToolName(request));
    }

    private static bool ToolCallsForbidden(ProviderRequest request)
    {
        if (string.Equals(Text(request.ToolChoice), "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return request.ToolChoice is JsonObject choice
            && string.Equals(Text(choice["type"]), "none", StringComparison.OrdinalIgnoreCase);
    }

    private static string? RequiredToolName(ProviderRequest request)
    {
        if (request.ToolChoice is not JsonObject choice)
        {
            return null;
        }

        var type = Text(choice["type"]);
        if (string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
        {
            return Text((choice["function"] as JsonObject)?["name"]) ?? Text(choice["name"]);
        }

        return string.Equals(type, "tool", StringComparison.OrdinalIgnoreCase)
            ? Text(choice["name"])
            : null;
    }

    private static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private sealed record DeclaredTool(string Name, JsonNode? Schema);

    public static string BuildRepairPrompt(ProviderRequest request, string rawOutput, string parseError)
    {
        var toolChoice = request.ToolChoice is null ? "auto" : request.ToolChoice.ToJsonString();
        return string.Join("\n\n", [
            "Repair the malformed tool-call envelope for a local OpenAI-compatible API bridge.",
            "Return only <chat2api_tool_calls> JSON </chat2api_tool_calls> with a valid JSON array.",
            "Do not add Markdown, analysis, or final user-facing text.",
            $"Parse error: {parseError}",
            $"Tool choice: {toolChoice}",
            $"Available tools: {request.Tools?.ToJsonString() ?? "[]"}",
            $"Malformed output:\n{rawOutput}"
        ]);
    }
}
