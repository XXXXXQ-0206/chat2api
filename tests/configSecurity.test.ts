import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { loadConfig } from "../src/config.js";

describe("listener security", () => {
  it("rejects non-loopback listener hosts", async () => {
    const dataDir = await mkdtemp(path.join(tmpdir(), "chat2api-config-security-"));
    const previousDataDir = process.env.CHAT2API_DATA_DIR;
    process.env.CHAT2API_DATA_DIR = dataDir;

    try {
      expect(() => loadConfig({ host: "0.0.0.0", provider: "mock" })).toThrow(/loopback/i);
      expect(() => loadConfig({ host: "192.168.1.10", provider: "mock" })).toThrow(/loopback/i);
      expect(loadConfig({ host: "127.0.0.1", provider: "mock" }).host).toBe("127.0.0.1");
    } finally {
      if (previousDataDir === undefined) {
        delete process.env.CHAT2API_DATA_DIR;
      } else {
        process.env.CHAT2API_DATA_DIR = previousDataDir;
      }
      await rm(dataDir, { recursive: true, force: true });
    }
  });
});
