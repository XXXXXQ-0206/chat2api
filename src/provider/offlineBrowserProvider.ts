import type { Logger } from "pino";
import type { AuthStatus, Chat2ApiConfig, ProviderRequest, ProviderResponse } from "../types.js";
import { AppError } from "../utils/errors.js";
import type { ChatProvider, ContextProbeCheck, ContextProbeInput } from "./provider.js";

export class OfflineBrowserProvider implements ChatProvider {
  constructor(
    private readonly config: Chat2ApiConfig,
    private readonly logger: Logger
  ) {}

  async complete(_request: ProviderRequest): Promise<ProviderResponse> {
    this.reject();
  }

  async *stream(_request: ProviderRequest): AsyncGenerator<string> {
    this.reject();
  }

  async authStatus(): Promise<AuthStatus> {
    this.reject();
  }

  async beginLogin(): Promise<AuthStatus> {
    this.reject();
  }

  async waitForLogin(_timeoutMs?: number): Promise<AuthStatus> {
    this.reject();
  }

  async probeContext(_input: ContextProbeInput): Promise<ContextProbeCheck> {
    this.reject();
  }

  async shutdown(): Promise<void> {
    return undefined;
  }

  private reject(): never {
    this.logger.info({ provider: this.config.provider }, "browser provider rejected by offline mode");
    throw new AppError(503, "provider_offline", "DeepSeek access is disabled by offline mode.");
  }
}
