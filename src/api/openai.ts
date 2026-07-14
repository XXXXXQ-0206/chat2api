import type { FastifyInstance, FastifyReply } from "fastify";
import type { ApiContext } from "./common.js";
import { applyContextLimit, assertStreamingProviderAvailable, booleanControl, modelForMode, parseContent, prepareWebSearchRequest, resolveFiles, resolveMode } from "./common.js";
import type { ProviderRequest, ToolCall, UnifiedMessage } from "../types.js";
import type { ChatProvider } from "../provider/provider.js";
import { PUBLIC_MODE_CAPABILITIES } from "../types.js";
import { id } from "../utils/ids.js";
import { endSse, prepareSse, writeData } from "../utils/sse.js";

export function registerOpenAiRoutes(app: FastifyInstance, context: ApiContext): void {
  app.get("/v1/models", async () => ({
    object: "list",
    data: PUBLIC_MODE_CAPABILITIES.map((mode) => ({
      id: mode.model,
      object: "model",
      owned_by: "chat2api",
      capabilities: mode
    }))
  }));

  app.post("/v1/chat/completions", async (request, reply) => {
    const body = request.body as Record<string, unknown>;
    const providerRequest = await openAiToProviderRequest(body, context);
    if (body.stream === true && !providerRequest.tools?.length) {
      assertStreamingProviderAvailable(context);
      await streamOpenAi(reply, context.provider, providerRequest);
      return reply;
    }
    const result = await context.provider.complete(providerRequest);
    if (body.stream === true) {
      streamOpenAi(reply, result.model, result.content, result.toolCalls);
      return reply;
    }
    return {
      id: id("chatcmpl"),
      object: "chat.completion",
      created: Math.floor(Date.now() / 1000),
      model: result.model,
      choices: [
        {
          index: 0,
          message: {
            role: "assistant",
            content: result.toolCalls?.length ? null : result.content,
            ...(result.toolCalls?.length ? { tool_calls: result.toolCalls } : {})
          },
          finish_reason: result.toolCalls?.length ? "tool_calls" : "stop"
        }
      ],
      usage: {
        prompt_tokens: result.usage.input_tokens,
        completion_tokens: result.usage.output_tokens,
        total_tokens: result.usage.total_tokens
      }
    };
  });
}

async function openAiToProviderRequest(body: Record<string, unknown>, context: ApiContext): Promise<ProviderRequest> {
  const rawMessages = Array.isArray(body.messages) ? body.messages : [];
  const messages: UnifiedMessage[] = [];
  const fileIds: string[] = [];
  let hasImage = false;

  for (const raw of rawMessages) {
    if (!raw || typeof raw !== "object") continue;
    const message = raw as Record<string, unknown>;
    const parsed = parseContent(message.content);
    fileIds.push(...parsed.fileIds);
    hasImage ||= parsed.hasImage;
    const role = String(message.role ?? "user") as UnifiedMessage["role"];
    if (role === "assistant" && Array.isArray(message.tool_calls)) {
      messages.push({
        role,
        content: `${parsed.text}\nAssistant tool calls:\n${JSON.stringify(message.tool_calls)}`
      });
    } else {
      messages.push({
        role: role === "function" ? "tool" : role,
        content: parsed.text,
        name: typeof message.name === "string" ? message.name : undefined,
        toolCallId: typeof message.tool_call_id === "string" ? message.tool_call_id : undefined
      });
    }
  }

  const extension = body.chat2api && typeof body.chat2api === "object" ? body.chat2api as Record<string, unknown> : {};
  const mode = resolveMode(String(body.model ?? ""), extension.mode ?? body.mode, hasImage);
  const webSearch = prepareWebSearchRequest(
    Array.isArray(body.tools) ? body.tools : undefined,
    body.tool_choice,
    booleanControl(body, "web_search") ?? false,
    "openai"
  );
  const request: ProviderRequest = {
    model: modelForMode(mode, String(body.model ?? "")),
    mode,
    messages,
    tools: webSearch.tools,
    toolChoice: webSearch.toolChoice,
    thinking: booleanControl(body, "thinking") ?? booleanControl(body, "deep_thinking") ?? false,
    webSearch: webSearch.webSearch,
    files: await resolveFiles(context.fileStore, body, fileIds),
    maxTokens: typeof body.max_tokens === "number" ? body.max_tokens : undefined,
    temperature: typeof body.temperature === "number" ? body.temperature : undefined
  };
  return applyContextLimit(context, request);
}

async function streamOpenAi(reply: FastifyReply, provider: ChatProvider, request: ProviderRequest): Promise<void> {
  const chunkId = id("chatcmpl");
  prepareSse(reply);
  writeData(reply, {
    id: chunkId,
    object: "chat.completion.chunk",
    created: Math.floor(Date.now() / 1000),
    model: request.model,
    choices: [{ index: 0, delta: { role: "assistant" }, finish_reason: null }]
  });

  try {
    for await (const delta of provider.stream(request)) {
      if (!delta) continue;
      writeData(reply, {
        id: chunkId,
        object: "chat.completion.chunk",
        created: Math.floor(Date.now() / 1000),
        model: request.model,
        choices: [{ index: 0, delta: { content: delta }, finish_reason: null }]
      });
    }
  } catch (error) {
    writeData(reply, {
      error: {
        message: error instanceof Error ? error.message : "Provider stream failed.",
        type: "server_error",
        code: "provider_stream_failed"
      }
    });
    endSse(reply);
    return;
  }

  writeData(reply, {
    id: chunkId,
    object: "chat.completion.chunk",
    created: Math.floor(Date.now() / 1000),
    model: request.model,
    choices: [{ index: 0, delta: {}, finish_reason: "stop" }]
  });
  writeData(reply, "[DONE]");
  endSse(reply);
}
