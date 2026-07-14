import type { FastifyInstance } from "fastify";
import type { ApiContext } from "./common.js";

export function registerAuthRoutes(app: FastifyInstance, context: ApiContext): void {
  app.get("/auth/status", async () => context.provider.authStatus());

  app.get("/auth/login", async () => context.provider.beginLogin());

  app.post("/auth/login", async () => context.provider.beginLogin());

  app.post("/auth/wait", async (request) => {
    const body = request.body as Record<string, unknown> | undefined;
    const timeoutMs = typeof body?.timeout_ms === "number" ? body.timeout_ms : undefined;
    return context.provider.waitForLogin(timeoutMs);
  });
}
