import { describe, expect, it } from "vitest";
import { prepareWebSearchRequest } from "../src/api/common.js";
import { MockWebSearchExecutor, WEB_SEARCH_TOOL_NAME } from "../src/tools/webSearch.js";

describe("web search tool contract", () => {
  it("turns the legacy boolean into a required OpenAI tool without browser search", () => {
    const prepared = prepareWebSearchRequest([], undefined, true, "openai");
    expect(prepared.webSearch).toBe(false);
    expect(prepared.tools).toHaveLength(1);
    expect(prepared.tools?.[0]).toMatchObject({ type: "function", function: { name: WEB_SEARCH_TOOL_NAME } });
    expect(prepared.toolChoice).toEqual({ type: "function", function: { name: WEB_SEARCH_TOOL_NAME } });
  });

  it("uses the Anthropic tool choice shape", () => {
    const prepared = prepareWebSearchRequest(undefined, undefined, true, "anthropic");
    expect(prepared.tools?.[0]).toMatchObject({ name: WEB_SEARCH_TOOL_NAME, input_schema: { type: "object" } });
    expect(prepared.toolChoice).toEqual({ type: "tool", name: WEB_SEARCH_TOOL_NAME });
    expect(prepared.webSearch).toBe(false);
  });

  it("returns a deterministic tool_result from the mock executor", async () => {
    const result = await new MockWebSearchExecutor().execute({
      id: "call_search",
      type: "function",
      function: { name: WEB_SEARCH_TOOL_NAME, arguments: JSON.stringify({ query: "local test" }) }
    });
    expect(result).toMatchObject({ role: "tool", tool_call_id: "call_search", name: WEB_SEARCH_TOOL_NAME });
    expect(JSON.parse(result.content)).toMatchObject({ query: "local test", results: [{ source: "mock" }] });
  });
});
