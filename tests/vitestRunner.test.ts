import { describe, expect, it } from "vitest";

describe("vitest runner", () => {
  it("translates Jest-style runInBand into Vitest serial execution flags", async () => {
    const { normalizeVitestArgs } = await import("../scripts/vitest-runner.mjs");

    expect(normalizeVitestArgs(["--runInBand", "tests/routes.test.ts"])).toEqual([
      "--no-file-parallelism",
      "--maxWorkers=1",
      "tests/routes.test.ts"
    ]);
  });

  it("leaves regular Vitest arguments untouched", async () => {
    const { normalizeVitestArgs } = await import("../scripts/vitest-runner.mjs");

    expect(normalizeVitestArgs(["tests/tooling.test.ts", "--reporter=verbose"])).toEqual([
      "tests/tooling.test.ts",
      "--reporter=verbose"
    ]);
  });

  it("uses native Node runtime names for portable releases", async () => {
    const { runtimeExecutableName, runtimeArchiveExtension } = await import("../scripts/release/build-portable.mjs");

    expect(runtimeExecutableName("win32")).toBe("node.exe");
    expect(runtimeExecutableName("darwin")).toBe("node");
    expect(runtimeArchiveExtension("win32")).toBe("zip");
    expect(runtimeArchiveExtension("linux")).toBe("tar.gz");
  });
});
