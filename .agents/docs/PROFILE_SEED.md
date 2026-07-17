# .NET Desktop App Profile

Use for C#/.NET desktop applications, utilities, and testable supporting
libraries.

Recommended `.agents` additions:

- `.agents/docs/PROJECT_PLAN.md`
- `.agents/docs/PROJECT_RULES.md`
- `.agents/docs/ARCHITECTURE.md`
- `.agents/workflows/HANDOFF_WORKFLOW.md`
- `.agents/skills/test-driven-development/SKILL.md`
- `.agents/skills/code-review/SKILL.md`

The local rules should define the required .NET SDK and target framework,
solution and test-project layout, UI framework and supported platforms, and
the standard `dotnet restore`, `dotnet build`, and `dotnet test` commands.

Keep portable domain and library code isolated from desktop UI and
platform-specific integrations. Projects that target Windows-specific
frameworks or workloads should document whether Linux or WSL can restore,
build, and test the full solution, and provide a Windows validation path when
cross-platform validation is incomplete.
