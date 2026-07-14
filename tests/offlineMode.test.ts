import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const state = vi.hoisted(() => ({ browserProviderConstructed: 0 }));

vi.mock("../src/provider/deepseekBrowserProvider.js", () => ({
  DeepSeekBrowserProvider: class {
    constructor() {
      state.browserProviderConstructed += 1;
    }
  }
}));

import { loadConfig } from "../src/config.js";
import { createServer, type ServerBundle } from "../src/server.js";

describe("offline mode", () => {
  let bundle: ServerBundle;

  beforeEach(async () => {
    state.browserProviderConstructed = 0;
    process.env.CHAT2API_DATA_DIR = await mkdtemp(path.join(tmpdir(), "chat2api-offline-"));
    bundle = await createServer(loadConfig({ provider: "browser", offlineMode: true }));
  });

  afterEach(async () => {
    await bundle.app.close();
  });

  it("refuses every browser-backed entry point without constructing the browser provider", async () => {
    const health = await bundle.app.inject({ method: "GET", url: "/health" });
    expect(health.statusCode).toBe(200);
    expect(health.json()).toMatchObject({ ok: true, provider: "browser" });

    const requests = [
      { method: "POST" as const, url: "/v1/chat/completions", payload: { model: "deepseek-chat2api-expert", messages: [{ role: "user", content: "offline" }] } },
      { method: "POST" as const, url: "/v1/chat/completions", payload: { model: "deepseek-chat2api-expert", stream: true, messages: [{ role: "user", content: "offline stream" }] } },
      { method: "GET" as const, url: "/auth/status" },
      { method: "POST" as const, url: "/auth/login" },
      { method: "POST" as const, url: "/auth/wait", payload: { timeout_ms: 1 } },
      { method: "POST" as const, url: "/admin/probe/context", payload: { mode: "expert", min_chars: 1, max_chars: 1 } }
    ];

    for (const request of requests) {
      const response = await bundle.app.inject(request);
      expect(response.statusCode).toBe(503);
      expect(response.json()).toMatchObject({ error: { code: "provider_offline" } });
      expect(response.body).not.toContain("data:");
    }

    expect(state.browserProviderConstructed).toBe(0);
  });

  it("keeps mock completion available in offline mode", async () => {
    await bundle.app.close();
    bundle = await createServer(loadConfig({ provider: "mock", offlineMode: true }));

    const response = await bundle.app.inject({
      method: "POST",
      url: "/v1/chat/completions",
      payload: { model: "deepseek-chat2api-expert", messages: [{ role: "user", content: "still local" }] }
    });

    expect(response.statusCode).toBe(200);
    expect(response.json().choices[0].message.content).toContain("mock:expert:still local");
  });
});
