import pino, { type Logger } from "pino";
import path from "node:path";
import type { Chat2ApiConfig } from "./types.js";

export function createLogger(config: Chat2ApiConfig): Logger {
  const fileDestination = pino.destination({
    dest: path.join(config.logDir, "chat2api.log"),
    mkdir: true,
    sync: false
  });

  return pino(
    {
      level: process.env.CHAT2API_LOG_LEVEL ?? "info",
      redact: {
        paths: ["req.headers.authorization", "authorization", "*.cookie", "*.cookies"],
        censor: "[redacted]"
      }
    },
    pino.multistream([{ stream: process.stdout }, { stream: fileDestination }])
  );
}
