---
name: autonomous-issue-feedback
description: "Use when an agent discovers an actionable bug, broken workflow, missing documentation, rough edge, feature idea, integration friction, or repeated papercut in an owner-maintained development/tool project and must fix it, file an autonomous PR, file an issue, or create a local draft."
---

# Autonomous Issue Feedback Skill

Use this skill when an owner-maintained development, tool, orchestration, registry, infrastructure, or developer-tooling project exposes a problem or useful improvement while being used, integrated, tested, or maintained.

If the finding is actionable, the agent must preserve it by fixing it in the current task when that is fully in scope, filing an autonomous pull request when safe and authorized, filing a GitHub issue when a PR is not appropriate, or creating a local issue draft when public filing is unsafe, unauthorized, uncertain, or needs owner review.

Examples include bugs, broken commands, missing documentation, confusing workflows, unsuitable defaults, integration friction, rough edges in orchestration or registry tooling, and useful feature ideas discovered through real use.

## Decision

Use this decision ladder in order:

1. Fix silently in the current task when the fix is small, fully in scope, verified, and needs no durable follow-up.
2. File an autonomous pull request when the pull request conditions below all hold.
3. File a GitHub issue when the finding should survive beyond this session but a PR is not appropriate.
4. Create a local draft under `.agents/artifacts/issue-drafts/` when public filing is unsafe, unauthorized, uncertain, or needs owner review.

## Autonomous Pull Request

File a pull request autonomously only when all conditions pass:

- The repo is owner-maintained or the agent has explicit write and pull-request permission.
- The fix is small, bounded, and inside the repo's responsibility boundary.
- The fix is verified by the smallest relevant check: tests, lints, or documented manual verification in the PR body.
- Public PR text does not contain secrets, private paths, security-sensitive content, or AI/agent mentions in the title, body, or commits unless project rules require them.
- No open pull request already covers the same change.
- The change does not touch protected branches directly.

Mechanics:

1. Create a task branch from the default branch.
2. Make scoped commits per logical unit.
3. Push only the task branch.
4. Create the PR with `gh pr create --title ... --body-file ...`.
5. Never push to the default branch, never force-push, and never merge your own PR.

PR completion by maintainers must preserve merge-commit history under the shared git workflow policy. Squash, rebase, fast-forward, and other history-flattening completion modes are prohibited for agent-driven merges.

If the fix is partial or reveals follow-up work, file or reference an issue and link it from the PR body.

If push or PR creation fails, or authorization is uncertain, fall back to an issue plus a local patch draft at `.agents/artifacts/issue-drafts/<date>_<slug>.md`. Include the diff summary, not raw secrets, and mention the draft in the handoff.

## Pull Request Body Template

```markdown
## Summary

## Why

## Verification

## Linked issues
```

## Autonomous Issue

File an issue autonomously when all conditions pass:

- The repo is owner-maintained or the agent has explicit permission to create issues.
- The problem is in this repo's responsibility boundary.
- The finding is reproducible, evidenced, or strongly supported by observed behavior.
- The issue is useful after the current session ends.
- It is safe to make public under project-local privacy, security, and no-AI-footprint rules.
- No open issue already tracks the same root cause.

Draft locally instead of filing when permissions, ownership, privacy, security, or duplication are uncertain.

## Procedure

1. Confirm the upstream with `git remote -v` and, when available, `gh repo view`.
2. Search existing issues with `gh issue list --state open --search "<key terms>"`.
3. If the issue is already tracked, add a concise note to the handoff instead of opening a duplicate.
4. Write a maintainer-quality issue using repo-relative paths and summarized evidence.
5. File with `gh issue create --title "<title>" --body-file <file>` when authenticated and authorized.
6. If filing fails or is not safe, create `.agents/artifacts/issue-drafts/<date>_<slug>.md` and mention the draft in handoff.

## Issue Body Template

```markdown
## Summary

## Expected behavior

## Actual behavior

## Reproduction

1.
2.
3.

## Evidence

## Impact

## Notes
```

## Guardrails

- Do not file public issues containing secrets, credentials, private keys, private data, unpublished owner context, or sensitive local paths.
- Do not file public security vulnerabilities or abuse paths; create a private handoff note or local draft for the owner without exploit details.
- Do not blame third-party projects unless the evidence clearly points there.
- Do not mention AI or agents in public issue text unless project-local rules explicitly require it.
- Prefer fixing the problem directly when it is in scope and small; file an issue for follow-up work that should survive beyond the current task.
- Redact raw logs and use repo-relative paths unless an absolute path is essential and non-sensitive.
