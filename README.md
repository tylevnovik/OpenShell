# OpenShell

> Provider-aware command shell and file workspace — PowerShell-compatible syntax, a modern `.osh` syntax, and an Avalonia desktop GUI.
>
> OpenShell 是一个跨平台（Windows / Linux / macOS）的 .NET 命令行 Shell 与文件管理工作台：兼容 PowerShell 语法、提供现代 `.osh` 语法，并附带 Avalonia 图形宿主。当前处于 **0.1.0-alpha** 早期阶段。

## Highlights

- **Unified providers** — one path model over heterogeneous stores: `fs::`, `zip::`, `reg::` (Windows), `sftp::`, plus virtual `variable::`, `env::`, `function::` drives.
- **150+ built-in commands** — item manipulation, filtering (`Where`/`Select`/`Sort` with a filter DSL), formatting, undo/redo journal, favorites & recents, search, preview, and more.
- **Two syntaxes** — PowerShell-compatible mode (`#lang ps1`) and the modern `.osh` mode (default), switchable per session.
- **Desktop GUI** (`openshell-gui`) — tabbed file workspace with split view, breadcrumb navigation, details/preview panes, command palette, global search, light/dark themes, zh-CN/en-US localization.
- **Sessions & snapshots** — named workspaces persisted across restarts (tabs, locations, navigation history), crash detection, optional WebDAV sync.
- **Extensibility** — plugin/provider packages, IPC channel, structured logging & telemetry hooks.

## Security & maturity notice

OpenShell is alpha software. The following security-sensitive boundaries should be understood before using it in adversarial environments:

| Area | Status |
|------|--------|
| Provider package signature verification | CLI and dotnet tool use Ed25519 verification; NullSignatureVerifier remains test-only and must not be registered in production. |
| Stored SFTP credentials | Metadata is stored separately from secrets; Windows uses DPAPI and Unix uses an owner-protected encrypted secret file. |
| Secure password prompt | Interactive terminals suppress echo; redirected input uses an explicit non-interactive path. |
| macOS update code-signature check | Uses /usr/bin/codesign --verify --deep --strict; missing/invalid signatures are rejected. |
| WinRM remoting transport | Documented in ADR-0059 but not implemented. |

Run `Get-Help about_security` inside the shell (from a repository checkout) for details.

## Getting started

Requirements: **.NET SDK 10.0.x** (needed to consume the `.slnx` solution; projects target `net8.0`).

```bash
# Build everything
dotnet build OpenShell.slnx

# Run the CLI
dotnet run --project src/OpenShell.Cli.Host

# Run the GUI
dotnet run --project src/OpenShell.Gui.Host

# Full test suite
dotnet test OpenShell.slnx
```

Self-contained release binaries are produced by the `release.yml` workflow for six RIDs (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`) when a `v*` tag is pushed.

### Environment variables

- `OPENSHELL_HOME` — overrides the persistence root (default `~/.openshell`). All state (config, history, sessions, logs, caches) is redirected. Useful for tests and portable installs.

## Repository layout

| Path | Contents |
|------|----------|
| `src/OpenShell.Core` | Language, parser/evaluator, providers, commands, services |
| `src/OpenShell.Cli.Host` | Terminal host (`openshell-cli`) |
| `src/OpenShell.Gui.Host` | Avalonia desktop host (`openshell-gui`) |
| `src/OpenShell.Providers.*` | FileSystem / Archive / Registry / Remote(SFTP) / Variables |
| `tests/` | Unit, integration, compliance and E2E suites |
| `docs/architecture/` | ADRs (ADR-0001 …) |
| `docs/*-audit.md` / `docs/*-tasks.md` | Per-theme defect audits and tracked fix task lists |
| `docs/about/` | `Get-Help about_*` topics |

## Development workflow

Every fix theme follows the same loop: audit document → compliance-test baseline (unimplemented features skipped with `pending T-xxx`) → implementation → task list + audit updates → zero-warning build and full-suite verification. See `AGENTS.md` for the binding conventions.

CI builds and tests on Ubuntu, Windows, and macOS for every push/PR to `main`.
