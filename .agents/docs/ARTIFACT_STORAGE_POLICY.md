# Artifact Storage Policy

Use `.agents/artifacts` for durable, human-readable evidence that helps a future agent understand what happened. Do not use it as a raw command log archive.

Track:

- task summaries
- review notes
- handoff prompts
- redacted run notes
- small Markdown reports with exact commands and key outcomes
- compact `.agents/research/` notes and small curated evidence that materially help future reproducibility

Keep local or transient:

- raw `stdout`, `stderr`, and exit-code files
- repeated command probes
- generated logs, captures, binaries, build products, generated figures, bulky data, measurement dumps, and caches
- bulky or uncurated research data and exploratory dumps
- scripts created only to run one local experiment

Put transient evidence in `.agents/temp` or `.agents/storage`. If raw evidence is important, summarize it in Markdown and keep the raw file local.

Project-local safety rules may require retaining more evidence for a task, but the registry should keep summaries rather than bulk output.

## Reference Repository Clones

Clone external repositories used only as read-only reference material under
`.agents/storage/`, for example `.agents/storage/reference_repos/<name>`.
`.agents/storage/` is git-ignored, so reference clones stay out of tracked
history automatically.

- Updating a reference clone with `git fetch` or `git pull` is fine.
- Never commit, create branches, or push inside a reference clone.
- Never clone reference material into the project tree, `.agents/temp`, or any
  tracked path, and never track a reference clone's contents in the host
  project.
- If a reference repository needs actual changes, that is a scope change:
  treat it as a separate target repository with its own branch, validation,
  and handoff workflow instead of editing the reference clone in place.

## Research Notes

For development work that could later support a paper, benchmark, technical report, or design note, preserve a short private research note under the live project's `.agents/research/` when the result would be hard to reconstruct from code alone. Agents should write in the project they are already working in; this registry can mirror those notes later through capture/save.

Good research notes capture only the useful minimum:

- research question or engineering hypothesis
- date, project, branch or commit, and relevant environment
- commands, scripts, parameters, hardware or dataset identity
- key measurements, observations, and negative results
- interpretation, limitations, and next experiment

Keep `.agents/research/` selective. It may contain Markdown notes, small curated tables, data provenance, raw data pointers, and regeneration instructions as needed. Avoid mirroring bulky raw logs, generated dumps, binaries, or large figures unless the owner explicitly asks for a curated research artifact.

Research notes and data are private owner context. Do not publish, export, copy into public docs/issues/PRs/releases/paper drafts, or sync outside this private `agent_files` registry and local `.agents` context unless the owner explicitly approves it.
