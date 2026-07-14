import { spawn } from "node:child_process";
import { access, readFile, rm, stat } from "node:fs/promises";
import { platform } from "node:process";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { targetRuntime } from "./build-portable.mjs";

async function waitForHealth(url, child) {
  const deadline = Date.now() + 15000;
  let lastError;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) {
      throw new Error(`Portable launcher exited before health check with code ${child.exitCode}.`);
    }
    try {
      const response = await fetch(url);
      if (response.ok) return response;
      lastError = new Error(`health returned ${response.status}`);
    } catch (error) {
      lastError = error;
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 200));
  }
  throw lastError ?? new Error("Timed out waiting for portable package health endpoint.");
}

export function launcherCommand(runtimePlatform, launcher, args) {
  if (runtimePlatform !== "win32") return { command: launcher, args, shell: false };
  return { command: launcher, args, shell: true };
}

async function stopProcess(child) {
  if (child.exitCode === null && child.pid) {
    if (platform === "win32") {
      await new Promise((resolveTaskkill) => {
        spawn("taskkill", ["/pid", String(child.pid), "/t", "/f"], { stdio: "ignore" })
          .once("close", resolveTaskkill)
          .once("error", resolveTaskkill);
      });
    } else {
      process.kill(-child.pid, "SIGTERM");
    }
  }
  await Promise.race([
    new Promise((resolveExit) => child.once("exit", resolveExit)),
    new Promise((resolveDelay) => setTimeout(resolveDelay, 3000))
  ]);
}

async function main() {
  const directory = resolve(process.argv[2] ?? "");
  if (!directory) throw new Error("Pass a portable release directory.");
  const manifest = JSON.parse(await readFile(join(directory, "release.json"), "utf8"));
  const expectedRuntime = targetRuntime(manifest.target);
  if (expectedRuntime.platform !== platform || expectedRuntime.arch !== process.arch) {
    throw new Error(`Portable target ${manifest.target} cannot be smoke-tested on ${platform}-${process.arch}.`);
  }
  const runtime = join(directory, manifest.runtime);
  const entrypoint = join(directory, manifest.entrypoint);
  const launcher = join(directory, manifest.launcher);
  await Promise.all([access(runtime), access(entrypoint), access(launcher)]);
  if (platform !== "win32") {
    const [runtimeStats, launcherStats] = await Promise.all([stat(runtime), stat(launcher)]);
    if ((runtimeStats.mode & 0o111) === 0 || (launcherStats.mode & 0o111) === 0) {
      throw new Error("Portable runtime and launcher must both be executable.");
    }
  }
  const port = Number(process.env.CHAT2API_PACKAGE_TEST_PORT ?? 18023);
  const dataDirectory = join(tmpdir(), `chat2api-portable-smoke-${process.pid}`);
  const launch = launcherCommand(platform, launcher, ["--host", "127.0.0.1", "--port", String(port), "--provider", "mock"]);
  process.stdout.write("Portable smoke mode: restricted mock-provider HTTP verification; no DeepSeek login or upstream request is made.\n");
  const child = spawn(launch.command, launch.args, {
    cwd: directory,
    env: { ...process.env, CHAT2API_DATA_DIR: dataDirectory, CHAT2API_LOG_LEVEL: "error" },
    stdio: "inherit",
    shell: launch.shell,
    detached: platform !== "win32"
  });

  try {
    const health = await (await waitForHealth(`http://127.0.0.1:${port}/health`, child)).json();
    if (health.ok !== true || health.provider !== "mock") throw new Error(`Unexpected health: ${JSON.stringify(health)}`);
    const modelsResponse = await fetch(`http://127.0.0.1:${port}/v1/models`);
    const models = await modelsResponse.json();
    if (!modelsResponse.ok || !Array.isArray(models.data) || models.data.length < 3) throw new Error(`Unexpected models: ${JSON.stringify(models)}`);
  } finally {
    await stopProcess(child);
    await rm(dataDirectory, { recursive: true, force: true });
  }

  process.stdout.write("Portable smoke passed: launcher -> bundled Node -> mock HTTP health and model routes.\n");
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  main().catch((error) => {
    console.error(error);
    process.exit(1);
  });
}
