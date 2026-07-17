---
name: artifact-storage
description: "Use when deciding whether .agents artifacts, command outputs, logs, generated evidence, or handoff notes should be tracked, summarized, or kept local, or where to place reference-only repository clones."
---

# Artifact Storage

Before capturing `.agents/artifacts` into the registry, read `.agents/docs/ARTIFACT_STORAGE_POLICY.md`.

Prefer a concise Markdown summary over raw command output. Keep raw logs in `.agents/temp` or `.agents/storage` unless a project-local rule explicitly requires tracking them.

Clone reference-only external repositories (fetch/pull allowed, never commit or
push) under `.agents/storage/`, for example
`.agents/storage/reference_repos/<name>` — never into the project tree,
`.agents/temp`, or any tracked path. Editing such a repository is a scope
change that needs the target repository's own branch and handoff workflow.

For development work that may later support a paper, benchmark, technical report, or design note, add a compact private research note under the live project's `.agents/research/` when the setup, data, measurements, or negative result would be hard to reconstruct. Keep bulky raw data, logs, generated figures, and large outputs out of the registry unless explicitly curated and useful.

Do not publish, export, or copy research notes/data outside this private `agent_files` registry and local `.agents` context without explicit owner approval.
