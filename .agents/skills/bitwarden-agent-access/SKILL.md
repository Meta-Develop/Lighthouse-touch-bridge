---
name: bitwarden-agent-access
description: "Use whenever an Agent needs a password, username, TOTP, API key, login credential, authenticated CLI child process, or Bitwarden-assisted browser login."
---

# Bitwarden Agent Access

Read `.agents/docs/BITWARDEN_AGENT_ACCESS.md` before handling any credential.

Bitwarden is the canonical on-demand source for passwords, usernames, TOTPs,
API keys, and login credentials. Credential availability does not grant
approval for the task or account action that needs it.

## Command-Line Workflow

1. Confirm the requested task and its approval boundary.
2. Identify the minimum credential fields the child process needs.
3. Use `.agents/scripts/run-with-bitwarden` with exactly one placeholder-free
   runtime selector (`--domain` or `--id`), explicit `--env NAME=field`
   mappings, and a `--` child command.
4. Let the user complete any Bitwarden pairing, unlock, or approval prompt.
5. Keep the child process from printing, persisting, forwarding, or dumping
   the injected environment.

Never use `--env-all`. Never retrieve credentials with `aac connect --output
json`, direct vault export, or SDK code that returns credential values to the
Agent. Never put a credential in a prompt, transcript, command argument, log,
file, clipboard automation, issue, commit, or handoff.

Do not enable shell tracing or run environment-dump diagnostics around the
child. If the child cannot consume a narrowly named environment variable
safely, stop and report that integration blocker.

## Browser Workflow

Bitwarden's dedicated Agent Access browser extension is not yet available. For
a browser login, obtain task-specific approval first and navigate only to the
approved login page. Ask the user to use Bitwarden autofill or manually approve
and fill the login. Do not inspect, copy, transcribe, or store the credential.

Preserve all existing approval requirements for account-changing, posting,
payment, purchase, settings, administrative, deletion, invitation, permission,
and security-related operations.

## Runtime And State Boundary

Agent Access is an early preview and Bitwarden recommends test data rather than
production credentials. The reviewed CLI compatibility baseline is `v0.11.0`;
do not auto-download a mutable latest release.

Pairing and approval are user-performed. Keep pairing tokens, item identifiers,
connections, and cache machine-local and outside project files and prompts. Do
not run `aac connections clear` without an explicit user request.

If `aac` is unavailable, unpaired, locked, denied, or incompatible, report the
runtime blocker. Do not bypass the wrapper or expose a secret as a fallback.
