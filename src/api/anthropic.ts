import type { FastifyInstance, FastifyReply } from "fastify";
import { toAnthropicToolUse } from "../agent/tooling.js";
import type { ChatProvider } from "../provider/provider.js";
import type { ProviderRequest, ToolCall, UnifiedMessage } from "../types.js";
import { id } from "../utils/ids.js";
import { endSse, prepareSse, writeEvent } from "../utils/sse.js";
import type { ApiContext } from "./common.js";
import { applyContextLimit, assertStreamingProviderAvailable, booleanControl, modelForMode, parseContent, prepareWebSearchRequest, resolveFiles, resolveMode } from "./common.js";

export function registerAnthropicRoutes(app: FastifyInstance, context: ApiContext): void {
  app.post("/v1/messages", async (request, reply) => {
    const body = request.body as Record<string, unknown>;
    const providerRequest = await anthropicToProviderRequest(body, context);
    if (body.stream === true && !providerRequest.tools?.length) {
      assertStreamingProviderAvailable(context);
      await streamAnthropic(reply, context.provider, providerRequest);
      return reply;
    }
    const result = await context.provider.complete(providerRequest);
    if (body.stream === true) {
      streamAnthropicResult(reply, result.model, result.content, result.toolCalls);
      return reply;
    }
    return {
      id: id("msg"),
      type: "message",
      role: "assistant",
      model: result.model,
      content: result.toolCalls?.length ? toAnthropicToolUse(result.toolCalls) : [{ type: "text", text: result.content }],
      stop_reason: result.toolCalls?.length ? "tool_use" : "end_turn",
      stop_sequence: null,
      usage: {
        input_tokens: result.usage.input_tokens,
        output_tokens: result.usage.output_tokens
      }
    };
  });
}

async function anthropicToProviderRequest(body: Record<string, unknown>, context: ApiContext): Promise<ProviderRequest> {
  const messages: UnifiedMessage[] = [];
  const fileIds: string[] = [];
  let hasImage = false;

  if (body.system) {
    const parsed = parseContent(body.system);
    messages.push({ role: "system", content: parsed.text });
  }

  for (const raw of Array.isArray(body.messages) ? body.messages : []) {
    if (!raw || typeof raw !== "object") continue;
    const message = raw as Record<string, unknown>;
    const parsed = parseContent(message.content);
    fileIds.push(...parsed.fileIds);
    hasImage ||= parsed.hasImage;
    const role = String(message.role ?? "user") === "assistant" ? "assistant" : "user";
    messages.push({ role, content: parsed.text });
  }

  const extension = body.chat2api && typeof body.chat2api === "object" ? body.chat2api as Record<string, unknown> : {};
  const mode = resolveMode(String(body.model ?? ""), extension.mode ?? body.mode, hasImage);
  const webSearch = prepareWebSearchRequest(
    Array.isArray(body.tools) ? body.tools : undefined,
    body.tool_choice,
    booleanControl(body, "web_search") ?? false,
    "anthropic"
  );
  const request: ProviderRequest = {
    model: modelForMode(mode, String(body.model ?? "")),
    mode,
    messages,
    tools: webSearch.tools,
    toolChoice: webSearch.toolChoice,
    thinking: booleanControl(body, "thinking") ?? false,
    webSearch: webSearch.webSearch,
    files: await resolveFiles(context.fileStore, body, fileIds),
    maxTokens: typeof body.max_tokens === "number" ? body.max_tokens : undefined,
    temperature: typeof body.temperature === "number" ? body.temperature : undefined
  };
  return applyContextLimit(context, request);
}

async function streamAnthropic(reply: FastifyReply, provider: ChatProvider, request: ProviderRequest): Promise<void> {
  const messageId = id("msg");
  prepareSse(reply);
  writeEvent(reply, "message_start", {
    type: "message_start",
    message: {
      id: messageId,
      type: "message",
      role: "assistant",
      model: request.model,
      content: [],
      stop_reason: null,
      stop_sequence: null,
      usage: { input_tokens: 0, output_tokens: 0 }
    }
  });
  writeEvent(reply, "content_block_start", { type: "content_block_start", index: 0, content_block: { type: "text", text: "" } });

  try {
    for await (const delta of provider.stream(request)) {
      if (delta) {
        writeEvent(reply, "content_block_delta", { type: "content_block_delta", index: 0, delta: { type: "text_delta", text: delta } });
      }
    }
  } catch (error) {
    writeEvent(reply, "error", {
      type: "error",
      error: { type: "api_error", message: error instanceof Error ? error.message : "Provider stream failed." }
    });
    endSse(reply);
    return;
  }

  writeEvent(reply, "content_block_stop", { type: "content_block_stop", index: 0 });
  writeEvent(reply, "message_delta", { type: "message_delta", delta: { stop_reason: "end_turn", stop_sequence: null } });
  writeEvent(reply, "message_stop", { type: "message_stop" });
  endSse(reply);
}

function streamAnthropicResult(reply: FastifyReply, model: string, content: string, toolCalls?: ToolCall[]): void {
  const messageId = id("msg");
  prepareSse(reply);
  writeEvent(reply, "message_start", {
    type: "message_start",
    message: {
      id: messageId,
      type: "message",
      role: "assistant",
      model,
      content: [],
      stop_reason: null,
      stop_sequence: null,
      usage: { input_tokens: 0, output_tokens: 0 }
    }
  });

  if (toolCalls?.length) {
    for (const [index, block] of toAnthropicToolUse(toolCalls).entries()) {
      writeEvent(reply, "content_block_start", { type: "content_block_start", index, content_block: block });
      writeEvent(reply, "content_block_stop", { type: "content_block_stop", index });
    }
    writeEvent(reply, "message_delta", { type: "message_delta", delta: { stop_reason: "tool_use", stop_sequence: null } });
  } else {
    writeEvent(reply, "content_block_start", { type: "content_block_start", index: 0, content_block: { type: "text", text: "" } });
    writeEvent(reply, "content_block_delta", { type: "content_block_delta", index: 0, delta: { type: "text_delta", text: content } });
    writeEvent(reply, "content_block_stop", { type: "content_block_stop", index: 0 });
    writeEvent(reply, "message_delta", { type: "message_delta", delta: { stop_reason: "end_turn", stop_sequence: null } });
  }
  writeEvent(reply, "message_stop", { type: "message_stop" });
  endSse(reply);
}
