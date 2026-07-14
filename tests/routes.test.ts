import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { loadConfig } from "../src/config.js";
import { createServer, type ServerBundle } from "../src/server.js";

describe("routes", () => {
  let bundle: ServerBundle;

  beforeEach(async () => {
    process.env.CHAT2API_DATA_DIR = await mkdtemp(path.join(tmpdir(), "chat2api-routes-"));
    process.env.CHAT2API_PROVIDER = "mock";
    bundle = await createServer(loadConfig({ provider: "mock" }));
  });

  afterEach(async () => {
    await bundle.app.close();
  });

  it("serves health and model metadata", async () => {
    const health = await bundle.app.inject({ method: "GET", url: "/health" });
    expect(health.statusCode).toBe(200);
    expect(health.json()).toMatchObject({ ok: true, provider: "mock" });

    const models = await bundle.app.inject({ method: "GET", url: "/v1/models" });
    expect(models.statusCode).toBe(200);
    expect(models.json().data.map((model: { id: string }) => model.id)).toContain("deepseek-chat2api-expert");
  });

  it("opens login from GET and form-style POST requests", async () => {
    const getLogin = await bundle.app.inject({ method: "GET", url: "/auth/login" });
    expect(getLogin.statusCode).toBe(200);
    expect(getLogin.json()).toMatchObject({ loggedIn: true, needsLogin: false });

    const postLogin = await bundle.app.inject({
      method: "POST",
      url: "/auth/login",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      payload: ""
    });
    expect(postLogin.statusCode).toBe(200);
    expect(postLogin.json()).toMatchObject({ loggedIn: true, needsLogin: false });
  });

  it("returns OpenAI-compatible chat completions", async () => {
    const response = await bundle.app.inject({
      method: "POST",
      url: "/v1/chat/completions",
      payload: {
        model: "deepseek-chat2api-expert",
        messages: [{ role: "user", content: "hello" }]
      }
    });
    expect(response.statusCode).toBe(200);
    const json = response.json();
    expect(json.choices[0].message.content).toContain("mock:expert:hello");
    expect(json.usage.total_tokens).toBeGreaterThan(0);
  });

  it("forwards provider stream deltas as multiple OpenAI SSE chunks", async () => {
    const response = await bundle.app.inject({
      method: "POST",
      url: "/v1/chat/completions",
      payload: {
        model: "deepseek-chat2api-expert",
        stream: true,
        messages: [{ role: "user", content: "stream this response" }]
      }
    });

    expect(response.statusCode).toBe(200);
    expect(response.body.match(/"content":/g)?.length ?? 0).toBeGreaterThan(1);
    expect(response.body).toContain("data: [DONE]");
  });

  it("forwards provider stream deltas for Anthropic and Responses", async () => {
    const anthropic = await bundle.app.inject({
      method: "POST",
      url: "/v1/messages",
      payload: {
        model: "deepseek-chat2api-expert",
        stream: true,
        max_tokens: 128,
        messages: [{ role: "user", content: "stream Anthropic" }]
      }
    });
    expect(anthropic.statusCode).toBe(200);
    expect(anthropic.body.match(/event: content_block_delta/g)?.length ?? 0).toBeGreaterThan(1);
    expect(anthropic.body).toContain("event: message_stop");

    const responses = await bundle.app.inject({
      method: "POST",
      url: "/v1/responses",
      payload: {
        model: "deepseek-chat2api-expert",
        stream: true,
        input: "stream Responses"
      }
    });
    expect(responses.statusCode).toBe(200);
    expect(responses.body.match(/event: response.output_text.delta/g)?.length ?? 0).toBeGreaterThan(1);
    expect(responses.body).toContain("event: response.completed");
  });

  it("stores local file uploads and returns metadata", async () => {
    const source = path.join(process.env.CHAT2API_DATA_DIR ?? "", "sample.txt");
    await writeFile(source, "hello file");
    const response = await bundle.app.inject({
      method: "POST",
      url: "/v1/files",
      payload: {
        path: source,
        mime_type: "text/plain"
      }
    });
    expect(response.statusCode).toBe(200);
    const created = response.json();
    expect(created.filename).toBe("sample.txt");
    expect(created.bytes).toBe(10);

    const fetched = await bundle.app.inject({ method: "GET", url: `/v1/files/${created.id}` });
    expect(fetched.statusCode).toBe(200);
    expect(fetched.json()).toMatchObject({ id: created.id, bytes: 10 });
  });

  it("rejects JSON uploads outside the private data directory", async () => {
    const externalDirectory = await mkdtemp(path.join(tmpdir(), "chat2api-external-upload-"));
    const source = path.join(externalDirectory, "outside.txt");
    await writeFile(source, "outside data");

    try {
      const response = await bundle.app.inject({
        method: "POST",
        url: "/v1/files",
        payload: { path: source, mime_type: "text/plain" }
      });

      expect(response.statusCode).toBe(400);
      expect(response.json()).toMatchObject({ error: { code: "file_path_not_allowed" } });
    } finally {
      await rm(externalDirectory, { recursive: true, force: true });
    }
  });

  it("returns OpenAI tool calls when tools are requested", async () => {
    const response = await bundle.app.inject({
      method: "POST",
      url: "/v1/chat/completions",
      payload: {
        model: "deepseek-chat2api-expert",
        messages: [{ role: "user", content: "please call_tool" }],
        tools: [{ type: "function", function: { name: "lookup", parameters: { type: "object" } } }]
      }
    });
    expect(response.statusCode).toBe(200);
    const json = response.json();
    expect(json.choices[0].finish_reason).toBe("tool_calls");
    expect(json.choices[0].message.tool_calls[0].function.name).toBe("lookup");
  });

  it("turns web_search into a tool call without enabling provider web search", async () => {
    const response = await bundle.app.inject({
      method: "POST",
      url: "/v1/chat/completions",
      payload: {
        model: "deepseek-chat2api-expert",
        web_search: true,
        messages: [{ role: "user", content: "find a local result" }]
      }
    });
    expect(response.statusCode).toBe(200);
    const json = response.json();
    expect(json.choices[0].finish_reason).toBe("tool_calls");
    expect(json.choices[0].message.tool_calls[0].function.name).toBe("web_search");
  });

  it("returns Anthropic-compatible messages", async () => {
    const response = await bundle.app.inject({
      method: "POST",
      url: "/v1/messages",
      payload: {
        model: "deepseek-chat2api-expert",
        messages: [{ role: "user", content: "hello" }]
      }
    });
    expect(response.statusCode).toBe(200);
    const json = response.json();
    expect(json.type).toBe("message");
    expect(json.content[0].text).toContain("mock:expert:hello");
  });

  it("returns Responses-compatible output", async () => {
    const response = await bundle.app.inject({
      method: "POST",
      url: "/v1/responses",
      payload: {
        model: "deepseek-chat2api-expert",
        input: "hello"
      }
    });
    expect(response.statusCode).toBe(200);
    const json = response.json();
    expect(json.object).toBe("response");
    expect(json.output_text).toContain("mock:expert:hello");
  });
});
