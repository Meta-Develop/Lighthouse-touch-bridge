---
name: host-software-requests
description: "Use when a project agent needs software, downloads, packages, tools, or dependencies that may belong in the user's persistent NixOS host setup instead of the current project."
---

# Host Software Requests

Use this skill when work in a non-host project needs new software, downloaded
tools, packages, CLI or GUI applications, device tooling, or dependencies.

## Classify The Need

First decide whether the need is project-local or host-wide:

- Keep repo-local development dependencies in the current project when they are
  only needed to build, test, lint, simulate, package, or debug that project.
  Prefer the project's existing mechanisms, such as `flake.nix` dev shells,
  language package managers, repo-local scripts, containers, or documented
  bootstrap commands.
- Use one-off ephemeral tools from existing repo-local or temporary mechanisms
  when they do not need desktop integration, persistent PATH availability,
  system services, device rules, host application state, or reuse outside the
  current task.
- Treat persistent host integration as host-wide. Examples include NixOS or
  Home Manager packages, GUI applications, desktop entries, services, udev or
  device access rules, long-lived downloaded toolchains or application bundles,
  browser or desktop helper tools, and software expected to survive beyond the
  current project checkout.

## Host-Wide Changes

For persistent host-wide software, downloads, packages, app tooling, or
dependencies, do not directly mutate adjacent repositories from the project
agent. Preserve the current repository as the hands-on mutation boundary.

Launch or hand off to a delegated worker in the managed My_NixOS_Setup host
repository. Use a redacted placeholder such as `<local-project-root>/My_NixOS_Setup`
when a local checkout path is needed in user-facing or publishable text.

The host worker must load that repository's local instructions, check its Git
state, use its branch/worktree rules, and follow its validation and activation
workflow. Live activation, rebuild, Home Manager switch, service restart, or
desktop reload decisions belong to the host repo workflow, not to the requesting
project agent.

If no delegated-worker mechanism is available, write a clear handoff and stop
before mutating `My_NixOS_Setup` or any other adjacent Git-backed repository.

## Handoff Payload

Provide the host worker with:

- package, tool, application, or dependency name
- why it is needed and which project/task requested it
- source, download URL, upstream repository, or package attribute if known
- scope: temporary one-off, project-local development, or persistent host-wide
- runtime expectations: CLI, GUI, service, device access, desktop launcher,
  browser integration, file association, or PATH availability
- validation command, smoke test, or user-visible check
- urgency and whether the requesting task is blocked
- whether live activation is requested, optional, or explicitly not requested
- any relevant privacy, licensing, hardware, network, or local-agent-file
  constraints

Keep private `.agents` context, local scratch, credentials, downloaded binaries,
and generated evidence out of unrelated repositories unless that target
repository's rules explicitly allow tracking them.
