---
topic: about_security
synopsis: Describes the current maturity of OpenShell security-sensitive subsystems.
---

# about_security

## SHORT DESCRIPTION

OpenShell is in an early alpha stage. Security-sensitive paths are fail-closed
where the platform capability is available; this topic lists the remaining
limitations and the validation boundaries.

## LONG DESCRIPTION

### Provider package signature verification

The CLI and dotnet tool register Ed25519SignatureVerifier, which validates the
detached signature against the package payload hash. NullSignatureVerifier is
retained for isolated tests and development fixtures only; production hosts
must not register it. Unsigned packages still require a trusted source or an
explicit matching trust key.

### Stored credentials

SFTP metadata is stored in JSON without password/passphrase values. Secrets are
stored in a separate encrypted store: Windows uses DPAPI and Unix uses an
owner-protected AES-GCM file/key pair. Keep the OpenShell data directory private.

### Secure password entry

Interactive terminal input disables echo and handles cancellation; redirected
input is explicitly treated as non-interactive and reads a line. This is not
an OS-native credential dialog, so avoid entering secrets where the controlling
terminal or parent process is not trusted.

### Update code-signature checks

On macOS the platform verifier invokes codesign --verify --deep --strict and
rejects a missing tool or a non-zero verification result. Linux has no single
portable platform signature API; update integrity there depends on the
published SHA-256 digest when one is supplied.

### Remoting

SSH-based remoting (SFTP provider and SSH transport foundations) is
implemented; the WinRM transport described in ADR-0059 is documented but not
implemented.

## RECOMMENDATIONS

- Do not install provider packages from untrusted sources.
- Keep `~/.openshell` (or `OPENSHELL_HOME`) private; session and credential
  files live there.
- Run the shell under a standard (non-administrative) account for everyday
  work; use `Start-Process -Verb RunAs` style elevation only when needed.

## SEE ALSO

- about_providers
- about_remote
