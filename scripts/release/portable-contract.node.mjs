import assert from "node:assert/strict";
import test from "node:test";

import {
  launcherFileName,
  launcherScript,
  assertRuntimeMatchesTarget,
  targetForRuntime,
  targetRuntime
} from "./build-portable.mjs";
import { launcherCommand } from "./verify-portable.mjs";

test("maps every supported portable target to its native runtime", () => {
  assert.deepEqual(targetRuntime("node20-win-x64"), { platform: "win32", arch: "x64" });
  assert.deepEqual(targetRuntime("node20-macos-x64"), { platform: "darwin", arch: "x64" });
  assert.deepEqual(targetRuntime("node20-macos-arm64"), { platform: "darwin", arch: "arm64" });
  assert.deepEqual(targetRuntime("node20-linux-x64"), { platform: "linux", arch: "x64" });
  assert.equal(targetForRuntime("darwin", "arm64"), "node20-macos-arm64");
  assert.throws(
    () => assertRuntimeMatchesTarget("node20-macos-x64", "darwin", "arm64"),
    /requires darwin-x64, but this runner provides darwin-arm64/
  );
});

test("creates launchers that execute only the bundled Node runtime", () => {
  assert.equal(launcherFileName("win32"), "chat2api.cmd");
  assert.match(launcherScript("win32"), /runtime\\node\.exe/);
  assert.match(launcherScript("win32"), /app\\dist\\index\.js/);

  assert.equal(launcherFileName("linux"), "chat2api");
  assert.match(launcherScript("linux"), /runtime\/node/);
  assert.match(launcherScript("linux"), /app\/dist\/index\.js/);
  assert.match(launcherScript("linux"), /serve "\$@"/);
});

test("starts Windows command launchers through the Windows shell", () => {
  assert.deepEqual(
    launcherCommand("win32", "portable/chat2api.cmd", ["--provider", "mock"]),
    {
      command: "portable/chat2api.cmd",
      args: ["--provider", "mock"],
      shell: true
    }
  );
});
