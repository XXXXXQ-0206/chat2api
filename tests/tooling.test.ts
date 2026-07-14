import { describe, expect, it } from "vitest";
import { describeToolsForPrompt, parseToolEnvelope, toAnthropicToolUse } from "../src/agent/tooling.js";

describe("tooling", () => {
  it("parses a valid chat2api tool envelope", () => {
    const parsed = parseToolEnvelope('<chat2api_tool_calls>[{"name":"read_file","arguments":{"path":"a.ts"}}]</chat2api_tool_calls>');
    expect(parsed.toolCalls).toHaveLength(1);
    expect(parsed.toolCalls?.[0].function.name).toBe("read_file");
    expect(parsed.toolCalls?.[0].function.arguments).toBe('{"path":"a.ts"}');
  });

  it("supports fenced json inside the envelope", () => {
    const parsed = parseToolEnvelope("<chat2api_tool_calls>```json\n{\"calls\":[{\"name\":\"search\",\"input\":{\"q\":\"x\"}}]}\n```</chat2api_tool_calls>");
    expect(parsed.toolCalls?.[0].function.name).toBe("search");
  });

  it("falls back to text when json is malformed", () => {
    const parsed = parseToolEnvelope("<chat2api_tool_calls>{bad json}</chat2api_tool_calls>");
    expect(parsed.toolCalls).toBeUndefined();
    expect(parsed.parseError).toBeTruthy();
    expect(parsed.text).toContain("{bad json}");
  });

  it("omits tool instructions when no tools are provided", () => {
    expect(describeToolsForPrompt()).toBe("");
  });

  it("converts OpenAI tool calls to Anthropic tool_use blocks", () => {
    const parsed = parseToolEnvelope('<chat2api_tool_calls>[{"name":"lookup","arguments":{"id":1}}]</chat2api_tool_calls>');
    const blocks = toAnthropicToolUse(parsed.toolCalls ?? []);
    expect(blocks[0]).toMatchObject({ type: "tool_use", name: "lookup", input: { id: 1 } });
  });
});
