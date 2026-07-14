import type { Logger } from "pino";
import type { ChatProvider } from "../provider/provider.js";
import type { Chat2ApiConfig, ContextProbeResult, DeepSeekMode, ProbeAttempt } from "../types.js";
import { estimateTokens } from "../utils/tokens.js";
import { ContextLimitStore } from "./contextLimits.js";

export interface ProbeOptions {
  mode: DeepSeekMode;
  minChars?: number;
  maxChars?: number;
  thinking?: boolean;
  webSearch?: boolean;
}

export class ContextProbe {
  private readonly store: ContextLimitStore;

  constructor(
    private readonly config: Chat2ApiConfig,
    private readonly provider: ChatProvider,
    private readonly logger: Logger
  ) {
    this.store = new ContextLimitStore(config);
  }

  async run(options: ProbeOptions): Promise<ContextProbeResult> {
    let low = options.minChars ?? 1024;
    let high = options.maxChars ?? 200000;
    const attempts: ProbeAttempt[] = [];

    while (low <= high) {
      const mid = Math.floor((low + high) / 2);
      const check = await this.provider.probeContext({
        mode: options.mode,
        promptChars: mid,
        thinking: options.thinking,
        webSearch: options.webSearch
      });
      const attempt: ProbeAttempt = {
        chars: mid,
        estimatedTokens: estimateTokens("x".repeat(mid)),
        accepted: check.accepted,
        error: check.error
      };
      attempts.push(attempt);
      this.logger.info({ mode: options.mode, attempt }, "context probe attempt");
      if (check.accepted) low = mid + 1;
      else high = mid - 1;
    }

    const maxAcceptedChars = Math.max(0, high);
    const estimatedTokens = estimateTokens("x".repeat(maxAcceptedChars));
    const result: ContextProbeResult = {
      mode: options.mode,
      maxAcceptedChars,
      estimatedTokens,
      safetyTokens: Math.floor(estimatedTokens * this.config.contextSafetyRatio),
      safetyRatio: this.config.contextSafetyRatio,
      probedAt: new Date().toISOString(),
      attempts
    };
    await this.store.write(result);
    return result;
  }

  async readAll(): Promise<Partial<Record<DeepSeekMode, ContextProbeResult>>> {
    return this.store.readAll();
  }
}
