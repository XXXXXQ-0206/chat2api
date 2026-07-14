import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import process from "node:process";

const VITEST_BIN = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "..",
  "node_modules",
  "vitest",
  "vitest.mjs"
);

const RUN_IN_BAND_FLAGS = ["--no-file-parallelism", "--maxWorkers=1"];

export function normalizeVitestArgs(args) {
  const normalized = [];

  for (const arg of args) {
    if (arg === "--runInBand" || arg === "-i") {
      normalized.push(...RUN_IN_BAND_FLAGS);
      continue;
    }

    if (arg.startsWith("--runInBand=")) {
      const value = arg.slice("--runInBand=".length);
      if (value === "" || value === "true") {
        normalized.push(...RUN_IN_BAND_FLAGS);
      }
      continue;
    }

    normalized.push(arg);
  }

  return normalized;
}

async function main() {
  const child = spawn(process.execPath, [VITEST_BIN, "run", ...normalizeVitestArgs(process.argv.slice(2))], {
    stdio: "inherit"
  });

  child.on("exit", (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
      return;
    }

    process.exit(code ?? 1);
  });
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  main().catch((error) => {
    console.error(error);
    process.exit(1);
  });
}
