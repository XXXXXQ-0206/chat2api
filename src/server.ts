import cors from "@fastify/cors";
import multipart from "@fastify/multipart";
import Fastify, { type FastifyInstance } from "fastify";
import type { Logger } from "pino";
import { registerAnthropicRoutes } from "./api/anthropic.js";
import { registerAuthRoutes } from "./api/auth.js";
import type { ApiContext } from "./api/common.js";
import { registerFileRoutes } from "./api/files.js";
import { registerHealthRoutes } from "./api/health.js";
import { registerOpenAiRoutes } from "./api/openai.js";
import { registerProbeRoutes } from "./api/probe.js";
import { registerResponsesRoutes } from "./api/responses.js";
import { loadConfig } from "./config.js";
import { FileStore } from "./files/fileStore.js";
import { createLogger } from "./logger.js";
import { createProvider } from "./provider/provider.js";
import type { Chat2ApiConfig } from "./types.js";
import { AppError, toErrorMessage } from "./utils/errors.js";

export interface ServerBundle {
  app: FastifyInstance;
  config: Chat2ApiConfig;
  logger: Logger;
}

export async function createServer(config: Chat2ApiConfig = loadConfig()): Promise<ServerBundle> {
  const logger = createLogger(config);
  const provider = createProvider(config, logger);
  const fileStore = new FileStore(config);
  const app = Fastify({ logger: false, bodyLimit: 128 * 1024 * 1024 });
  const context: ApiContext = { config, logger, provider, fileStore };

  await app.register(cors, { origin: true });
  app.addContentTypeParser("application/x-www-form-urlencoded", { parseAs: "string" }, (_request, _body, done) => {
    done(null, {});
  });
  await app.register(multipart, {
    limits: {
      fileSize: 100 * 1024 * 1024,
      files: 20
    }
  });

  registerHealthRoutes(app, context);
  registerAuthRoutes(app, context);
  registerFileRoutes(app, context);
  registerOpenAiRoutes(app, context);
  registerAnthropicRoutes(app, context);
  registerResponsesRoutes(app, context);
  registerProbeRoutes(app, context);

  app.setErrorHandler((error, request, reply) => {
    const appError = error instanceof AppError ? error : undefined;
    const statusCode = appError?.statusCode ?? ("statusCode" in error && typeof error.statusCode === "number" ? error.statusCode : 500);
    const code = appError?.code ?? (statusCode === 500 ? "internal_error" : "request_error");
    logger.error({ code, statusCode, path: request.url, error: toErrorMessage(error) }, "request failed");
    reply.status(statusCode).send({
      error: {
        message: appError?.message ?? error.message,
        type: code,
        code,
        details: appError?.details
      }
    });
  });

  app.addHook("onClose", async () => {
    await provider.shutdown();
  });

  return { app, config, logger };
}
