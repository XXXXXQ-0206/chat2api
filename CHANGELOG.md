# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-07-15

### Added

- OpenAI Chat Completions, Anthropic Messages, and Responses-compatible local endpoints.
- Browser and mock providers, local file uploads, tool-call repair, and SSE forwarding.
- Shared .NET Console Host, Context Engine, SQLite vector retrieval, and offline protections.
- Public documentation, security policy, CodeQL, Dependabot, dependency review, and release automation.

### Changed

- `expert` is the default mode; `fast` is disabled and returns `400 unsupported_mode`.
- File and image requests use `vision`; `web_search` is an external tool/MCP contract.
