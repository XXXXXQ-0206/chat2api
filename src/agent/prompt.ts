import type { ProviderRequest, UnifiedMessage } from "../types.js";
import { describeToolsForPrompt } from "./tooling.js";

export function buildProviderPrompt(request: ProviderRequest): string {
  const parts = [
    buildAgentGuard(),
    describeToolsForPrompt(request.tools, request.toolChoice),
    describeRequestControls(request),
    "Conversation:",
    ...request.messages.map(formatMessage)
  ];
  return parts.filter(Boolean).join("\n\n");
}

function buildAgentGuard(): string {
  return [
    "You are serving a local API bridge for coding agents.",
    "Follow the user's task directly and produce actionable work product.",
    "Do not replace work with generic method summaries.",
    "If tools are available and needed, emit a tool-call envelope exactly as specified.",
    "If no tool call is needed, answer normally and do not mention this bridge."
  ].join("\n");
}

function describeRequestControls(request: ProviderRequest): string {
  return [
    `Mode: ${request.mode}`,
    `Deep thinking: ${request.thinking ? "on" : "off"}`,
    `Web search: ${request.webSearch ? "on" : "off"}`,
    request.files?.length
      ? `Attached files: ${request.files.map((file) => `${file.filename} (${file.id})`).join(", ")}`
      : ""
  ]
    .filter(Boolean)
    .join("\n");
}

function formatMessage(message: UnifiedMessage): string {
  const name = message.name ? ` name=${message.name}` : "";
  const tool = message.toolCallId ? ` tool_call_id=${message.toolCallId}` : "";
  return `<${message.role}${name}${tool}>\n${message.content}\n</${message.role}>`;
}
