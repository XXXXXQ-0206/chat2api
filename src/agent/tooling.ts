import type { ToolCall } from "../types.js";
import { id } from "../utils/ids.js";

const TOOL_OPEN = "<chat2api_tool_calls>";
const TOOL_CLOSE = "</chat2api_tool_calls>";

export interface ParsedToolEnvelope {
  text: string;
  toolCalls?: ToolCall[];
  parseError?: string;
}

interface RawToolCall {
  id?: string;
  name?: string;
  arguments?: unknown;
  input?: unknown;
  function?: {
    name?: string;
    arguments?: unknown;
  };
}

export function describeToolsForPrompt(tools?: unknown[], toolChoice?: unknown): string {
  if (!tools || tools.length === 0) return "";
  return [
    "Tool contract:",
    `When a tool is required, respond only with ${TOOL_OPEN} JSON ${TOOL_CLOSE}.`,
    "The JSON must be an array of calls: [{\"name\":\"tool_name\",\"arguments\":{\"key\":\"value\"}}].",
    "Do not explain the call. Do not wrap it in Markdown. Do not continue with final text until tool results are provided.",
    `Available tools: ${JSON.stringify(tools)}`,
    toolChoice ? `Requested tool choice: ${JSON.stringify(toolChoice)}` : ""
  ]
    .filter(Boolean)
    .join("\n");
}

export function parseToolEnvelope(content: string): ParsedToolEnvelope {
  const start = content.indexOf(TOOL_OPEN);
  const end = content.indexOf(TOOL_CLOSE);
  if (start === -1 || end === -1 || end <= start) {
    return { text: content };
  }

  const before = content.slice(0, start).trim();
  const after = content.slice(end + TOOL_CLOSE.length).trim();
  const jsonText = stripJsonFence(content.slice(start + TOOL_OPEN.length, end).trim());

  try {
    const parsed = JSON.parse(jsonText) as unknown;
    const rawCalls = Array.isArray(parsed)
      ? parsed
      : typeof parsed === "object" && parsed && "calls" in parsed && Array.isArray((parsed as { calls: unknown }).calls)
        ? (parsed as { calls: unknown[] }).calls
        : [];
    const toolCalls = rawCalls.map(normalizeRawToolCall).filter((call): call is ToolCall => call !== undefined);
    if (toolCalls.length === 0) {
      return { text: [before, after].filter(Boolean).join("\n\n"), parseError: "No valid tool calls found in envelope." };
    }
    return { text: [before, after].filter(Boolean).join("\n\n"), toolCalls };
  } catch (error) {
    return {
      text: content,
      parseError: error instanceof Error ? error.message : String(error)
    };
  }
}

function stripJsonFence(text: string): string {
  const fenced = text.match(/^```(?:json)?\s*([\s\S]*?)\s*```$/i);
  return fenced ? fenced[1].trim() : text;
}

function normalizeRawToolCall(raw: unknown): ToolCall | undefined {
  if (!raw || typeof raw !== "object") return undefined;
  const value = raw as RawToolCall;
  const name = value.name ?? value.function?.name;
  if (!name) return undefined;
  const args = value.arguments ?? value.input ?? value.function?.arguments ?? {};
  const argumentString = typeof args === "string" ? args : JSON.stringify(args);
  return {
    id: value.id ?? id("call"),
    type: "function",
    function: {
      name,
      arguments: argumentString
    }
  };
}

export function toAnthropicToolUse(toolCalls: ToolCall[]): Array<Record<string, unknown>> {
  return toolCalls.map((call) => ({
    type: "tool_use",
    id: call.id,
    name: call.function.name,
    input: safeJson(call.function.arguments)
  }));
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}
