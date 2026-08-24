# ElliePdf Coding Guidelines

## Agentic workflows

Five GitHub Actions workflows automate developer tasks using `actions/ai-inference@v1`.
They work with **any model supported by GitHub Models**: GPT latest, Claude Sonnet, o-mini, Codex, etc.

### Choosing a model

Set the **`AGENT_MODEL` repository variable** (Settings → Secrets and variables → Variables) to control which model all agent workflows use by default. Use versionless aliases so you always get the current release without updating workflows:

| Value | Model |
|---|---|
| `gpt-latest` | GPT latest (default) |
| `claude-sonnet` | Claude Sonnet (currently Sonnet 5) |
| `o-mini-latest` | OpenAI o-mini latest |
| `gpt-mini-latest` | GPT mini latest (faster/cheaper) |

Each workflow also accepts a `model` input when triggered via `workflow_dispatch`, overriding `AGENT_MODEL` for that single run.

### Workflows at a glance

| Workflow | Trigger | What the AI does |
|---|---|---|
| `agent-test-generation` | PR touches Services/ViewModels/Core | Generates xUnit tests, commits to PR branch |
| `agent-feature-implementation` | Issue labeled `feature` | Scaffolds code + draft PR |
| `agent-code-review` | PR (non-draft) touches .cs/.xaml | Reviews diff, posts comment |
| `agent-release` | Tag `v*.*.*` push | Generates changelog, creates GitHub Release |
| `agent-issue-triage` | Issue opened/reopened | Labels issue, posts follow-up comment |


```
ElliePdf (WinUI 3, net11.0-windows10.0.26100.0)
├── ElliePdf.Core    Pure .NET logic — no WinUI, no P/Invoke (net11.0)
├── Services/        IPdfService, IDocumentTabService, etc. + implementations
├── ViewModels/      MVVM ViewModels (CommunityToolkit.Mvvm)
├── Pages/           ReaderPage, SettingsPage (code-behind only, no logic)
├── Controls/        Reusable XAML controls
└── ElliePdf.Tests   xUnit tests (net11.0) — references ElliePdf.Core only
```

## General rules

- **One responsibility per class.** Services handle I/O; ViewModels handle UI state; Pages bind and forward.
- **Always nullable-aware.** Enable `<Nullable>enable</Nullable>`. Never suppress with `!` without a comment.
- **No logic in code-behind.** `*.xaml.cs` files call ViewModel commands and nothing else.

## Async

- Every public method that calls PDFium must be `async Task` and accept `CancellationToken cancellationToken = default`.
- Do NOT use `.Result` or `.Wait()` — always `await`.
- Background render work runs via `Task.Run`; UI updates are dispatched back via `DispatcherQueue`.

## PDFium / Services

- All PDFium P/Invoke is contained in `Services/PdfiumNative.cs`.
- `PdfService` serialises PDFium calls through a `SemaphoreSlim(1,1)` (or equivalent) to avoid concurrent access.
- Every `PdfDocumentSession` obtained from `OpenDocumentAsync` **must** be released with `CloseDocumentAsync`
  — ideally in a `finally` block.

## MVVM (CommunityToolkit.Mvvm)

```csharp
// CORRECT — source-generated observable property
[ObservableProperty]
public partial int CurrentPage { get; set; }

// CORRECT — source-generated relay command
[RelayCommand]
private async Task NavigateNextAsync(CancellationToken ct) { ... }

// WRONG — do not write backing fields manually
private int _currentPage;
public int CurrentPage { get => _currentPage; set => SetProperty(ref _currentPage, value); }
```

## Testing

- Tests live in `ElliePdf.Tests/` (framework: `net11.0`, no WinUI).
- Only test classes in `ElliePdf.Core` or pure-logic methods in `Services/` and `ViewModels/`.
- Use **NSubstitute** for interface mocking (`Substitute.For<IPdfService>()`).
- Test method naming: `MethodName_StateUnderTest_ExpectedBehavior`.

## Commit messages

Follow Conventional Commits:
- `feat:` new user-visible feature
- `fix:` bug fix
- `refactor:` code change without behaviour change
- `test:` adding or updating tests
- `chore:` build, CI, dependencies

## Labels

| Label | Meaning |
|---|---|
| `feature` | Triggers the feature-implementation agent workflow |
| `bug` | Regression or crash |
| `enhancement` | Improvement to existing behaviour |
| `priority: high` | Crash / data loss |
