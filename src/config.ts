import "dotenv/config";
import { mkdirSync } from "node:fs";
import { isIP } from "node:net";
import { homedir } from "node:os";
import path from "node:path";
import type { Chat2ApiConfig } from "./types.js";

function env(name: string, fallback = ""): string {
  const value = process.env[name];
  return value === undefined || value === "" ? fallback : value;
}

function optionalEnv(name: string): string | undefined {
  const value = process.env[name];
  return value === undefined || value === "" ? undefined : value;
}

function boolEnv(name: string, fallback: boolean): boolean {
  const value = env(name);
  if (!value) return fallback;
  return ["1", "true", "yes", "on"].includes(value.toLowerCase());
}

function numberEnv(name: string, fallback: number): number {
  const value = Number(env(name));
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

export interface ConfigOverrides {
  host?: string;
  port?: number;
  provider?: "browser" | "mock";
  offlineMode?: boolean;
}

export function loadConfig(overrides: ConfigOverrides = {}): Chat2ApiConfig {
  const dataDir = env("CHAT2API_DATA_DIR", path.join(homedir(), ".chat2api"));
  const config: Chat2ApiConfig = {
    host: overrides.host ?? env("CHAT2API_HOST", "127.0.0.1"),
    port: overrides.port ?? numberEnv("CHAT2API_PORT", 8022),
    provider: overrides.provider ?? (env("CHAT2API_PROVIDER", "browser") as "browser" | "mock"),
    offlineMode: overrides.offlineMode ?? boolEnv("CHAT2API_OFFLINE", false),
    dataDir,
    logDir: path.join(dataDir, "logs"),
    uploadDir: path.join(dataDir, "uploads"),
    browserProfileDir: path.join(dataDir, "browser-profile"),
    deepSeekUrl: env("CHAT2API_DEEPSEEK_URL", "https://chat.deepseek.com"),
    browserHeadless: boolEnv("CHAT2API_BROWSER_HEADLESS", false),
    browserChannel: optionalEnv("CHAT2API_BROWSER_CHANNEL"),
    completionTimeoutMs: numberEnv("CHAT2API_COMPLETION_TIMEOUT_MS", 180000),
    sessionTtlMinutes: numberEnv("CHAT2API_SESSION_TTL_MINUTES", 360),
    contextSafetyRatio: Math.min(1, Math.max(0.1, numberEnv("CHAT2API_CONTEXT_SAFETY_RATIO", 0.9))),
    stateEncryptionKey: optionalEnv("CHAT2API_STATE_ENCRYPTION_KEY")
  };

  if (config.provider !== "browser" && config.provider !== "mock") {
    config.provider = "browser";
  }

  assertLoopbackHost(config.host);

  for (const dir of [config.dataDir, config.logDir, config.uploadDir, config.browserProfileDir]) {
    mkdirSync(dir, { recursive: true });
  }

  return config;
}

function assertLoopbackHost(host: string): void {
  const normalized = host.trim().replace(/^\[(.*)]$/, "$1").toLowerCase();
  const isIpv4Loopback = isIP(normalized) === 4 && normalized.startsWith("127.");
  if (normalized === "localhost" || normalized === "::1" || isIpv4Loopback) {
    return;
  }

  throw new Error("Chat2api only supports loopback host binding. Use 127.0.0.1, localhost, or ::1.");
}
