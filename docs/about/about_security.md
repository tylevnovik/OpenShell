---
topic: about_security
synopsis: Describes the current maturity of OpenShell security-sensitive subsystems.
---

# about_security

## SHORT DESCRIPTION

OpenShell is in an early alpha stage. Several security-sensitive subsystems
ship with development placeholders. This topic lists what is real, what is a
placeholder, and what to avoid until the gaps are closed.

## LONG DESCRIPTION

### Provider package signature verification

Package signature verification (ADR-0039) currently uses a development stub
(`NullSignatureVerifier`): a package with any attached signature is accepted
without cryptographic validation. Treat provider packages as untrusted code
and only install packages from sources you trust.

### Stored credentials

SFTP credentials recorded by the credential provider are stored as plain-text
JSON. Encrypted storage (DPAPI on Windows, keychain on Unix) is planned for a
later milestone. Avoid storing credentials for accounts with broad access.

### Secure password entry

The console password prompter reads input with a plain `ReadLine` fallback.
OS-native secure prompts (CredUI / Security.framework / terminal echo-off)
are planned replacements; until then, prefer credential files you manage
yourself over interactive prompts in shared environments.

### Update code-signature checks

On macOS the platform code-signature verifier is a placeholder that accepts
updates without validation. Verify update sources manually when using the
update service on macOS.

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
