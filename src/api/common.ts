import type { Logger } from "pino";
import type { FastifyInstance } from "fastify";
import type { FileStore } from "../files/fileStore.js";
import type { ChatProvider } from "../provider/provider.js";
import { ContextLimitStore } from "../probe/contextLimits.js";
import type { Chat2ApiConfig, DeepSeekMode, ProviderRequest, UnifiedMessage, UploadedFileRef } from "../types.js";
import { MODE_CAPABILITIES } from "../types.js";
import { AppError } from "../utils/errors.js";
import { requiredWebSearchChoice, webSearchTool, type PreparedWebSearchRequest, type WebSearchProtocol } from "../tools/webSearch.js";
import { estimateMessagesTokens, trimMessagesToTokenLimit } from "../utils/tokens.js";

export interface ApiContext {
  config: Chat2ApiConfig;
  logger: Logger;
  provider: ChatProvider;
  fileStore: FileStore;
}

export type RouteRegistrar = (app: FastifyInstance, context: ApiContext) => Promise<void> | void;

export interface ContentParseResult {
  text: string;
  fileIds: string[];
  hasImage: boolean;
}

export function resolveMode(model?: string, explicit?: unknown, hasImage = false): DeepSeekMode {
  const requested = typeof explicit === "string" ? explicit.trim().toLowerCase() : undefined;
  if (requested === "fast") {
    throw new AppError(400, "unsupported_mode", "The fast mode is disabled; use expert or vision.");
  }
  if (requested === "expert" || requested === "vision") return requested;
  if (requested) {
    throw new AppError(400, "invalid_mode", "mode must be expert or vision.");
  }

  const value = (model ?? "").toLowerCase();
  if (value.includes("fast")) {
    throw new AppError(400, "unsupported_mode", "The fast mode is disabled; use expert or vision.");
  }
  if (hasImage) return "vision";
  if (value.includes("vision") || value.includes("image")) return "vision";
  if (value.includes("expert") || value.includes("reason") || value.includes("r1")) return "expert";
  return "expert";
}

export function modelForMode(mode: DeepSeekMode, requested?: string): string {
  if (requested && !requested.startsWith("deepseek-chat2api")) return requested;
  return MODE_CAPABILITIES[mode].model;
}

export function parseContent(content: unknown): ContentParseResult {
  if (content === undefined || content === null) return { text: "", fileIds: [], hasImage: false };
  if (typeof content === "string") return { text: content, fileIds: [], hasImage: false };
  if (!Array.isArray(content)) return { text: JSON.stringify(content), fileIds: [], hasImage: false };

  const lines: string[] = [];
  const fileIds: string[] = [];
  let hasImage = false;
  for (const part of content) {
    if (!part || typeof part !== "object") {
      lines.push(String(part));
      continue;
    }
    const value = part as Record<string, unknown>;
    const type = String(value.type ?? "");
    if (type === "text" || type === "input_text") {
      lines.push(String(value.text ?? ""));
    } else if (type === "image_url") {
      hasImage = true;
      const url = typeof value.image_url === "object" && value.image_url
        ? String((value.image_url as Record<string, unknown>).url ?? "")
        : String(value.image_url ?? "");
      lines.push(`[image_url:${url}]`);
    } else if (type === "input_image") {
      hasImage = true;
      const url = String(value.image_url ?? value.url ?? "");
      const fileId = String(value.file_id ?? "");
      if (fileId) fileIds.push(fileId);
      lines.push(url ? `[image_url:${url}]` : `[image:${fileId || "inline"}]`);
    } else if (type === "file" || type === "input_file" || type === "document") {
      const fileId = String(value.file_id ?? value.id ?? "");
      if (fileId) fileIds.push(fileId);
      lines.push(`[file:${fileId || value.filename || "attached"}]`);
    } else if (type === "tool_result") {
      lines.push(`[tool_result:${value.tool_use_id ?? ""}]\n${String(value.content ?? "")}`);
    } else if (type === "tool_use") {
      lines.push(`[tool_use:${value.name ?? ""}]\n${JSON.stringify(value.input ?? {})}`);
    } else {
      lines.push(JSON.stringify(value));
    }
  }
  return { text: lines.filter(Boolean).join("\n"), fileIds, hasImage };
}

export function booleanControl(body: Record<string, unknown>, name: string): boolean | undefined {
  const direct = body[name];
  const extension = body.chat2api && typeof body.chat2api === "object"
    ? (body.chat2api as Record<string, unknown>)[name]
    : undefined;
  const value = direct ?? extension;
  return typeof value === "boolean" ? value : undefined;
}

export function prepareWebSearchRequest(
  tools: unknown[] | undefined,
  toolChoice: unknown,
  enabled: boolean,
  protocol: WebSearchProtocol
): PreparedWebSearchRequest {
  if (!enabled) {
    return { tools, toolChoice, webSearch: false };
  }

  const current = tools ? [...tools] : [];
  const hasWebSearch = current.some((tool) => {
    if (!tool || typeof tool !== "object") return false;
    const value = tool as Record<string, unknown>;
    const functionValue = value.function;
    return value.name === "web_search"
      || (functionValue && typeof functionValue === "object" && (functionValue as Record<string, unknown>).name === "web_search");
  });
  if (!hasWebSearch) current.push(webSearchTool(protocol));

  return {
    tools: current,
    toolChoice: requiredWebSearchChoice(protocol),
    webSearch: false
  };
}

export async function resolveFiles(fileStore: FileStore, body: Record<string, unknown>, fileIds: string[]): Promise<UploadedFileRef[]> {
  const extension = body.chat2api && typeof body.chat2api === "object"
    ? (body.chat2api as Record<string, unknown>).file_ids
    : undefined;
  const bodyIds = Array.isArray(body.file_ids) ? body.file_ids : [];
  const extensionIds = Array.isArray(extension) ? extension : [];
  const ids = [...fileIds, ...bodyIds, ...extensionIds].map(String).filter(Boolean);
  return fileStore.resolveMany([...new Set(ids)]);
}

export async function applyContextLimit(context: ApiContext, request: ProviderRequest): Promise<ProviderRequest> {
  const store = new ContextLimitStore(context.config);
  const limit = await store.limitFor(request.mode);
  const cost = estimateMessagesTokens(request.messages);
  if (cost <= limit) return request;
  const trimmed = trimMessagesToTokenLimit(request.messages, limit);
  context.logger.warn({ mode: request.mode, cost, limit }, "request context exceeded configured limit and was trimmed");
  if (trimmed.length === 0) {
    throw new AppError(400, "context_length_exceeded", `Request exceeds context limit for mode ${request.mode}.`);
  }
  return {
    ...request,
    messages: [
      {
        role: "system",
        content: `chat2api trimmed older messages to fit the measured ${request.mode} context window.`
      },
      ...trimmed
    ]
  };
}

export function assertStreamingProviderAvailable(context: ApiContext): void {
  if (context.config.offlineMode && context.config.provider !== "mock") {
    throw new AppError(503, "provider_offline", "DeepSeek access is disabled by offline mode.");
  }
}
