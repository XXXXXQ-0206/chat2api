import { createReadStream } from "node:fs";
import type { FastifyInstance } from "fastify";
import type { ApiContext } from "./common.js";
import { AppError } from "../utils/errors.js";

export function registerFileRoutes(app: FastifyInstance, context: ApiContext): void {
  app.post("/v1/files", { config: { rateLimit: { max: 20, timeWindow: "1 minute" } } }, async (request) => {
    if (request.isMultipart()) {
      const part = await request.file();
      if (!part) throw new AppError(400, "file_missing", "Expected multipart field containing a file.");
      const record = await context.fileStore.saveMultipart(part);
      return {
        id: record.id,
        object: "file",
        bytes: record.size,
        filename: record.filename,
        purpose: "assistants",
        created_at: Math.floor(new Date(record.createdAt).getTime() / 1000)
      };
    }

    const body = request.body as Record<string, unknown>;
    if (typeof body?.path !== "string") {
      throw new AppError(400, "file_missing", "Send multipart/form-data or JSON {\"path\":\"/local/file\"}.");
    }
    const record = await context.fileStore.saveLocalPath(body.path, typeof body.mime_type === "string" ? body.mime_type : undefined);
    return {
      id: record.id,
      object: "file",
      bytes: record.size,
      filename: record.filename,
      purpose: "assistants",
      created_at: Math.floor(new Date(record.createdAt).getTime() / 1000)
    };
  });

  app.get("/v1/files/:id", async (request) => {
    const params = request.params as { id: string };
    const record = await context.fileStore.get(params.id);
    return {
      id: record.id,
      object: "file",
      bytes: record.size,
      filename: record.filename,
      purpose: "assistants",
      created_at: Math.floor(new Date(record.createdAt).getTime() / 1000)
    };
  });

  app.get("/v1/files/:id/content", async (request, reply) => {
    const params = request.params as { id: string };
    const record = await context.fileStore.get(params.id);
    if (record.mimeType) reply.header("content-type", record.mimeType);
    return reply.send(createReadStream(record.path));
  });
}
