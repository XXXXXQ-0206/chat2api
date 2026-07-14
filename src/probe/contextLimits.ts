import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import type { Chat2ApiConfig, ContextProbeResult, DeepSeekMode } from "../types.js";
import { MODE_CAPABILITIES } from "../types.js";

export class ContextLimitStore {
  private readonly filePath: string;

  constructor(private readonly config: Chat2ApiConfig) {
    this.filePath = path.join(config.dataDir, "context-limits.json");
  }

  async readAll(): Promise<Partial<Record<DeepSeekMode, ContextProbeResult>>> {
    try {
      return JSON.parse(await readFile(this.filePath, "utf8")) as Partial<Record<DeepSeekMode, ContextProbeResult>>;
    } catch {
      return {};
    }
  }

  async write(result: ContextProbeResult): Promise<void> {
    const all = await this.readAll();
    all[result.mode] = result;
    await writeFile(this.filePath, JSON.stringify(all, null, 2), { mode: 0o600 });
  }

  async limitFor(mode: DeepSeekMode): Promise<number> {
    const all = await this.readAll();
    return all[mode]?.safetyTokens ?? MODE_CAPABILITIES[mode].defaultContextTokens;
  }
}
