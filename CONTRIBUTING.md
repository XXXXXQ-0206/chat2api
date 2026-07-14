# Contributing to chat2api

Thanks for contributing. This repository uses a pull-request workflow: do not push changes directly to `main`.

## Before You Start

- Read the [security policy](SECURITY.md) and [code of conduct](CODE_OF_CONDUCT.md).
- Use issues for reproducible bugs or scoped proposals before beginning large changes.
- Never submit account credentials, browser profiles, session storage, diagnostics, private prompts, local paths, or personal data.
- Keep browser-provider traffic low and manual. Tests and CI must use the mock provider unless a maintainer explicitly authorizes an isolated acceptance run.

## Development Setup

```bash
npm ci
npm test
npm run build
dotnet build dotnet/Chat2Api.Host/Chat2Api.Host.csproj -c Release
```

On Windows, also run:

```powershell
dotnet run --project windows/Chat2ApiTray.Tests/Chat2ApiTray.Tests.csproj -c Release
```

## Pull Requests

1. Branch from `dev` unless the issue specifies another integration branch.
2. Keep one focused change per pull request and include tests for observable behavior.
3. Run the relevant Node and .NET checks before requesting review.
4. Describe compatibility, security, and documentation effects in the pull-request template.
5. Use conventional commit-style subjects, for example `fix: reject non-loopback host bindings`.

Maintainers may request a squash merge to preserve a readable history.

## Documentation

When changing user-facing behavior, update both `README.md` and `README.zh-CN.md`. Do not describe mock, local, cross-built, or source-compatibility evidence as real provider or native-platform validation.
