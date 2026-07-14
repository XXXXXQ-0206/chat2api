# chat2api

[English](README.md) | [中文](README.zh-CN.md)

`chat2api` is a local API bridge for a user-controlled DeepSeek web session. It provides OpenAI Chat Completions, Anthropic Messages, and OpenAI Responses-compatible endpoints for local agent clients such as Codex and Claude Code.

> This is an independent project and is not affiliated with, endorsed by, or supported by DeepSeek.

## Overview

chat2api keeps browser login under the user's control, translates standard API requests into a browser-backed provider session, and normalizes the result into familiar agent protocols. A deterministic mock provider is included for local development and CI.

The service binds to loopback by default. It is intended for a single user's local development environment, not as an unauthenticated network service.

## Features

- OpenAI-compatible `POST /v1/chat/completions`, including JSON and SSE streaming.
- Anthropic-compatible `POST /v1/messages` and Responses-compatible `POST /v1/responses`.
- Manual browser login and local session handling without collecting account passwords.
- `expert` mode by default, `vision` mode for file or image inputs, and explicit `thinking` control.
- Stable `400 unsupported_mode` handling for the disabled `fast` mode.
- Multipart file uploads and file-ID references; image uploads automatically select `vision`.
- Tool-call envelope parsing, repair, and continuation for agent tool loops.
- Local Context Engine with persisted conversations, incremental summaries, SQLite vector retrieval, and token budgeting in the .NET runtime.
- Offline fuse, loopback-only binding validation, redacted logs, and local-data protections.
- A shared .NET Console Host for Windows, Linux, and macOS source builds.

`web_search` is an external tool/MCP contract. chat2api does not execute network searches and does not persist search results. The provider-facing webpage search switch remains disabled.

## Installation

### Prerequisites

- Node.js 20 or later for the Node host and test suite.
- .NET SDK 9.0 for the shared Console Host and Windows tray source.
- A supported local browser channel or Playwright Chromium for browser-backed use.

### Node Host

```bash
git clone https://github.com/XXXXXQ-0206/chat2api.git
cd chat2api
npm ci
npx playwright install chromium
```

Copy `.env.example` to `.env` only when local configuration is needed. Never commit `.env`, browser profiles, diagnostics, or session snapshots.

### Console Host

```bash
dotnet restore dotnet/Chat2Api.Host/Chat2Api.Host.csproj
dotnet build dotnet/Chat2Api.Host/Chat2Api.Host.csproj -c Release
```

## Usage

### Local Mock Mode

Mock mode does not open a browser or contact DeepSeek. It is the recommended first health check.

```powershell
$env:CHAT2API_PROVIDER = "mock"
npm run dev
```

The server listens on `http://127.0.0.1:8022` unless configured otherwise.

### Browser-Backed Mode

Set `CHAT2API_PROVIDER=browser`, start the server, and complete login only through the visible login command or endpoint. The browser session remains local to the configured data directory.

```bash
npm run build
node dist/index.js login
node dist/index.js serve
```

Do not expose the service beyond loopback without a separately designed authentication and network-security layer.

### OpenAI-Compatible Request

```bash
curl http://127.0.0.1:8022/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{
    "model": "deepseek-chat2api-expert",
    "messages": [{"role": "user", "content": "Explain a binary search."}],
    "chat2api": {"mode": "expert", "thinking": false}
  }'
```

Supported public model names:

- `deepseek-chat2api-expert`
- `deepseek-chat2api-vision`

`fast` is disabled. Requests that select it receive `400 unsupported_mode` rather than silently changing mode.

### Agent Configuration

- OpenAI-compatible clients: `base_url=http://127.0.0.1:8022/v1`
- Anthropic-compatible clients: `base_url=http://127.0.0.1:8022`
- API keys may be any non-empty placeholder when a client requires one; chat2api does not validate a remote API key.

Use `deepseek-chat2api-expert` for text or tool workflows. Use `deepseek-chat2api-vision` only when the request includes an image or file.

### Web-Search Tools

Requesting `web_search` declares a required tool for the model. A configured MCP server or tool executor performs the search and sends the normal tool result in a later request. See [the web-search tool contract](docs/WEB_SEARCH_TOOL_CONTRACT.md).

## Build Instructions

```bash
npm ci
npm test
npm run build
dotnet build dotnet/Chat2Api.Host/Chat2Api.Host.csproj -c Release
```

The Windows-only source test runner can be invoked on Windows:

```powershell
dotnet run --project windows/Chat2ApiTray.Tests/Chat2ApiTray.Tests.csproj -c Release
```

Release automation builds self-contained Console Host archives for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64` after a version tag is pushed.

## Windows Tray Source

`windows/Chat2ApiTray` is a single-process Windows tray implementation that owns the local API server and browser lifecycle. It does not start a separate Node process.

The Windows tray source is compiled with the shared Core, but the Windows tray package has not undergone standalone rigorous testing. Treat it as source available for evaluation, not as a separately validated release runtime.

## Project Structure

```text
src/                         Node API bridge and Playwright provider
tests/                       Node protocol, security, and documentation tests
dotnet/Chat2Api.Core/        Shared .NET protocol and context runtime
dotnet/Chat2Api.Host/        Cross-platform Console Host entry point
windows/Chat2ApiTray/        Windows tray source shell
windows/Chat2ApiTray.Tests/  Windows source test runner
docs/                        Public architecture, protocol, and release notes
.github/                     CI, CodeQL, Dependabot, and PR automation
```

## Roadmap

- Native smoke receipts on every supported platform.
- More precise context-capacity measurements.
- Configurable external search-executor integrations.
- Performance profiling and operational observability improvements.

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and [SECURITY.md](SECURITY.md) before opening an issue or pull request.

Never include browser profiles, session data, credentials, private prompts, diagnostics, or personal paths in an issue, test fixture, commit, or pull request.

## FAQ

### Is this an official DeepSeek API?

No. It adapts a locally logged-in browser session and can stop working when the web product, account state, or browser environment changes.

### Does it provide unlimited native model context?

No. The Context Engine manages longer local histories with summaries, retrieval, and budgets. Its managed-history goal is not a claim about a provider's native context limit.

### Can I expose the server on my LAN or the Internet?

Not safely without additional authentication, transport security, rate limiting, and network controls that you own and validate. The supported default is loopback-only use.

### Does chat2api perform web searches?

No. It only emits the `web_search` tool call. A caller-provided MCP server or tool executor must perform any network operation.

## Acknowledgements

This project uses Node.js, Fastify, Playwright, .NET, SQLite, sqlite-vec, and the OpenAI, Anthropic, and Responses protocol conventions. Their names and trademarks belong to their respective owners.

## Disclaimer

This software is provided **AS IS**, without warranty of any kind, express or implied. To the maximum extent permitted by law, the authors and contributors are not liable for any direct, indirect, incidental, special, consequential, or other damages arising from its use, inability to use it, data loss, account restrictions, service changes, or third-party actions.

You are solely responsible for your account, browser session, data, local security, and compliance with applicable law and third-party terms. Do not use this project to bypass access controls, evade rate limits or safety measures, automate abusive activity, violate platform rules, or perform any unlawful activity.

## License

Released under the [MIT License](LICENSE).
