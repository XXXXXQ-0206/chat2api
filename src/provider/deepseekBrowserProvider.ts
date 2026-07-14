import type { Logger } from "pino";
import { buildProviderPrompt } from "../agent/prompt.js";
import { parseToolEnvelope } from "../agent/tooling.js";
import { DeepSeekWebClient } from "../browser/deepseekWebClient.js";
import type { AuthStatus, Chat2ApiConfig, ProviderRequest, ProviderResponse } from "../types.js";
import { id } from "../utils/ids.js";
import { estimateTokens } from "../utils/tokens.js";
import type { ChatProvider, ContextProbeCheck, ContextProbeInput } from "./provider.js";

export class DeepSeekBrowserProvider implements ChatProvider {
  private readonly client: DeepSeekWebClient;

  constructor(
    private readonly config: Chat2ApiConfig,
    private readonly logger: Logger
  ) {
    this.client = new DeepSeekWebClient(config, logger);
  }

  async complete(request: ProviderRequest): Promise<ProviderResponse> {
    const prompt = buildProviderPrompt(request);
    const rawContent = await this.client.send({
      prompt,
      mode: request.mode,
      thinking: request.thinking,
      webSearch: request.webSearch,
      files: request.files
    });
    const parsed = parseToolEnvelope(rawContent);
    if (parsed.parseError) {
      this.logger.warn({ error: parsed.parseError }, "tool envelope parse failed; returning text");
    }
    const content = parsed.toolCalls ? "" : parsed.text;
    const inputTokens = estimateTokens(prompt);
    const outputTokens = estimateTokens(content || rawContent);
    return {
      id: id("chat2api"),
      model: request.model,
      mode: request.mode,
      content,
      toolCalls: parsed.toolCalls,
      usage: {
        input_tokens: inputTokens,
        output_tokens: outputTokens,
        total_tokens: inputTokens + outputTokens
      },
      raw: rawContent
    };
  }

  async *stream(request: ProviderRequest): AsyncGenerator<string> {
    const prompt = buildProviderPrompt(request);
    for await (const delta of this.client.stream({
      prompt,
      mode: request.mode,
      thinking: request.thinking,
      webSearch: request.webSearch,
      files: request.files
    })) {
      yield delta;
    }
  }

  async authStatus(): Promise<AuthStatus> {
    return this.client.authStatus();
  }

  async beginLogin(): Promise<AuthStatus> {
    return this.client.beginLogin();
  }

  async waitForLogin(timeoutMs?: number): Promise<AuthStatus> {
    return this.client.waitForLogin(timeoutMs);
  }

  async probeContext(input: ContextProbeInput): Promise<ContextProbeCheck> {
    const prompt = [
      "Context probe. Reply with exactly: OK",
      "Probe payload follows.",
      "x".repeat(input.promptChars)
    ].join("\n");
    const accepted = await this.client.probe(input.mode, prompt, input.thinking, input.webSearch);
    return accepted ? { accepted } : { accepted, error: "probe_rejected" };
  }

  async shutdown(): Promise<void> {
    await this.client.close();
  }
}
