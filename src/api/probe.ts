import type { FastifyInstance } from "fastify";
import { ContextProbe } from "../probe/contextProbe.js";
import type { DeepSeekMode } from "../types.js";
import { AppError } from "../utils/errors.js";
import type { ApiContext } from "./common.js";

export function registerProbeRoutes(app: FastifyInstance, context: ApiContext): void {
  app.get("/admin/probe/context", { config: { rateLimit: { max: 30, timeWindow: "1 minute" } } }, async () => {
    const probe = new ContextProbe(context.config, context.provider, context.logger);
    return probe.readAll();
  });

  app.post("/admin/probe/context", { config: { rateLimit: { max: 2, timeWindow: "1 hour" } } }, async (request) => {
    const body = request.body as Record<string, unknown>;
    const mode = body.mode as DeepSeekMode;
    if (mode === "fast") {
      throw new AppError(400, "unsupported_mode", "The fast mode is disabled; use expert or vision.");
    }
    if (mode !== "expert" && mode !== "vision") {
      throw new AppError(400, "invalid_mode", "mode must be expert or vision.");
    }
    const probe = new ContextProbe(context.config, context.provider, context.logger);
    return probe.run({
      mode,
      minChars: typeof body.min_chars === "number" ? body.min_chars : undefined,
      maxChars: typeof body.max_chars === "number" ? body.max_chars : undefined,
      thinking: typeof body.thinking === "boolean" ? body.thinking : undefined,
      webSearch: typeof body.web_search === "boolean" ? body.web_search : undefined
    });
  });
}
