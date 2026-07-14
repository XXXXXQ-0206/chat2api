export type DeepSeekMode = "fast" | "expert" | "vision";

export interface ModeCapabilities {
  mode: DeepSeekMode;
  model: string;
  label: string;
  supportsThinking: boolean;
  supportsWebSearch: boolean;
  supportsImages: boolean;
  supportsFiles: boolean;
  defaultContextTokens: number;
}

export interface ToolCall {
  id: string;
  type: "function";
  function: {
    name: string;
    arguments: string;
  };
}

export interface UnifiedMessage {
  role: "system" | "user" | "assistant" | "tool";
  content: string;
  name?: string;
  toolCallId?: string;
}

export interface UploadedFileRef {
  id: string;
  filename: string;
  path: string;
  mimeType?: string;
  size: number;
}

export interface ProviderRequest {
  model: string;
  mode: DeepSeekMode;
  messages: UnifiedMessage[];
  tools?: unknown[];
  toolChoice?: unknown;
  thinking?: boolean;
  webSearch?: boolean;
  files?: UploadedFileRef[];
  maxTokens?: number;
  temperature?: number;
  metadata?: Record<string, unknown>;
}

export interface ProviderUsage {
  input_tokens: number;
  output_tokens: number;
  total_tokens: number;
}

export interface ProviderResponse {
  id: string;
  model: string;
  mode: DeepSeekMode;
  content: string;
  toolCalls?: ToolCall[];
  usage: ProviderUsage;
  raw?: unknown;
}

export interface AuthStatus {
  loggedIn: boolean;
  needsLogin: boolean;
  loginUrl: string;
  lastCheckedAt: string;
  lastLoginAt?: string;
  expiresAt?: string;
  message?: string;
}

export interface ProbeAttempt {
  chars: number;
  estimatedTokens: number;
  accepted: boolean;
  error?: string;
}

export interface ContextProbeResult {
  mode: DeepSeekMode;
  maxAcceptedChars: number;
  estimatedTokens: number;
  safetyTokens: number;
  safetyRatio: number;
  probedAt: string;
  attempts: ProbeAttempt[];
}

export interface FileRecord {
  id: string;
  filename: string;
  path: string;
  mimeType?: string;
  size: number;
  createdAt: string;
}

export interface Chat2ApiConfig {
  host: string;
  port: number;
  provider: "browser" | "mock";
  offlineMode: boolean;
  dataDir: string;
  logDir: string;
  uploadDir: string;
  browserProfileDir: string;
  deepSeekUrl: string;
  browserHeadless: boolean;
  browserChannel?: string;
  completionTimeoutMs: number;
  sessionTtlMinutes: number;
  contextSafetyRatio: number;
  stateEncryptionKey?: string;
}

export const MODE_CAPABILITIES: Record<DeepSeekMode, ModeCapabilities> = {
  fast: {
    mode: "fast",
    model: "deepseek-chat2api-fast",
    label: "Fast",
    supportsThinking: false,
    supportsWebSearch: true,
    supportsImages: false,
    supportsFiles: true,
    defaultContextTokens: 32000
  },
  expert: {
    mode: "expert",
    model: "deepseek-chat2api-expert",
    label: "Expert",
    supportsThinking: true,
    supportsWebSearch: true,
    supportsImages: false,
    supportsFiles: true,
    defaultContextTokens: 64000
  },
  vision: {
    mode: "vision",
    model: "deepseek-chat2api-vision",
    label: "Vision",
    supportsThinking: true,
    supportsWebSearch: true,
    supportsImages: true,
    supportsFiles: true,
    defaultContextTokens: 32000
  }
};

export const PUBLIC_MODE_CAPABILITIES = Object.values(MODE_CAPABILITIES).filter(
  (mode) => mode.mode !== "fast"
);
