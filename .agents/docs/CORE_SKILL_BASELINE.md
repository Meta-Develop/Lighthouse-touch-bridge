# Core Skill Baseline

This baseline is the fallback operating policy for managed projects. Use a
project-local skill first when it exists and is more specific. Use this baseline
when the project has not yet defined a dedicated skill for the same work.

## Required Baseline

- Git workflow: create or switch to a task branch or isolated worktree before
  tracked edits, split independent scopes into separate branches or worktrees,
  commit each logical unit unless explicitly told not to or project policy
  forbids it, merge only after relevant validation is complete, and preserve
  history at completion. Completion into `main`, `master`, or another default
  branch must use `git merge --no-ff`; PR completion must use a merge-commit
  mode that preserves the branch's logical commits. Fast-forward merges,
  `--ff-only` merges, squash merges, rebases, and any other history-flattening
  completion path are prohibited. Agents may push scoped task branches without asking when the branch
  is non-protected, the remote/upstream is clear or can be set safely, checks
  passed or unrelated blockers are reported, and staged/committed paths were
  reviewed for secrets, scratch files, and private/local-only agent files. Once
  those push conditions are met, keep the task branch synchronized with its
  remote: push after each logical-unit commit or small coherent group of
  commits, and always before a handoff or session end; report a blocked push
  instead of silently deferring.
- Repository mutation boundary: the current Git repository is the hands-on edit
  boundary. Do not directly mutate a different Git-backed repository just
  because it is adjacent in the filesystem or relevant to a finding. For another
  repository, stop before mutation and preserve the finding as a
  maintainer-facing GitHub issue, local issue draft, or handoff with
  repo-relative evidence. Direct mutation is allowed only after the user
  explicitly changes scope or a delegated worker is launched in the target
  repository context, after loading that repo's local instructions, checking its
  Git state, and using its branch, validation, and handoff workflow. Registry
  mirrors under inventory-defined `projects/<organization>/<repo>/.agents`
  paths are registry data and must be changed through registry sync or feature
  adoption workflows, not as ad hoc live repository edits.
- Publication gates: task-branch pushes are not approval to merge, release,
  publish packages, or publicize sensitive owner or agent context. Ask before
  pushing to `main`/`master`, merging to a default branch, force-pushing,
  rewriting history, deleting remote branches, publishing releases or packages,
  or pushing private/local-only agent files when project policy forbids them.
- Human authorship and attribution: machine, model, vendor, runtime, agent, and
  constructed agent-role names such as domain-specific Clean Worker or coding,
  automation, and delegated workers must never be used as Git authors or
  committers, credit trailers such
  as `Co-authored-by`, `Signed-off-by`, or `Reviewed-by`, contributors,
  authors, credits, copyright owners, release implementation credits,
  generated-by stamps, or agent-implementation disclosures. Use the human or
  organization identity approved by the current repository; never assume one
  global name or email. A real person must not be rejected merely because their
  surname is `Worker`. Real human credits and ordinary technical references to
  models, vendors, runtimes, APIs, or compatibility remain valid. GitHub
  platform committer identities and Git's internal `git stash` identity
  are repository mechanics rather than AI self-attribution and remain valid.
  Unmistakable author placeholders such as `Your Name`, `あなたの名前`,
  `you@example.com`, and `your.name@example.invalid` are not valid human or
  organization attribution. A factual
  disclosure may be added only when the owner explicitly requests it, and it
  must not misstate authorship. Run
  `.agents/scripts/install-human-authorship-guard` in each checkout so the
  composing `commit-msg` and range-aware `pre-push` checks are active. Run
  `.agents/scripts/check-human-authorship metadata-tree` before publishing PR,
  release, contributor, author, credit, copyright, or package metadata. Use
  `.agents/scripts/check-human-authorship all-history` when an exhaustive audit
  of every commit reachable from local refs is required.
- Local agent-file privacy: `.agents/`, `AGENTS.md`, local custom-agent files,
  hooks, and prompt files are private unless the project explicitly says
  otherwise. Keep them ignored, locally excluded, or guarded from accidental
  public push.
- Organization-owned repository confidentiality: for private or
  organization-owned repositories, preserve confidentiality for source,
  exports, generated packages, internal docs, agent context, raw artifacts, and
  cross-repo findings. Respect organization-specific remotes, release
  channels, public/private split rules, and publication gates. Classify source,
  exports, docs, generated artifacts, and issue/PR text before tracking,
  pushing, uploading, or publishing them. Do not publish `.agents`, `AGENTS.md`,
  raw agent artifacts, AI/orchestration traces, private handoff notes, or
  organization-sensitive findings unless the project explicitly permits it.
  Sensitive cross-repo findings should default to local/private issue drafts or
  handoffs rather than public issues or edits in another repository.
- Owner-local path redaction: public-facing docs, code comments, issue/PR text,
  release notes, and other publishable tracked content must not expose
  owner-local absolute paths or machine-specific roots such as `/mnt/d/...`,
  `/home/<owner>/...`, `C:\Users\...`, drive roots such as
  `D:\...`, or WSL mount paths. Use repo-relative paths or redacted
  placeholders such as `<repo-root>`, `<local-project-root>`, `<owner-home>`,
  or `<host-setup-repo>` when a local location matters. Private agent-only
  context may mention local paths only when required for local operation and
  must not be copied into public docs, issues, PRs, releases, or packages.
- Change risk: classify the likely blast radius before editing shared behavior,
  public interfaces, migrations, build logic, deployment, hardware, or data.
- Interface contracts: review callers, consumers, schemas, protocols, config
  keys, public commands, and file formats before changing a contract.
- Code and content review: prefer concrete findings, regressions, user-visible
  risks, and missing tests over broad summaries.
- SPECA review escalation: for security-, protocol-, invariant-,
  trust-boundary-, or specification-compliance reviews, use SPECA methodology
  as an optional escalation path: derive expected properties from specs or
  requirements, map each property to implementation code, try to prove it
  holds, and treat proof gaps as candidate findings. Run a SPECA CLI or
  pipeline only when enough specification context exists and the runtime or cost
  is justified. Independently verify SPECA outputs before reporting or acting
  on them. Keep generated audit outputs, logs, and target-specific findings
  sensitive and out of tracked project context unless explicitly approved.
- Project hygiene: keep generated files, caches, raw logs, temporary artifacts,
  and credentials ignored, removed from the worktree, or deliberately curated.
- Language policy: follow the project `AGENTS.md` Language Policy.
- License selection: choose AGPL, GPL, LGPL/MPL, Apache/MIT, Creative Commons,
  or hardware-specific licenses from the project distribution model and owner
  goal instead of applying one license family to every repository. Use strong
  copyleft when community reciprocity and SaaS/cloud source return matter; use
  permissive or weak-copyleft licenses when adoption, interoperability, or
  library reuse matters more. Preserve third-party and vendored-code notices.
- Systematic debugging: reproduce, isolate, form a small hypothesis, test it,
  and record only durable evidence.
- Test-driven or verification-driven work: choose the smallest useful check for
  each change, and broaden verification when touching shared behavior.
- Completion verification: run or state the relevant verification, rerun git
  status after commands that can change tracked files, use the repository
  handoff guard when available, leave no dirty task state except documented WIP
  with reason, owner, and next command, identify unrelated changes, and report
  remaining risk. In `agent_files`, registry-local verification and handoff
  guard details are owned by `.agents/docs/PROJECT_RULES.md` Verification.

## Escalation

Project-specific rules may be stricter, but they must not permit
fast-forward, `--ff-only`, squash, rebase, or other history-flattening
completion paths. For other baseline conflicts, follow the project-specific
rule. If a baseline rule is
repeatedly needed with project-specific commands or constraints, promote a local
skill in that project rather than weakening this shared baseline.
