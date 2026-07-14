import type { ToolCall } from "../types.js";
import { AppError } from "../utils/errors.js";

export const WEB_SEARCH_TOOL_NAME = "web_search";

const parameters = {
  type: "object",
  properties: {
    query: { type: "string", description: "The concise search query." }
  },
  required: ["query"],
  additionalProperties: false
};

export type WebSearchProtocol = "openai" | "anthropic" | "responses";

export interface PreparedWebSearchRequest {
  tools?: unknown[];
  toolChoice?: unknown;
  webSearch: false;
}

export function webSearchTool(protocol: WebSearchProtocol): Record<string, unknown> {
  if (protocol === "anthropic") {
    return {
      name: WEB_SEARCH_TOOL_NAME,
      description: "Search the configured MCP or tool-executor backend.",
      input_schema: parameters
    };
  }

  return {
    type: "function",
    function: {
      name: WEB_SEARCH_TOOL_NAME,
      description: "Search the configured MCP or tool-executor backend.",
      parameters
    }
  };
}

export function requiredWebSearchChoice(protocol: WebSearchProtocol): Record<string, unknown> {
  return protocol === "anthropic"
    ? { type: "tool", name: WEB_SEARCH_TOOL_NAME }
    : { type: "function", function: { name: WEB_SEARCH_TOOL_NAME } };
}

export interface ToolResult {
  role: "tool";
  tool_call_id: string;
  name: string;
  content: string;
}

export interface ToolExecutor {
  execute(call: ToolCall, signal?: AbortSignal): Promise<ToolResult>;
}

export class MockWebSearchExecutor implements ToolExecutor {
  async execute(call: ToolCall, signal?: AbortSignal): Promise<ToolResult> {
    signal?.throwIfAborted();
    if (call.function.name !== WEB_SEARCH_TOOL_NAME) {
      throw new AppError(400, "unknown_tool", `Unsupported tool '${call.function.name}'.`);
    }

    let input: unknown;
    try {
      input = JSON.parse(call.function.arguments);
    } catch {
      throw new AppError(400, "invalid_tool_arguments", "web_search arguments must be a JSON object.");
    }

    const query = input && typeof input === "object" && typeof (input as { query?: unknown }).query === "string"
      ? (input as { query: string }).query.trim()
      : "";
    if (!query) {
      throw new AppError(400, "invalid_tool_arguments", "web_search requires a non-empty query.");
    }

    return {
      role: "tool",
      tool_call_id: call.id,
      name: WEB_SEARCH_TOOL_NAME,
      content: JSON.stringify({
        query,
        results: [{ source: "mock", title: "Mock search result", snippet: "Deterministic local result." }]
      })
    };
  }
}
