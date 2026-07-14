import type { Logger } from "pino";
import type { AuthStatus, Chat2ApiConfig, ContextProbeResult, DeepSeekMode, ProviderRequest, ProviderResponse } from "../types.js";
import { DeepSeekBrowserProvider } from "./deepseekBrowserProvider.js";
import { MockProvider } from "./mockProvider.js";
import { OfflineBrowserProvider } from "./offlineBrowserProvider.js";

export interface ContextProbeInput {
  mode: DeepSeekMode;
  promptChars: number;
  thinking?: boolean;
  webSearch?: boolean;
}

export interface ContextProbeCheck {
  accepted: boolean;
  error?: string;
}

export interface ChatProvider {
  complete(request: ProviderRequest): Promise<ProviderResponse>;
  stream(request: ProviderRequest): AsyncIterable<string>;
  authStatus(): Promise<AuthStatus>;
  beginLogin(): Promise<AuthStatus>;
  waitForLogin(timeoutMs?: number): Promise<AuthStatus>;
  probeContext(input: ContextProbeInput): Promise<ContextProbeCheck>;
  shutdown(): Promise<void>;
}

export function createProvider(config: Chat2ApiConfig, logger: Logger): ChatProvider {
  if (config.provider === "mock") {
    return new MockProvider(config, logger);
  }
  if (config.offlineMode) {
    return new OfflineBrowserProvider(config, logger);
  }
  return new DeepSeekBrowserProvider(config, logger);
}

export type { ContextProbeResult };
