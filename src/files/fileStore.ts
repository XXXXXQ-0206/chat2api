import { createReadStream, createWriteStream } from "node:fs";
import { mkdir, readFile, realpath, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { pipeline } from "node:stream/promises";
import type { MultipartFile } from "@fastify/multipart";
import type { Chat2ApiConfig, FileRecord, UploadedFileRef } from "../types.js";
import { AppError } from "../utils/errors.js";
import { id } from "../utils/ids.js";

export class FileStore {
  private readonly indexPath: string;

  constructor(private readonly config: Chat2ApiConfig) {
    this.indexPath = path.join(config.uploadDir, "files.json");
  }

  async saveMultipart(part: MultipartFile): Promise<FileRecord> {
    await mkdir(this.config.uploadDir, { recursive: true });
    const fileId = id("file");
    const safeName = sanitizeFilename(part.filename || "upload.bin");
    const target = path.join(this.config.uploadDir, `${fileId}-${safeName}`);
    await pipeline(part.file, createWriteStream(target));
    const saved = await stat(target);
    const record: FileRecord = {
      id: fileId,
      filename: safeName,
      path: target,
      mimeType: part.mimetype,
      size: saved.size,
      createdAt: new Date().toISOString()
    };
    await this.put(record);
    return record;
  }

  async saveLocalPath(localPath: string, mimeType?: string): Promise<FileRecord> {
    const dataDirectory = await realpath(this.config.dataDir);
    const requestedFilename = dataRootFilename(localPath, dataDirectory);
    const sourcePath = await realpath(path.join(dataDirectory, requestedFilename));
    if (!isPathWithinDirectory(dataDirectory, sourcePath)) {
      throw new AppError(400, "file_path_not_allowed", "JSON file paths must name a file directly inside the chat2api data directory. Use multipart upload for external files.");
    }

    const filename = sanitizeFilename(path.basename(sourcePath));
    const fileId = id("file");
    const target = path.join(this.config.uploadDir, `${fileId}-${filename}`);
    await pipeline(createReadStream(sourcePath), createWriteStream(target));
    const saved = await stat(target);
    const record: FileRecord = {
      id: fileId,
      filename,
      path: target,
      mimeType,
      size: saved.size,
      createdAt: new Date().toISOString()
    };
    await this.put(record);
    return record;
  }

  async get(id: string): Promise<FileRecord> {
    const all = await this.all();
    const record = all.find((file) => file.id === id);
    if (!record) throw new AppError(404, "file_not_found", `File ${id} was not found.`);
    return record;
  }

  async resolveMany(ids: string[] = []): Promise<UploadedFileRef[]> {
    const files = await Promise.all(ids.map((fileId) => this.get(fileId)));
    return files.map((file) => ({
      id: file.id,
      filename: file.filename,
      path: file.path,
      mimeType: file.mimeType,
      size: file.size
    }));
  }

  async all(): Promise<FileRecord[]> {
    try {
      return JSON.parse(await readFile(this.indexPath, "utf8")) as FileRecord[];
    } catch {
      return [];
    }
  }

  private async put(record: FileRecord): Promise<void> {
    const records = await this.all();
    records.push(record);
    await writeFile(this.indexPath, JSON.stringify(records, null, 2), { mode: 0o600 });
  }
}

function sanitizeFilename(filename: string): string {
  return filename.replace(/[^a-zA-Z0-9._-]+/g, "_").slice(0, 160) || "upload.bin";
}

function dataRootFilename(value: string, dataDirectory: string): string {
  const filename = path.basename(value);
  const directFilename = !path.isAbsolute(value) && value === filename;
  const directDataRootPath = path.isAbsolute(value) && path.relative(dataDirectory, path.dirname(path.resolve(value))) === "";
  if ((directFilename || directDataRootPath) && filename === sanitizeFilename(filename) && filename !== "." && filename !== "..") {
    return filename;
  }

  throw new AppError(400, "file_path_not_allowed", "JSON file paths must name a file directly inside the chat2api data directory. Use multipart upload for external files.");
}

function isPathWithinDirectory(directory: string, candidate: string): boolean {
  const relative = path.relative(directory, candidate);
  return relative.length > 0 && relative !== ".." && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
}
