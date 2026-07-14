# chat2api

[English](README.md) | [中文](README.zh-CN.md)

`chat2api` 是一个面向本地 agent 客户端的 DeepSeek 网页会话 API 桥接服务。它为 Codex、Claude Code 等客户端提供 OpenAI Chat Completions、Anthropic Messages 和 OpenAI Responses 兼容接口。

> 本项目为独立项目，与 DeepSeek 不存在隶属、合作、认可或支持关系。

## 概述

chat2api 始终由用户自行完成浏览器登录，将标准 API 请求转换为浏览器 provider 会话请求，再将结果规范化为常见 agent 协议。项目也提供确定性的 mock provider，用于本地开发和 CI。

服务默认仅绑定回环地址，面向单用户本地开发环境，不是一个未经认证的网络服务。

## 功能

- OpenAI 兼容的 `POST /v1/chat/completions`，支持 JSON 与 SSE 流式响应。
- Anthropic 兼容的 `POST /v1/messages` 与 Responses 兼容的 `POST /v1/responses`。
- 用户手动完成浏览器登录，项目不采集账号密码。
- 默认 `expert` 模式，文件或图片使用 `vision`，并可显式控制 `thinking`。
- 已停用的 `fast` 模式稳定返回 `400 unsupported_mode`。
- multipart 文件上传和文件 ID 引用；图片上传会自动选择 `vision`。
- 面向 agent 工具循环的工具调用封包解析、修复和结果续传。
- .NET 运行时中的本地 Context Engine：持久化会话、增量摘要、SQLite 向量检索和 token 预算。
- 离线 fuse、仅回环绑定校验、日志脱敏与本地数据保护。
- Windows、Linux、macOS 源码构建共用的 .NET Console Host。

`web_search` 是外部工具/MCP 契约。chat2api 不执行网络搜索，也不保存搜索结果；面向 provider 的网页联网开关始终关闭。

## 安装

### 前置条件

- Node.js 20 或更高版本，用于 Node host 与测试套件。
- .NET SDK 9.0，用于共享 Console Host 与 Windows 托盘源码。
- 浏览器 provider 需要本机浏览器通道或 Playwright Chromium。

### Node Host

```bash
git clone https://github.com/XXXXXQ-0206/chat2api.git
cd chat2api
npm ci
npx playwright install chromium
```

只有在需要本地配置时才复制 `.env.example` 为 `.env`。不要提交 `.env`、浏览器 profile、诊断文件或会话快照。

### Console Host

```bash
dotnet restore dotnet/Chat2Api.Host/Chat2Api.Host.csproj
dotnet build dotnet/Chat2Api.Host/Chat2Api.Host.csproj -c Release
```

## 使用

### 本地 Mock 模式

Mock 模式不会打开浏览器，也不会访问 DeepSeek，适合作为首次健康检查。

```powershell
$env:CHAT2API_PROVIDER = "mock"
npm run dev
```

除非另行配置，服务监听 `http://127.0.0.1:8022`。

### 浏览器 Provider 模式

设置 `CHAT2API_PROVIDER=browser`，启动服务后只通过可见的登录命令或端点完成登录。浏览器会话只保存在配置的数据目录中。

```bash
npm run build
node dist/index.js login
node dist/index.js serve
```

除非自行设计并验证认证和网络安全层，否则不要将服务暴露到回环地址之外。

### OpenAI 兼容请求

```bash
curl http://127.0.0.1:8022/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{
    "model": "deepseek-chat2api-expert",
    "messages": [{"role": "user", "content": "解释二分查找。"}],
    "chat2api": {"mode": "expert", "thinking": false}
  }'
```

公开支持的模型名：

- `deepseek-chat2api-expert`
- `deepseek-chat2api-vision`

`fast` 已停用。请求该模式将收到 `400 unsupported_mode`，不会静默改变模式。

### Agent 配置

- OpenAI 兼容客户端：`base_url=http://127.0.0.1:8022/v1`
- Anthropic 兼容客户端：`base_url=http://127.0.0.1:8022`
- 如果客户端要求 API key，可填任意非空占位符；chat2api 不会校验远端 API key。

普通文本和工具工作流使用 `deepseek-chat2api-expert`。只有请求包含图片或文件时才使用 `deepseek-chat2api-vision`。

### 搜索工具

请求 `web_search` 会为模型声明一个必选工具。MCP 服务或工具执行器负责搜索，并在后续请求中回传标准工具结果。OpenAI、Anthropic 与 Responses 的具体格式见[联网工具契约](docs/WEB_SEARCH_TOOL_CONTRACT.md)。

## 构建说明

```bash
npm ci
npm test
npm run build
dotnet build dotnet/Chat2Api.Host/Chat2Api.Host.csproj -c Release
```

Windows 环境可以运行 Windows 源码测试：

```powershell
dotnet run --project windows/Chat2ApiTray.Tests/Chat2ApiTray.Tests.csproj -c Release
```

推送版本标签后，发布自动化会构建 `win-x64`、`linux-x64`、`osx-x64`、`osx-arm64` 的自包含 Console Host 压缩包。

## Windows 托盘源码

`windows/Chat2ApiTray` 是一个单进程 Windows 托盘实现，负责本地 API 服务和浏览器生命周期，且不会再启动独立 Node 进程。

Windows 托盘源码与共享 Core 一同编译，但 Windows 托盘包尚未严格测试。请把它视为可评估的源码，而不是单独完成验证的发布运行时。

## 项目结构

```text
src/                         Node API 桥接与 Playwright provider
tests/                       Node 协议、安全与文档测试
dotnet/Chat2Api.Core/        共享 .NET 协议与上下文运行时
dotnet/Chat2Api.Host/        跨平台 Console Host 入口
windows/Chat2ApiTray/        Windows 托盘源码壳
windows/Chat2ApiTray.Tests/  Windows 源码测试入口
docs/                        公开架构、协议与发布说明
.github/                     CI、CodeQL、Dependabot 与 PR 自动化
```

## 路线图

- 补齐各受支持平台的原生冒烟收据。
- 提高上下文容量边界测量精度。
- 接入可配置的外部搜索执行器。
- 持续进行性能分析和可观测性优化。

## 贡献

欢迎贡献。发起 issue 或 Pull Request 前，请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)、[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) 和 [SECURITY.md](SECURITY.md)。

请勿在 issue、测试夹具、提交或 Pull Request 中放入浏览器 profile、会话数据、凭据、私有 prompt、诊断内容或个人路径。

## 常见问题

### 这是官方 DeepSeek API 吗？

不是。它适配本地已登录的浏览器会话。网页产品、账号状态或浏览器环境变化都有可能影响可用性。

### 它是否提供无限的原生模型上下文？

不是。Context Engine 通过摘要、检索和预算管理较长本地历史；其受管历史目标不代表 provider 的原生上下文上限。

### 可以将服务暴露到局域网或互联网吗？

在没有自行设计并验证认证、传输安全、限流和网络控制的情况下不安全。支持的默认方式是仅回环使用。

### chat2api 会自己执行网页搜索吗？

不会。它只产生 `web_search` 工具调用；任何网络操作都由调用方提供的 MCP 服务或工具执行器完成。

## 致谢

本项目使用 Node.js、Fastify、Playwright、.NET、SQLite、sqlite-vec，以及 OpenAI、Anthropic 和 Responses 协议约定。相关名称和商标归其各自所有者所有。

## 免责声明

本软件按 **AS IS** 原样提供，不提供任何明示或默示保证。在法律允许的最大范围内，作者和贡献者不对因使用或无法使用本软件、数据丢失、账号限制、服务变化或第三方行为产生的任何直接、间接、附带、特殊、后果性或其他损失承担责任。

你应自行对账号、浏览器会话、数据、本地安全以及遵守适用法律和第三方条款负责。不得使用本项目绕过访问控制、规避限流或安全措施、自动化滥用行为、违反平台规则或实施任何违法活动。

## 许可证

本项目以 [MIT License](LICENSE) 发布。
