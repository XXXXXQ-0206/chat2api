import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { loadConfig } from "../src/config.js";
import { createLogger } from "../src/logger.js";
import { MockProvider } from "../src/provider/mockProvider.js";
import { ContextProbe } from "../src/probe/contextProbe.js";

describe("ContextProbe", () => {
  it("finds the largest accepted prompt size by binary search", async () => {
    process.env.CHAT2API_DATA_DIR = await mkdtemp(path.join(tmpdir(), "chat2api-probe-"));
    process.env.CHAT2API_PROVIDER = "mock";
    const config = loadConfig();
    const logger = createLogger(config);
    const provider = new MockProvider(config, logger);
    const probe = new ContextProbe(config, provider, logger);

    const result = await probe.run({ mode: "fast", minChars: 1, maxChars: 9000 });

    expect(result.maxAcceptedChars).toBe(8000);
    expect(result.safetyTokens).toBe(Math.floor(result.estimatedTokens * config.contextSafetyRatio));
    expect(result.attempts.length).toBeGreaterThan(1);
  });
});
