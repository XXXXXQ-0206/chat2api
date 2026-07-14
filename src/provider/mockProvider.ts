import type { Logger } from "pino";
import { buildProviderPrompt } from "../agent/prompt.js";
import { parseToolEnvelope } from "../agent/tooling.js";
import type { AuthStatus, Chat2ApiConfig, ProviderRequest, ProviderResponse } from "../types.js";
import { estimateTokens } from "../utils/tokens.js";
import { id } from "../utils/ids.js";
import type { ChatProvider, ContextProbeCheck, ContextProbeInput } from "./provider.js";

export class MockProvider implements ChatProvider {
  private readonly thresholdByMode = {
    fast: 8000,
    expert: 16000,
    vision: 6000
  };

  constructor(
    private readonly config: Chat2ApiConfig,
    private readonly logger: Logger
  ) {}

  async complete(request: ProviderRequest): Promise<ProviderResponse> {
    const prompt = buildProviderPrompt(request);
    const lastUser = [...request.messages].reverse().find((message) => message.role === "user");
    let content = `mock:${request.mode}:${lastUser?.content ?? ""}`;

    const requiredToolName = requiredTool(request.toolChoice);
    if (request.tools?.length && (requiredToolName || /call_tool|use_tool|tool/i.test(lastUser?.content ?? ""))) {
      const tool = request.tools
        .map((value) => value as { function?: { name?: string }; name?: string })
        .find((value) => (value.function?.name ?? value.name) === requiredToolName)
        ?? request.tools[0] as { function?: { name?: string }; name?: string };
      const name = tool.function?.name ?? tool.name ?? "mock_tool";
      content = `<chat2api_tool_calls>[{"name":"${name}","arguments":{"query":"mock"}}]</chat2api_tool_calls>`;
    }

    const parsed = parseToolEnvelope(content);
    const inputTokens = estimateTokens(prompt);
    const outputTokens = estimateTokens(parsed.text || content);
    this.logger.debug({ mode: request.mode }, "mock provider completed request");
    return {
      id: id("chat2api"),
      model: request.model,
      mode: request.mode,
      content: parsed.toolCalls ? "" : parsed.text,
      toolCalls: parsed.toolCalls,
      usage: {
        input_tokens: inputTokens,
        output_tokens: outputTokens,
        total_tokens: inputTokens + outputTokens
      }
    };
  }

  async *stream(request: ProviderRequest): AsyncGenerator<string> {
    const response = await this.complete(request);
    for (const chunk of response.content.match(/.{1,8}/gs) ?? []) {
      yield chunk;
    }
  }

  async authStatus(): Promise<AuthStatus> {
    return {
      loggedIn: true,
      needsLogin: false,
      loginUrl: this.config.deepSeekUrl,
      lastCheckedAt: new Date().toISOString(),
      message: "Mock provider is always authenticated."
    };
  }

  async beginLogin(): Promise<AuthStatus> {
    return this.authStatus();
  }

  async waitForLogin(): Promise<AuthStatus> {
    return this.authStatus();
  }

  async probeContext(input: ContextProbeInput): Promise<ContextProbeCheck> {
    const accepted = input.promptChars <= this.thresholdByMode[input.mode];
    return accepted ? { accepted } : { accepted, error: "mock_context_limit" };
  }

  async shutdown(): Promise<void> {
    return undefined;
  }
}

function requiredTool(toolChoice: unknown): string | undefined {
  if (!toolChoice || typeof toolChoice !== "object") return undefined;
  const choice = toolChoice as { type?: unknown; name?: unknown; function?: { name?: unknown } };
  const type = typeof choice.type === "string" ? choice.type.toLowerCase() : "";
  if (type === "function") {
    return typeof choice.function?.name === "string"
      ? choice.function.name
      : typeof choice.name === "string" ? choice.name : undefined;
  }
  return type === "tool" && typeof choice.name === "string" ? choice.name : undefined;
}
