import { access, chmod, cp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { arch, platform, version } from "node:process";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const portableTargets = {
  "node20-win-x64": { platform: "win32", arch: "x64" },
  "node20-macos-x64": { platform: "darwin", arch: "x64" },
  "node20-macos-arm64": { platform: "darwin", arch: "arm64" },
  "node20-linux-x64": { platform: "linux", arch: "x64" }
};

export function runtimeExecutableName(runtimePlatform = platform) {
  return runtimePlatform === "win32" ? "node.exe" : "node";
}

export function runtimeArchiveExtension(runtimePlatform = platform) {
  return runtimePlatform === "win32" ? "zip" : "tar.gz";
}

export function targetRuntime(target) {
  const runtime = portableTargets[target];
  if (!runtime) {
    throw new Error(`Unsupported portable target: ${target}`);
  }
  return runtime;
}

export function targetForRuntime(runtimePlatform = platform, runtimeArch = arch) {
  const target = Object.entries(portableTargets).find(([, runtime]) => (
    runtime.platform === runtimePlatform && runtime.arch === runtimeArch
  ));
  if (!target) {
    throw new Error(`No supported portable target for ${runtimePlatform}-${runtimeArch}`);
  }
  return target[0];
}

export function assertRuntimeMatchesTarget(target, runtimePlatform = platform, runtimeArch = arch) {
  const expected = targetRuntime(target);
  if (expected.platform !== runtimePlatform || expected.arch !== runtimeArch) {
    throw new Error(
      `Target ${target} requires ${expected.platform}-${expected.arch}, `
      + `but this runner provides ${runtimePlatform}-${runtimeArch}.`
    );
  }
}

export function launcherFileName(runtimePlatform = platform) {
  return runtimePlatform === "win32" ? "chat2api.cmd" : "chat2api";
}

export function launcherScript(runtimePlatform = platform) {
  const executableName = runtimeExecutableName(runtimePlatform);
  return runtimePlatform === "win32"
    ? `@echo off\r\n"%~dp0runtime\\${executableName}" "%~dp0app\\dist\\index.js" serve %*\r\n`
    : `#!/usr/bin/env sh\n"$(dirname "$0")/runtime/${executableName}" "$(dirname "$0")/app/dist/index.js" serve "$@"\n`;
}

function optionValue(args, name) {
  const index = args.indexOf(name);
  if (index < 0) return undefined;
  if (!args[index + 1]) throw new Error(`Missing value for ${name}`);
  return args[index + 1];
}

function outputDirectoryFromArgs(args, target) {
  const output = optionValue(args, "--output");
  return output
    ? resolve(output)
    : resolve(repositoryRoot, "release", `chat2api-portable-${target}`);
}

async function assertReleaseInputs() {
  await access(join(repositoryRoot, "dist", "index.js"));
  const packageJson = JSON.parse(await readFile(join(repositoryRoot, "package.json"), "utf8"));
  await Promise.all(Object.keys(packageJson.dependencies ?? {}).map((dependency) => (
    access(join(repositoryRoot, "node_modules", dependency, "package.json"))
  )));
}

async function main() {
  const args = process.argv.slice(2);
  const target = optionValue(args, "--target") ?? targetForRuntime();
  assertRuntimeMatchesTarget(target);
  await assertReleaseInputs();

  const outputDirectory = outputDirectoryFromArgs(args, target);
  const appDirectory = join(outputDirectory, "app");
  const runtimeDirectory = join(outputDirectory, "runtime");
  const executableName = runtimeExecutableName();
  const launcherName = launcherFileName();

  await rm(outputDirectory, { recursive: true, force: true });
  await mkdir(appDirectory, { recursive: true });
  await mkdir(runtimeDirectory, { recursive: true });
  await Promise.all([
    cp(join(repositoryRoot, "dist"), join(appDirectory, "dist"), { recursive: true }),
    cp(join(repositoryRoot, "node_modules"), join(appDirectory, "node_modules"), { recursive: true, dereference: true }),
    cp(join(repositoryRoot, "package.json"), join(appDirectory, "package.json")),
    cp(process.execPath, join(runtimeDirectory, executableName))
  ]);

  const launcher = join(outputDirectory, launcherName);
  await writeFile(launcher, launcherScript(), { mode: platform === "win32" ? undefined : 0o755 });
  if (platform !== "win32") {
    await Promise.all([
      chmod(join(runtimeDirectory, executableName), 0o755),
      chmod(launcher, 0o755)
    ]);
  }
  await writeFile(join(outputDirectory, "release.json"), JSON.stringify({
    target,
    platform,
    arch,
    node: version,
    entrypoint: `app/dist/index.js`,
    runtime: `runtime/${executableName}`,
    launcher: launcherName
  }, null, 2));
  process.stdout.write(`${outputDirectory}\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  main().catch((error) => {
    console.error(error);
    process.exit(1);
  });
}
