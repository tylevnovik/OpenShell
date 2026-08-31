# Reliability and Security Hardening Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make the remaining search, credential, trust, installation, and GUI reliability paths fail safely and prove them with behavior-level tests.

**Architecture:** Keep Core platform-neutral at the contracts and service boundaries. Add an index lifecycle coordinator that owns load/refresh/rebuild and make Search-Global use a scoped indexed query only when the index is ready; otherwise use the existing provider fallback. Keep production cryptographic implementations explicit in host DI, isolate development stubs to tests, and stage provider installation before atomic activation.

**Tech Stack:** .NET 8, C#, Microsoft.Extensions.DependencyInjection, SQLite/FTS5, BouncyCastle Ed25519, Avalonia/ReactiveUI, xUnit, FluentAssertions.

---

### Task 1: Establish the improvement audit and compliance baseline

**Files:**
- Create: `docs/improvement-hardening-audit.md`
- Create: `docs/improvement-hardening-tasks.md`
- Create: `tests/OpenShell.Core.Tests/ImprovementHardeningComplianceTests.cs`

**Step 1:** Record the current findings and mark not-yet-implemented behavior as skipped compliance tests.

**Step 2:** Run the focused compliance class and confirm the baseline only skips pending IH tasks.

### Task 2: Repair indexed global search

**Files:**
- Modify: `src/OpenShell.Core/Preview/FileIndexStore.cs`
- Modify: `src/OpenShell.Core/Commands/Builtins/GlobalSearchCommand.cs`
- Modify: `src/OpenShell.Core/Preview/PreviewServiceCollectionExtensions.cs`
- Create or modify: `src/OpenShell.Core/Preview/FileIndexLifecycleService.cs`
- Test: `tests/OpenShell.Core.Tests/Preview/FileIndexStoreTests.cs`
- Test: `tests/OpenShell.Core.Tests/Commands/GlobalSearchCommandTests.cs`

**Steps:**

1. Remove the pending skips for empty-index fallback, scoped search, duplicate names, rename/upsert, and delete synchronization.
2. Run the focused tests and confirm the baseline failures identify the current index path.
3. Add a stable relation between the FTS row and `files.path`, and make Upsert/Delete transactional for both tables.
4. Add a path-scoped FTS query and use it from `GlobalSearchCommand`.
5. Add a lifecycle service that loads the persistent index, refreshes configured filesystem roots, and rebuilds SQLite from the indexer.
6. Make an empty or unavailable index fall back to provider enumeration.
7. Run the focused tests and then the Core test project.

### Task 3: Harden credentials and secure input

**Files:**
- Create: `src/OpenShell.Core/Security/ISecretStore.cs`
- Create: platform secret-store implementations under `src/OpenShell.Core/Security/`
- Modify: `src/OpenShell.Providers.Remote/InMemoryCredentialProvider.cs`
- Modify: `src/OpenShell.Core/Security/ConsoleSecurePasswordPrompter.cs`
- Test: `tests/OpenShell.Core.Tests/Security/SecurePasswordPrompterTests.cs`
- Test: `tests/OpenShell.Providers.Remote.Tests/InMemoryCredentialProviderTests.cs`

**Steps:**

1. Define a secret-store contract that never exposes serialized plaintext as the persistence format.
2. Implement Windows DPAPI and a Unix protected-file fallback with owner-only permissions; keep test injection available.
3. Implement console echo suppression with cancellation and redirected-stdin handling.
4. Surface load/save failures without writing secret values to logs.
5. Add tests for round-trip, no-plaintext persistence, cancellation, and non-interactive behavior.

### Task 4: Close production trust and install transaction gaps

**Files:**
- Modify: `src/OpenShell.Core/Packaging/Signing/NullSignatureVerifier.cs`
- Modify: `src/OpenShell.Core/Updates/PlatformCodeSignatureVerifier.cs`
- Modify: `src/OpenShell.Core/Plugins/PluginLoader.cs`
- Modify: `src/OpenShell.Core/Packaging/Installation/ProviderInstaller.cs`
- Modify: host registrations in `src/OpenShell.Cli.Host/Program.cs` and `src/OpenShell.Gui.Host/AppBuilder.cs`
- Test: `tests/OpenShell.Core.Tests/Packaging/SignatureVerifierTests.cs`
- Test: `tests/OpenShell.Core.Tests/Packaging/ProviderInstallerTests.cs`

**Steps:**

1. Make the null verifier explicitly test-only and ensure all production hosts resolve Ed25519.
2. Make unsupported platform code-signature checks fail closed for automatic updates.
3. Require an explicit sandbox declaration or use a restricted default for third-party plugins.
4. Stage package extraction and atomically activate only after manifest, payload, API, and configuration checks pass.
5. Add rollback tests for extraction/current-link/configuration failures.

### Task 5: Complete search UI and lifecycle-safe windows

**Files:**
- Modify: `src/OpenShell.Gui.Host/ViewModels/GlobalSearchViewModel.cs`
- Modify: `src/OpenShell.Gui.Host/Views/GlobalSearchWindow.cs`
- Modify: `src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs`
- Test: `tests/OpenShell.Gui.Host.Tests/LatestProjectComplianceTests.cs`

**Steps:**

1. Add content-search and cancellation state to the ViewModel and bind it to the window.
2. Dispose the search ViewModel when its window closes.
3. Give new windows an independent workspace lifecycle and test closing either window.
4. Run GUI headless tests and record the remaining real-desktop screenshot boundary.

### Task 6: Verify and update external tracking

**Files:**
- Modify: `docs/improvement-hardening-audit.md`
- Modify: `docs/improvement-hardening-tasks.md`

**Steps:**

1. Run `dotnet build OpenShell.slnx --no-restore`.
2. Run `dotnet test OpenShell.slnx --no-build --no-restore -m:1`.
3. Run `git diff --check` and review only intended files.
4. Record exact pass/skip/failure counts and any platform/manual limits.

