# Bitwarden Agent Access

Bitwarden is the canonical on-demand source when an Agent needs a password,
username, TOTP, API key, or other login credential. Do not copy credentials
into prompts, chat, plans, logs, files, command arguments, issues, commits, or
other durable context.

This policy controls credential handling only. It does not grant an Agent
standing access to any account and does not replace per-task approval. Follow
the active project and browser-automation approval boundaries before any
account-changing, posting, payment, purchase, settings, administrative,
deletion, invitation, permission, or security-related action.

## Preview Status And Compatibility

Bitwarden describes Agent Access as an early preview. Its APIs and protocol may
change, and Bitwarden currently recommends sample data rather than production
credentials for testing. Treat this limitation as material: do not silently
represent the preview as production-ready or bypass a user approval because a
credential is available.

The reviewed compatibility baseline for this feature is the official `aac`
CLI `v0.11.0`. This is a baseline, not a mutable-latest installation rule. Do
not automate a download from a `latest` URL. Installation or upgrade must use
an intentionally selected official release and normal software verification.

Official sources:

- https://github.com/bitwarden/agent-access
- https://github.com/bitwarden/agent-access/releases/tag/v0.11.0
- https://bitwarden.com/blog/introducing-agent-access-sdk/

## Required Runtime Path

Use `.agents/scripts/run-with-bitwarden` when a command-line task needs a
credential. The wrapper invokes only `aac run`, requires exactly one
`--domain` or `--id`, requires at least one explicit `--env NAME=field`, and
injects the selected fields only into the child process.

Example with placeholders only:

```bash
.agents/scripts/run-with-bitwarden \
  --domain service.example \
  --env SERVICE_USER=username \
  --env SERVICE_PASSWORD=password \
  -- command-that-consumes-the-environment
```

Available Agent Access credential fields at the reviewed baseline are
`username`, `password`, `totp`, `uri`, `notes`, `domain`, and `credential_id`.
Map only the fields the child actually needs. Prefer a narrowly scoped child
process that consumes the environment directly.

The following are prohibited:

- direct `aac connect --output json` credential retrieval
- SDK examples or custom code that print or return credentials to the Agent
- `--env-all`
- credentials in stdout, stderr, files, clipboard automation, shell history,
  process arguments, prompts, or chat
- shell tracing such as `set -x`, environment dumps, or diagnostics that expose
  the injected values
- child commands whose purpose is to print, persist, forward, or inspect the
  injected environment
- direct vault export or broad vault enumeration as a substitute for the
  least-privilege Agent Access request

Do not add a secret to the child command's arguments. Arguments can be visible
to other processes and commonly appear in logs. If a tool cannot consume an
explicitly named environment variable without echoing or persisting it, stop
and request a safer integration path.

## Pairing And Machine-Local State

Pairing and approval are user-performed. Pairing tokens, connection state,
cache, item identifiers, and provider state remain machine-local and outside
Git. Never ask the user to paste a pairing token or vault identifier into a
prompt, and never record them in project documentation or configuration.

Do not run destructive connection cleanup such as `aac connections clear`
unless the user explicitly requests that exact cleanup. A missing `aac`
binary, missing pairing, locked vault, denied request, or incompatible preview
version is a runtime blocker to report; it is not permission to fall back to
printing or storing a credential.

## Browser Login Fallback

Bitwarden states that a dedicated Agent Access browser extension is still
forthcoming. For a browser login, first obtain the approval required for the
current task, then navigate only to the approved login page. Have the user use
the existing Bitwarden browser extension autofill or manually approve and fill
the credential. The Agent must not inspect, copy, request, transcribe, or store
the credential.

After login, continue only within the approved task scope. Credential access
does not authorize unrelated browsing or any account-changing, administrative,
payment, deletion, or security operation.
