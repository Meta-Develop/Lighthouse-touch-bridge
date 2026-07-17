# Autonomous Issue Feedback

Use this guidance for owner-maintained development, tool, orchestration, registry, infrastructure, and developer-tooling projects.

When an agent uses or maintains owner-maintained tools or infrastructure and discovers an actionable bug, confusing workflow, missing documentation, rough edge, unsuitable default, integration friction, broken command, or useful feature idea, it must preserve that feedback instead of letting it disappear in the current task.

This applies to orchestration tooling, registry workflows, local developer tools, shared features, automation scripts, test/build infrastructure, documentation used by agents, and downstream usage that exposes a problem in an owner-maintained project.

The required outcome is one of:

- Fix the problem in the current task when the fix is small, fully in scope, verified, and needs no durable follow-up.
- File an autonomous pull request when the fix is safe, authorized, bounded, verified, and not already covered by another pull request.
- File a GitHub issue when filing is safe, authorized, non-duplicative, and useful to maintainers.
- Create a local issue draft when public filing is unsafe, unauthorized, duplicate status is uncertain, or the report needs owner review first.

## Decision Ladder

Use this order:

1. Fix silently in the current task when the fix is small, fully in scope, verified, and needs no durable follow-up.
2. File an autonomous pull request when the pull request conditions below all hold.
3. File a GitHub issue when the finding should survive beyond this session but a PR is not appropriate.
4. Create a local draft under `.agents/artifacts/issue-drafts/` when public filing is unsafe, unauthorized, uncertain, or needs owner review.

## Autonomous Pull Request

An agent may file a pull request without asking first only when all of these are true:

- The repository is maintained by the owner or the agent has explicit write and pull-request permission.
- The fix is small, bounded, and inside this repository's responsibility boundary.
- The fix is verified by the smallest relevant check, such as tests, lints, or documented manual verification in the PR body.
- Public PR text will not expose secrets, private paths, security-sensitive content, or AI/agent mentions in the title, body, or commits unless project-local rules require them.
- No open pull request already covers the same change.
- The change does not touch protected branches directly.

Mechanics:

1. Create a task branch from the default branch.
2. Commit scoped logical units.
3. Push only the task branch.
4. Create the pull request with `gh pr create --title ... --body-file ...`.

Never push to the default branch, never force-push, and never merge your own pull request. PR completion by maintainers must preserve merge-commit history per the shared git workflow policy. Squash, rebase, fast-forward, and other history-flattening completion modes are prohibited for agent-driven merges.

If the fix is partial or reveals follow-up work, file or reference an issue and link it from the PR body.

If push or PR creation fails, or authorization is uncertain, fall back to an issue plus a local patch draft at `.agents/artifacts/issue-drafts/<date>_<slug>.md`. Include the diff summary, not raw secrets, and mention the draft in the handoff.

## Pull Request Body Template

```markdown
## Summary

## Why

## Verification

## Linked issues
```

## File Without Asking

An agent should file a GitHub issue without asking first when all of these are true:

- The repository is maintained by the owner or the agent has clear permission to write issues.
- The problem belongs to this repository, not to a third-party dependency or local machine state.
- The problem is reproducible or supported by concrete evidence.
- The issue is actionable by a maintainer who was not present for the session.
- Filing the issue will not expose secrets, private data, customer data, sensitive local paths, hardware safety details, or security vulnerabilities.
- The issue is not already covered by an open issue.

If any condition is uncertain, create a local issue draft and mention it in the handoff instead of filing publicly.

## Do Not File

Do not file a public issue for:

- secrets, credentials, tokens, private keys, or real private paths
- private owner data, customer data, unpublished research, or sensitive repository paths
- suspected security vulnerabilities or abuse paths
- one-off local environment failures without evidence that the project is at fault
- vague preferences without a concrete failure mode or expected behavior
- problems already fixed in the current branch
- issues that are fully fixed as part of the current task and need no follow-up

For security-sensitive problems, write a private handoff note and ask the owner how they want to disclose or track it.

## Issue Quality

Write issues as normal maintainer issues. Do not mention that an AI or agent found the problem unless project-local rules explicitly require it.

Use repo-relative paths and summarized evidence. Avoid raw logs unless the log is short, redacted, and necessary. If raw command output matters, keep it in local storage and quote only the important lines in the issue.

A useful issue includes:

- Summary
- Expected behavior
- Actual behavior
- Reproduction steps
- Evidence, with commit, command, file path, or observed output
- Impact on downstream users or projects
- Suggested direction, if known

## GitHub Workflow

Before filing:

1. Check the repository remote and confirm it is the intended upstream.
2. Search open issues for the same failure or papercut.
3. Prefer one issue per root cause.
4. Use neutral labels only when they already exist, such as `bug`, `enhancement`, `documentation`, or `triage`.

If GitHub access is unavailable, create a Markdown draft under `.agents/artifacts/issue-drafts/` with a clear title, body, and the reason it was not filed.

## Local Drafts

Local drafts are required when public filing is not appropriate. Keep them concise, actionable, and private by default.

Use `.agents/artifacts/issue-drafts/<date>_<slug>.md` when the project allows local artifacts. Include the same issue-quality fields, plus:

- Filing status: `draft-only`
- Reason not filed
- Privacy or security concern, summarized without sensitive details
- Owner decision needed, if any

Do not include public secrets, private data, sensitive absolute paths, exploit details, or raw logs that would be unsafe to publish. Summarize and redact evidence instead.
