# SessionWalker Developer Dashboard &amp; Excel To-Do Export

The dashboard makes Roslyn analysis results easy to understand, investigate,
prioritize and convert into work items. It is **visibility and diagnostics
only**: the Roslyn analysis engine and the Session detection rules are
unchanged.

```
Roslyn Analysis Engine                     (unchanged — SessionUsageAnalyzer)
      ↓
Analysis Result Models                     (AnalysisResult / ProjectAnalysisResult, enriched)
      ↓
Dashboard View Model                       (AnalysisDashboardModel — built once)
      ↓
HTML Dashboard   →   Excel (.xlsx) Export Service
```

The dashboard and Excel export **never re-run analysis**. `--dashboard` and
`--excel` are post-processing steps over the single `AnalysisResult` already
computed by the run.

## Usage

```
SessionWalker analyze <Solution.sln|Project.csproj> --dashboard dashboard.html --excel tasks.xlsx
SessionWalker analyze <Solution.sln> --excel session-tasks.xlsx
SessionWalker analyze <Solution.sln> --dashboard dashboard.html
```

Both flags work with the existing filters (`--project`, `--session`,
`--session-write`, `--controller-only`) and with `--json`/`--csv`.

* `--dashboard <path>` writes a **self-contained HTML file** (embedded data,
  no server, no external assets). Open it in any browser.
* `--excel <path>` writes a **.xlsx developer to-do workbook**.

## HTML Dashboard pages

| Page | What it shows |
| ---- | ------------- |
| **Overview** | Solution name/path/timestamp, totals (projects, documents, controllers, actions, Session actions, operations, diagnostics, errors, warnings) and an explicit **"what was NOT detected"** panel (rejected detections, projects without findings, projects not analyzed). |
| **Projects** | Health table: first columns of the spec — project, assembly, documents, controllers, actions, Session actions, Session operations, compilation errors, status, developer action. Clicking a row opens the project detail. |
| **Project Detail** | General info (name, path, assembly, documents) + per-controller/action findings (file, line, Session usage, confidence, detection method, ReadOnly eligibility) + rejected (not-detected) Session-like accesses for that project. |
| **Session Findings** | Every detected operation (project, controller, action, file, line, operation, key, read/write, detection method, confidence, expression) with live text/type/confidence filters. |
| **Investigations** | Cases grouped by kind: compilation/reference problems, rejected (not-detected) accesses, no Session usage detected, analyzer failures. Each case lists evidence, possible reasons, and a suggested action. |
| **Action Center** | Generated to-do items grouped by priority with client-side checkboxes (persisted in localStorage). |

## Excel workbook structure

Ordered tabs (header row frozen, auto-filter on, auto column widths,
conditional formatting on Priority/Status, dates formatted `yyyy-mm-dd hh:mm`):

### 1. `Executive Summary` — Item | Value | Status
Solution, path, timestamp, project/document/controller/action totals, Session
counts, diagnostics/errors/warnings, rejected detections, projects with
issues, not-analyzed projects.

### 2. `Project Analysis` — Priority | Project | Path | Documents | Controllers | Actions | Session Actions | Session Operations | Compilation Errors | Status | Developer Action
Priority = High for broken/needs-review projects, Medium for no-Session-usage
review, Low for clean. `Compilation Errors > 0` is highlighted red.

### 3. `Session Findings` — Project | Controller | Action | File | Line | Operation | Session Key | Type | Confidence | Status | Developer Notes
Status defaults to **Open**. Developer Notes carry the expression, mutation
kind, access path and any analyzer note.

### 4. `Diagnostics` — Priority | Project | Category | Message | File | Line | Impact | Suggested Fix | Status
Categories: `Reference Error`, `Duplicate Assembly`, `Compilation Error`,
`Compilation Warning`, `Load Error`, `Analyzer Error`, `Detection Gap`,
`Other`. Every row carries an impact statement and a concrete suggested fix.

### 5. `Developer To-Do List` — ID | Priority | Category | Project | Component | Problem | Evidence | Suggested Solution | Owner | Status | Created Date | Completed Date | Notes
The main actionable sheet. IDs are `TASK-001`… in priority order, status
`Open`, owner `Developer`, created date = analysis timestamp.

## To-do generation rules

| Priority | Category | Trigger | Suggested solution |
| -------- | -------- | ------- | ------------------ |
| High | Analyzer Bug | Session-like access resolved to a type not in the Session mapping | Add the type to `SessionSymbolDetector` (separately approved rule change) |
| High | Reference Issue | Missing / duplicate assembly reference | Restore or remove the reference |
| High | Compilation Issue | Project could not be compiled/analyzed | Fix load/compile failure, re-run |
| Medium | Compilation Issue | Compilation errors limit binding | Fix compiler errors, re-run |
| Medium | Reference Issue | Session-like access did not bind (error type) | Resolve the reference |
| Medium | Manual Review | MVC project with controllers but zero Session findings | Confirm it is a genuine negative |
| Medium | Analyzer Failure | Analyzer threw | Review/fix the analyzer |
| Low | Manual Review | Low/medium-confidence findings | Verify at runtime |
| Low | Compilation Cleanup | Warnings present | Clean the build |
| Low | Optimization | Actions eligible for `SessionStateBehavior.ReadOnly` | Review eligibility |

## Data model

* `Dashboard/Models/DashboardModels.cs` — `AnalysisDashboardModel`
  (`SolutionSummary`, `Projects`, `SessionFindings`, `Diagnostics`,
  `Investigations`, `TodoItems`).
* `Dashboard/DashboardModelBuilder.cs` — maps `AnalysisResult` → dashboard
  model, classifies diagnostics, builds investigation cases and to-dos.
* `Dashboard/Export/ExcelExportService.cs` — dashboard model → workbook.
* `Dashboard/Export/XlsxWorkbook.cs` — dependency-free OOXML writer (uses only
  `System.IO.Compression`/`System.Xml`; no new NuGet packages).
* `Dashboard/HtmlDashboardWriter.cs` — dashboard model → self-contained HTML.
* `Core/Models/RejectedCandidate.cs` — observation-record of a Session-shaped
  expression the detector did not accept (never fed back into detection).
* `AnalysisResult`/`ProjectAnalysisResult` gained additive metadata:
  document count, compilation error/warning counts, warning text, rejected
  candidates, compilation-succeeded flag. Existing constructors/callers are
  source-compatible.

## Verification

1. **Detection unchanged** — the only analyzer changes are (a) removal of
   leftover debug `Console.Error` prints and (b) observation-only recording
   of rejected candidates. `SessionSymbolDetector.MatchesByType` semantics are
   identical; `SessionUsageAnalyzer`'s match paths are untouched. Compare
   `--json`/`--csv` output before/after the change on your solution.
2. **Sample solution** — run against `samples/DemoSln/DemoSln.sln` (needs the
   .NET Framework reference assemblies, e.g. Windows/Visual Studio):
   * DemoWeb → detected Session operations (read/write/remove/abandon,
     interprocedural trace) + 2 rejected candidates (`_session` dictionary,
     `session` hashtable);
   * DemoPortal → "No Session Usage" status + investigation case;
   * DemoBroken → missing `EPPlus`/`Telerik.Mvc` references, "Needs Review",
     High-priority reference tasks.
3. **Dashboard representation** — open the generated HTML and check projects
   with findings, without findings, and with compilation issues appear
   distinctly.
4. **Excel** — open the generated `.xlsx` in Excel/LibreOffice; verify the
   five sheets, frozen headers, filters, priority/status highlighting, and
   that each row carries enough evidence to create a ticket without console
   logs.
5. **Existing reports** — JSON/CSV/console output are untouched (the new
   metadata sits in separate result fields; the JSON writer's DTO is
   unchanged).

## Performance

* Analysis is run **once**; dashboard and Excel are in-memory projections.
* Per-project overhead: one `compilation.GetDiagnostics` call (already existed
  for errors; warnings share the same enumeration), one document count, and
  rejection recording bounded to 5,000 entries/project. Rejected-candidate
  recording only fires for Session-shaped receivers (`Session`,
  `HttpContext.Session`, `_session*`).
* Excel writer is a streaming zip writer; memory is proportional to the
  dashboard model (rows in RAM), same as the existing JSON writer.

## Limitations

* The dashboard is a static HTML file; project detail shows the complete
  controller → action inventory from the run (actions with zero findings are
  visible with `Session Usage: none`).
* `Unresolved symbols` investigation is based on observation records added in
  this change; a Session-shaped expression whose receiver is named
  differently (e.g. a `HttpContext`-derived custom property) is not recorded.
* Excel conditional formatting marks `Open` rows amber by design (they are
  actionable); completed tasks turn green.
* `DocumentCount` counts documents loaded into the compilation (excluding the
  synthesized implicit-usings file), including files the analyzer skips as
  generated.
* The sample solution requires .NET Framework reference assemblies (Windows
  with Visual Studio or Build Tools).
