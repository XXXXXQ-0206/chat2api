import type { FastifyInstance } from "fastify";
import type { ApiContext } from "./common.js";

const statusRateLimit = { max: 30, timeWindow: "1 minute" };
const loginRateLimit = { max: 5, timeWindow: "1 minute" };
const waitRateLimit = { max: 10, timeWindow: "1 minute" };

export function registerAuthRoutes(app: FastifyInstance, context: ApiContext): void {
  app.get("/auth/status", { config: { rateLimit: statusRateLimit } }, async () => context.provider.authStatus());

  app.get("/auth/login", { config: { rateLimit: loginRateLimit } }, async () => context.provider.beginLogin());

  app.post("/auth/login", { config: { rateLimit: loginRateLimit } }, async () => context.provider.beginLogin());

  app.post("/auth/wait", { config: { rateLimit: waitRateLimit } }, async (request) => {
    const body = request.body as Record<string, unknown> | undefined;
    const timeoutMs = typeof body?.timeout_ms === "number" ? body.timeout_ms : undefined;
    return context.provider.waitForLogin(timeoutMs);
  });
}
