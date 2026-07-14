import type { FastifyInstance, FastifyReply } from "fastify";
import type { ProviderRequest, ToolCall, UnifiedMessage } from "../types.js";
import type { ChatProvider } from "../provider/provider.js";
import { id } from "../utils/ids.js";
import { endSse, prepareSse, writeEvent } from "../utils/sse.js";
import type { ApiContext } from "./common.js";
import { applyContextLimit, assertStreamingProviderAvailable, booleanControl, modelForMode, parseContent, prepareWebSearchRequest, resolveFiles, resolveMode } from "./common.js";

export function registerResponsesRoutes(app: FastifyInstance, context: ApiContext): void {
  app.post("/v1/responses", async (request, reply) => {
    const body = request.body as Record<string, unknown>;
    const providerRequest = await responsesToProviderRequest(body, context);
    if (body.stream === true && !providerRequest.tools?.length) {
      assertStreamingProviderAvailable(context);
      await streamResponse(reply, context.provider, providerRequest);
      return reply;
    }
    const result = await context.provider.complete(providerRequest);
    if (body.stream === true) {
      streamResponseResult(reply, result.model, result.content, result.toolCalls);
      return reply;
    }
    const output = result.toolCalls?.length
      ? result.toolCalls.map((call) => ({
        type: "function_call",
        id: call.id,
        call_id: call.id,
        name: call.function.name,
        arguments: call.function.arguments
      }))
      : [
        {
          id: id("msg"),
          type: "message",
          role: "assistant",
          content: [{ type: "output_text", text: result.content }]
        }
      ];
    return {
      id: id("resp"),
      object: "response",
      created_at: Date.now() / 1000,
      status: "completed",
      model: result.model,
      output,
      output_text: result.toolCalls?.length ? "" : result.content,
      usage: {
        input_tokens: result.usage.input_tokens,
        output_tokens: result.usage.output_tokens,
        total_tokens: result.usage.total_tokens
      }
    };
  });
}

async function responsesToProviderRequest(body: Record<string, unknown>, context: ApiContext): Promise<ProviderRequest> {
  const messages: UnifiedMessage[] = [];
  const fileIds: string[] = [];
  let hasImage = false;
  const input = body.input;
  if (typeof input === "string") {
    messages.push({ role: "user", content: input });
  } else if (Array.isArray(input)) {
    for (const raw of input) {
      if (!raw || typeof raw !== "object") continue;
      const message = raw as Record<string, unknown>;
      const parsed = parseContent(message.content ?? message.text ?? message.input);
      fileIds.push(...parsed.fileIds);
      hasImage ||= parsed.hasImage;
      const role = String(message.role ?? "user") as UnifiedMessage["role"];
      messages.push({ role: role === "assistant" || role === "system" || role === "tool" ? role : "user", content: parsed.text });
    }
  }

  const extension = body.chat2api && typeof body.chat2api === "object" ? body.chat2api as Record<string, unknown> : {};
  const mode = resolveMode(String(body.model ?? ""), extension.mode ?? body.mode, hasImage);
  const webSearch = prepareWebSearchRequest(
    Array.isArray(body.tools) ? body.tools : undefined,
    body.tool_choice,
    booleanControl(body, "web_search") ?? false,
    "responses"
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
    maxTokens: typeof body.max_output_tokens === "number" ? body.max_output_tokens : undefined,
    temperature: typeof body.temperature === "number" ? body.temperature : undefined
  };
  return applyContextLimit(context, request);
}

async function streamResponse(reply: FastifyReply, provider: ChatProvider, request: ProviderRequest): Promise<void> {
  const responseId = id("resp");
  const itemId = id("msg");
  prepareSse(reply);
  writeEvent(reply, "response.created", { type: "response.created", response: { id: responseId, status: "in_progress", model: request.model } });
  writeEvent(reply, "response.output_item.added", {
    type: "response.output_item.added",
    output_index: 0,
    item: { id: itemId, type: "message", role: "assistant", content: [] }
  });
  writeEvent(reply, "response.content_part.added", {
    type: "response.content_part.added",
    item_id: itemId,
    output_index: 0,
    content_index: 0,
    part: { type: "output_text", text: "" }
  });

  let content = "";
  try {
    for await (const delta of provider.stream(request)) {
      if (!delta) continue;
      content += delta;
      writeEvent(reply, "response.output_text.delta", {
        type: "response.output_text.delta",
        item_id: itemId,
        output_index: 0,
        content_index: 0,
        delta
      });
    }
  } catch (error) {
    writeEvent(reply, "response.failed", {
      type: "response.failed",
      response: {
        id: responseId,
        status: "failed",
        model: request.model,
        error: { code: "server_error", message: error instanceof Error ? error.message : "Provider stream failed." }
      }
    });
    endSse(reply);
    return;
  }

  writeEvent(reply, "response.output_text.done", { type: "response.output_text.done", item_id: itemId, output_index: 0, content_index: 0, text: content });
  writeEvent(reply, "response.content_part.done", { type: "response.content_part.done", item_id: itemId, output_index: 0, content_index: 0, part: { type: "output_text", text: content } });
  writeEvent(reply, "response.output_item.done", {
    type: "response.output_item.done",
    output_index: 0,
    item: { id: itemId, type: "message", role: "assistant", content: [{ type: "output_text", text: content }] }
  });
  writeEvent(reply, "response.completed", { type: "response.completed", response: { id: responseId, status: "completed", model: request.model } });
  endSse(reply);
}

function streamResponseResult(reply: FastifyReply, model: string, content: string, toolCalls?: ToolCall[]): void {
  const responseId = id("resp");
  prepareSse(reply);
  writeEvent(reply, "response.created", { type: "response.created", response: { id: responseId, status: "in_progress", model } });
  if (toolCalls?.length) {
    for (const [outputIndex, call] of toolCalls.entries()) {
      const item = { type: "function_call", id: call.id, call_id: call.id, name: call.function.name, arguments: call.function.arguments };
      writeEvent(reply, "response.output_item.added", { type: "response.output_item.added", output_index: outputIndex, item });
      writeEvent(reply, "response.output_item.done", { type: "response.output_item.done", output_index: outputIndex, item });
    }
  } else {
    writeEvent(reply, "response.output_text.delta", { type: "response.output_text.delta", delta: content });
  }
  writeEvent(reply, "response.completed", { type: "response.completed", response: { id: responseId, status: "completed", model } });
  endSse(reply);
}
