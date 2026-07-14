# Security Policy

## Supported Version

Security fixes are applied to the latest `main` branch and the latest tagged release when practical.

## Reporting a Vulnerability

Do not disclose a suspected vulnerability in a public issue. Use GitHub's private security-advisory reporting for this repository. Include a minimal reproduction, affected component, impact, and suggested mitigation. Do not attach browser profiles, credentials, session files, raw prompts, or private diagnostics.

If private reporting is unavailable, open a minimal public issue asking a maintainer for a secure reporting channel without revealing exploit details.

## Security Model

chat2api is a local bridge, not a security boundary for other local processes. The supported network posture is loopback-only binding. Deployments outside loopback require independently designed authentication, authorization, TLS, rate limiting, and network controls.

The project does not collect provider passwords. Browser profiles, uploaded files, persisted conversations, summaries, vector data, and diagnostics can still be sensitive local data. Restrict their filesystem permissions, enable state encryption where supported, and never commit or publish them.

Logs redact authorization values, bearer tokens, cookies, known secret fields, prompts, and request bodies. Treat diagnostics as sensitive even when redaction is enabled.

## Security Automation

- CodeQL analyzes JavaScript/TypeScript and C# on pull requests, protected branches, and a schedule.
- Dependabot monitors npm, NuGet, and GitHub Actions dependencies.
- Dependency Review runs on pull requests.
- Secret Scanning and push protection should be enabled in repository settings whenever GitHub makes them available.

## Out Of Scope

Provider web-product changes, account restrictions, third-party browser extensions, and user-controlled external tool executors are not vulnerabilities in this project by themselves. Reports showing an implementation flaw in chat2api's authentication, authorization, data handling, request parsing, file access, or tool boundary remain in scope.
