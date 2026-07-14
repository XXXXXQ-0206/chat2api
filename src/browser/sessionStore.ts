import { createCipheriv, createDecipheriv, createHash, randomBytes } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import type { BrowserContext } from "playwright";
import type { Chat2ApiConfig } from "../types.js";

export class SessionStore {
  private readonly statePath: string;

  constructor(private readonly config: Chat2ApiConfig) {
    this.statePath = path.join(config.dataDir, "storage-state.json");
  }

  async save(context: BrowserContext): Promise<void> {
    const state = await context.storageState();
    const json = JSON.stringify({ savedAt: new Date().toISOString(), state });
    const payload = this.config.stateEncryptionKey ? encrypt(json, this.config.stateEncryptionKey) : json;
    await writeFile(this.statePath, payload, { mode: 0o600 });
  }

  async readSavedAt(): Promise<Date | undefined> {
    try {
      const raw = await readFile(this.statePath, "utf8");
      const json = this.config.stateEncryptionKey ? decrypt(raw, this.config.stateEncryptionKey) : raw;
      const parsed = JSON.parse(json) as { savedAt?: string };
      return parsed.savedAt ? new Date(parsed.savedAt) : undefined;
    } catch {
      return undefined;
    }
  }
}

function keyFromSecret(secret: string): Buffer {
  return createHash("sha256").update(secret).digest();
}

function encrypt(text: string, secret: string): string {
  const iv = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", keyFromSecret(secret), iv);
  const encrypted = Buffer.concat([cipher.update(text, "utf8"), cipher.final()]);
  const tag = cipher.getAuthTag();
  return `enc:${Buffer.concat([iv, tag, encrypted]).toString("base64")}`;
}

function decrypt(payload: string, secret: string): string {
  if (!payload.startsWith("enc:")) return payload;
  const buffer = Buffer.from(payload.slice(4), "base64");
  const iv = buffer.subarray(0, 12);
  const tag = buffer.subarray(12, 28);
  const encrypted = buffer.subarray(28);
  const decipher = createDecipheriv("aes-256-gcm", keyFromSecret(secret), iv);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(encrypted), decipher.final()]).toString("utf8");
}
