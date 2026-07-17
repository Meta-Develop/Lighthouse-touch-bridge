---
name: web-access-browser-automation
description: "Use when browser automation, logged-in web operations, local browser history/bookmark lookup, or eze-is/web-access are relevant."
---

# Web Access Browser Automation

Before using `eze-is/web-access` or another real-browser automation path, read
`.agents/docs/WEB_ACCESS_BROWSER_AUTOMATION.md`.

`web-access` is optional and powerful. Do not treat it as an always-on research
skill. For ordinary public web research, prefer built-in web/search tools and
official sources where applicable.

Prefer a dedicated Agent browser setup: isolated browser or profile, non-daily
account, and a virtual display or separate monitor where possible. Do not use
the owner's daily browser profile or primary personal account by default.

Do not inspect local browser history, bookmarks, saved tabs, downloads, cookies,
passwords, or form data unless the user explicitly asks for that category of
inspection.

Get explicit user approval for the current task before performing logged-in,
account-changing, posting, payment, purchase, settings, administrative,
deletion, invitation, permission, or security-related actions.

If a local proxy or browser-control endpoint is used, keep it loopback-only.
Treat unauthenticated local control ports as machine-local capabilities and do
not expose them to a LAN, VPN peer, public tunnel, container bridge, or remote
host without explicit approval.

Keep mutable package state, browser profiles, caches, cookies, downloaded site
data, local configuration, site patterns, and generated credentials out of
tracked files and out of shared cross-project paths. Avoid mutable web-access
config in shared external checkouts.

Screenshots, traces, raw logs, HAR files, and local page captures stay local
unless the user asks to preserve or publish them. Prefer concise summaries in
handoffs.

`web-access` does not provide a stronger model or override runtime/project
policy. Model choice, delegation, and approved-agent rules still come from the
active environment; approved strong-model agents may use the tool for web and
browser work when allowed.
