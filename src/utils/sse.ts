import type { FastifyReply } from "fastify";

export function prepareSse(reply: FastifyReply): void {
  reply.raw.writeHead(200, {
    "content-type": "text/event-stream; charset=utf-8",
    "cache-control": "no-cache, no-transform",
    connection: "keep-alive",
    "x-accel-buffering": "no"
  });
}

export function writeData(reply: FastifyReply, data: unknown): void {
  reply.raw.write(`data: ${typeof data === "string" ? data : JSON.stringify(data)}\n\n`);
}

export function writeEvent(reply: FastifyReply, event: string, data: unknown): void {
  reply.raw.write(`event: ${event}\n`);
  writeData(reply, data);
}

export function endSse(reply: FastifyReply): void {
  reply.raw.end();
}
