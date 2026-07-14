import { readFile } from "node:fs/promises";
import { describe, expect, it } from "vitest";

const publicDocuments = [
  "README.md",
  "README.zh-CN.md",
  "SECURITY.md",
  "CONTRIBUTING.md",
  "docs/WEB_SEARCH_TOOL_CONTRACT.md",
];

describe("public release documentation", () => {
  it("keeps English and Chinese README entry points synchronized", async () => {
    const english = await readFile("README.md", "utf8");
    const chinese = await readFile("README.zh-CN.md", "utf8");

    expect(english).toContain("[中文](README.zh-CN.md)");
    expect(chinese).toContain("[English](README.md)");
    expect(english).toContain("## Installation");
    expect(chinese).toContain("## 安装");
    expect(english).toContain("## Disclaimer");
    expect(chinese).toContain("## 免责声明");
  });

  it("documents disabled fast mode and the external search boundary", async () => {
    const english = await readFile("README.md", "utf8");
    const chinese = await readFile("README.zh-CN.md", "utf8");
    const contract = await readFile("docs/WEB_SEARCH_TOOL_CONTRACT.md", "utf8");

    expect(english).not.toContain("deepseek-chat2api-fast");
    expect(english).toContain("400 unsupported_mode");
    expect(english).toContain("does not execute network searches");
    expect(english).toContain("does not persist search results");
    expect(english).toContain("Per-route rate limits protect browser session management, file uploads, and context probes.");
    expect(chinese).toContain("按路由限流保护浏览器会话管理、文件上传和上下文探测。");
    expect(contract).toContain("OpenAI Chat Completions");
    expect(contract).toContain("Anthropic Messages");
    expect(contract).toContain("Responses API");
  });

  it("keeps the Windows tray package distinct from validated source builds", async () => {
    const english = await readFile("README.md", "utf8");
    const chinese = await readFile("README.zh-CN.md", "utf8");
    const security = await readFile("SECURITY.md", "utf8");

    expect(english).toContain("Windows tray package has not undergone standalone rigorous testing");
    expect(chinese).toContain("Windows 托盘包尚未严格测试");
    expect(security).toContain("loopback");
  });

  it("does not contain personal paths, credentials, or account-scoped release state", async () => {
    const documents = await Promise.all(publicDocuments.map((document) => readFile(document, "utf8")));

    for (const document of documents) {
      expect(document).not.toMatch(/C:\\\\Users\\|D:\\\\|account2|original-account|old-account|traffic_state|frozen/i);
      expect(document).not.toMatch(/ghp_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+|sk-[A-Za-z0-9_-]+/i);
    }
  });

  it("does not publish commands for omitted acceptance assets", async () => {
    const packageJson = JSON.parse(await readFile("package.json", "utf8"));

    expect(packageJson.scripts).not.toHaveProperty("acceptance:windows");
    expect(packageJson.scripts).not.toHaveProperty("acceptance:windows:dry-run");
    expect(packageJson.scripts.package).toContain("scripts/release/build-portable.mjs");
  });

  it("limits npm package contents to runtime assets and public documents", async () => {
    const packageJson = JSON.parse(await readFile("package.json", "utf8"));

    expect(packageJson.files).toEqual(["dist", "README.md", "README.zh-CN.md", "LICENSE"]);
  });

  it("keeps portable-release source scripts tracked while ignoring root release output", async () => {
    const gitignore = await readFile(".gitignore", "utf8");

    expect(gitignore).toContain("/release/");
    expect(gitignore).not.toMatch(/^release\/$/m);
  });

  it("uses CodeQL build modes supported by each scanned language", async () => {
    const workflow = await readFile(".github/workflows/codeql.yml", "utf8");

    expect(workflow).toMatch(/language: javascript-typescript\s+build-mode: none/);
    expect(workflow).toMatch(/language: csharp\s+build-mode: manual/);
  });
});
