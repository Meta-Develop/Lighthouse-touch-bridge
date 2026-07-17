# Web Access Browser Automation

`eze-is/web-access` is an optional external browser automation package. It is
powerful enough to inspect and act through a real browser context, so treat it
as an explicit task tool rather than background infrastructure.

## When To Use It

Use browser automation only when the task genuinely needs a real browser or a
local browser context, such as:

- testing an interactive local web application
- working with a logged-in web workflow after explicit user approval
- inspecting local browser history or bookmarks after the user explicitly asks
- reproducing behavior that depends on browser state, extensions, or cookies
- using the optional `eze-is/web-access` package in a project that has attached it

For ordinary public web research, current facts, documentation lookup, source
quotes, or broad search, prefer the built-in web/search tools and official
sources where applicable.

## Browser And Profile Boundaries

Prefer a dedicated Agent browser setup: isolated browser or profile,
non-daily account, and a virtual display or separate monitor whenever the
environment supports it. Do not use the owner's daily browser profile or
primary personal account by default.

If a task requires the owner's existing browser session, cookies, extensions, or
history, get explicit approval for that task before opening or inspecting it.

Do not inspect browser history, bookmarks, saved tabs, downloads, form data,
cookies, passwords, or other local browser data unless the user explicitly asks
for that category of inspection.

## Approval Boundaries

Explicit user approval for the current task is required before using browser
automation to perform logged-in, account-changing, posting, payment, purchase,
settings, administrative, deletion, invitation, permission, or security-related
actions.

Read-only navigation inside an already approved workflow should still avoid
unrelated accounts, unrelated sites, and unrelated private data.

## Local Proxy And Network Safety

If `web-access` or related tooling exposes a local proxy or control port, keep
it bound to loopback only. Treat unauthenticated local control endpoints as
machine-local capabilities, not as services that may be exposed to a LAN,
container bridge, VPN peer, public tunnel, or remote host.

Do not add firewall, tunnel, reverse proxy, or bind-address changes that make a
local browser-control endpoint reachable outside loopback unless the user
explicitly asks for that exact setup and the risks are reviewed.

## State And Evidence Hygiene

Keep mutable package state, browser profiles, caches, cookies, downloaded site
data, local configuration, site patterns, and generated credentials out of
tracked files. Do not store mutable web-access config in a shared external
checkout where it can leak across projects.

Do not copy browser automation state between projects unless the user explicitly
requests it. Avoid cross-project leakage through shared config paths, shared
profiles, shared site pattern files, or tracked package state.

Screenshots, traces, raw logs, HAR files, local page captures, and other
browser evidence stay local unless the user asks to preserve or publish them.
Prefer concise summaries in handoff text over committing raw evidence.

## Model And Delegation Boundary

`web-access` does not provide a stronger model or override project runtime
policy. Model choice, delegation, and approved-agent rules still come from the
active runtime and project instructions. Approved strong-model agents may use
the tool for web and browser work when the task and policy allow it.
