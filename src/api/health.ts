import type { FastifyInstance } from "fastify";
import type { ApiContext } from "./common.js";
import { PUBLIC_MODE_CAPABILITIES } from "../types.js";

export function registerHealthRoutes(app: FastifyInstance, context: ApiContext): void {
  app.get("/health", async () => ({
    ok: true,
    provider: context.config.provider,
    time: new Date().toISOString()
  }));

  app.get("/v1/modes", async () => ({
    object: "list",
    data: PUBLIC_MODE_CAPABILITIES
  }));
}
