using SessionWalker.Dashboard.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionWalker.Dashboard;

/// <summary>
/// Writes a self-contained HTML dashboard from <see cref="AnalysisDashboardModel"/>.
///
/// The output is a single static file: data is embedded as JSON, styling and
/// interactions are vanilla JS/CSS, and there are no external assets or
/// server-side dependencies — open it in any browser. Pages:
///   - Overview (solution summary + what was not detected)
///   - Projects (health table)
///   - Project Detail (per-controller/action findings)
///   - Session Findings (all detected operations, filterable)
///   - Investigations (why something may be missing / failed)
///   - Action Center (generated to-do list)
///
/// The writer consumes the dashboard view model only; it never re-runs
/// analysis and never touches Roslyn types.
/// </summary>
public sealed class HtmlDashboardWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string DataToken = "__SESSIONWALKER_DASHBOARD_DATA__";

    public Task WriteAsync(AnalysisDashboardModel model, string destinationPath, CancellationToken cancellationToken)
    {
        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("An output path is required.", nameof(destinationPath));
        }

        var json = JsonSerializer.Serialize(model, JsonOptions)
            .Replace("</", "<\\/");

        var html = BuildDocument(json);
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Task.CompletedTask;
    }

    private static string BuildDocument(string json)
    {
        var template = new StringBuilder(HtmlTemplate.Length + json.Length + 4096)
            .Append(HtmlTemplate)
            .Replace(DataToken, json);

        return template.ToString();
    }

    private const string HtmlTemplate = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SessionWalker Analysis Dashboard</title>
<style>
:root {
  --accent: #1f4e78; --accent-2: #2f6ba8; --bg: #f4f6f9; --card: #ffffff;
  --text: #1f2937; --muted: #6b7280; --border: #dbe1e8;
  --high-bg:#ffc7ce; --high-fg:#9c0006; --med-bg:#ffeb9c; --med-fg:#9c6500;
  --low-bg:#c6efce; --low-fg:#006100;
}
* { box-sizing: border-box; }
body { margin: 0; font-family: "Segoe UI", Calibri, Arial, sans-serif; background: var(--bg); color: var(--text); font-size: 14px; }
header { background: var(--accent); color: #fff; padding: 14px 20px; position: sticky; top: 0; z-index: 20; box-shadow: 0 2px 6px rgba(0,0,0,.15); }
header h1 { margin: 0 0 4px; font-size: 19px; font-weight: 600; }
header .sub { font-size: 12.5px; opacity: .85; overflow-wrap: anywhere; }
nav { display: flex; flex-wrap: wrap; gap: 6px; padding: 10px 20px 4px; background: var(--bg); position: sticky; top: 0; z-index: 10; }
nav button { border: 1px solid var(--border); background: var(--card); color: var(--text); padding: 7px 14px; border-radius: 6px; cursor: pointer; font-size: 13px; }
nav button.active { background: var(--accent); color: #fff; border-color: var(--accent); }
main { padding: 14px 20px 60px; max-width: 1600px; margin: 0 auto; }
.page { display: none; }
.page.active { display: block; }
h2 { font-size: 17px; margin: 8px 0 12px; color: var(--accent); }
h3 { font-size: 14.5px; margin: 18px 0 8px; }
.grid { display: grid; gap: 12px; }
.cards { grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); }
.card { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; }
.card .label { font-size: 12px; color: var(--muted); text-transform: uppercase; letter-spacing: .03em; }
.card .value { font-size: 24px; font-weight: 700; margin-top: 4px; }
.card .hint { font-size: 11px; color: var(--muted); margin-top: 3px; }
.card.warn { border-left: 4px solid #e6a817; }
.card.bad { border-left: 4px solid #c0392b; }
.card.ok { border-left: 4px solid #1e8e3e; }
.panel { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 14px 16px; margin-bottom: 14px; }
.kv { display: grid; grid-template-columns: 240px 1fr; gap: 4px 14px; }
.kv dt { color: var(--muted); font-size: 12.5px; padding-top: 1px; }
.kv dd { margin: 0; font-weight: 500; overflow-wrap: anywhere; }
table { border-collapse: collapse; width: 100%; background: var(--card); font-size: 12.5px; }
th { background: var(--accent); color: #fff; text-align: left; padding: 7px 8px; position: sticky; top: 52px; white-space: nowrap; }
td { border-bottom: 1px solid var(--border); padding: 6px 8px; vertical-align: top; }
tr.clickable { cursor: pointer; }
tr.clickable:hover td { background: #eef4fa; }
tr.expanded td { background: #eef4fa; }
.detail-row td { background: #f8fafc; }
.scroll { overflow-x: auto; border: 1px solid var(--border); border-radius: 8px; }
.badge { display: inline-block; padding: 2px 8px; border-radius: 10px; font-size: 11px; font-weight: 600; white-space: nowrap; }
.badge.high { background: var(--high-bg); color: var(--high-fg); }
.badge.medium { background: var(--med-bg); color: var(--med-fg); }
.badge.low { background: var(--low-bg); color: var(--low-fg); }
.badge.info { background: #dbeafe; color: #1e40af; }
.badge.muted { background: #e5e7eb; color: #374151; }
.badge.clean { background: var(--low-bg); color: var(--low-fg); }
.badge.review { background: var(--med-bg); color: var(--med-fg); }
.filters { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 10px; }
.filters input, .filters select { padding: 6px 9px; border: 1px solid var(--border); border-radius: 6px; font-size: 13px; }
.filters input { min-width: 260px; }
.case { border-left: 4px solid var(--accent-2); }
.case.high { border-left-color: #c0392b; }
.case.medium { border-left-color: #e6a817; }
.case.low { border-left-color: #1e8e3e; }
.case .title { font-weight: 600; margin: 4px 0 2px; }
.case .meta { font-size: 12px; color: var(--muted); margin-bottom: 8px; }
.case ul { margin: 6px 0 10px 20px; padding: 0; }
.case li { margin: 2px 0; overflow-wrap: anywhere; }
.reasons { background: #f8fafc; border: 1px dashed var(--border); border-radius: 6px; padding: 8px 10px; margin: 8px 0; }
.reasons .rt { font-size: 11.5px; color: var(--muted); text-transform: uppercase; letter-spacing: .03em; margin-bottom: 4px; }
.todo { display: flex; gap: 10px; padding: 10px 12px; border: 1px solid var(--border); border-radius: 8px; margin-bottom: 8px; background: var(--card); align-items: flex-start; }
.todo input { margin-top: 3px; }
.todo .body { flex: 1; }
.todo .problem { font-weight: 600; }
.todo .evidence { font-size: 12px; color: var(--muted); margin: 3px 0; overflow-wrap: anywhere; }
.todo .solution { font-size: 12.5px; margin-top: 4px; }
.todo .meta { font-size: 11.5px; color: var(--muted); margin-top: 5px; }
.todo.done { opacity: .55; }
.todo.done .problem { text-decoration: line-through; }
.muted { color: var(--muted); }
.pill { display: inline-block; background: #e5e7eb; color: #374151; border-radius: 10px; padding: 2px 9px; font-size: 11px; margin: 0 4px 4px 0; }
code { background: #eef2f7; border-radius: 4px; padding: 1px 5px; font-size: 12px; }
footer { color: var(--muted); font-size: 12px; padding: 18px 20px; }
.empty { padding: 26px; text-align: center; color: var(--muted); }
.counter { font-size: 12px; color: var(--muted); margin: 6px 0; }
</style>
</head>
<body>
<header>
  <h1>SessionWalker &mdash; Roslyn Analysis Dashboard</h1>
  <div class="sub" id="header-sub"></div>
</header>
<nav>
  <button data-tab="overview" class="active" onclick="showTab('overview')">Overview</button>
  <button data-tab="projects" onclick="showTab('projects')">Projects</button>
  <button data-tab="detail" onclick="showTab('detail')">Project Detail</button>
  <button data-tab="findings" onclick="showTab('findings')">Session Findings</button>
  <button data-tab="investigations" onclick="showTab('investigations')">Investigations</button>
  <button data-tab="actions" onclick="showTab('actions')">Action Center</button>
</nav>
<main>
  <section id="page-overview" class="page active"></section>
  <section id="page-projects" class="page"></section>
  <section id="page-detail" class="page"></section>
  <section id="page-findings" class="page"></section>
  <section id="page-investigations" class="page"></section>
  <section id="page-actions" class="page"></section>
</main>
<footer>
  Generated by SessionWalker. Data is embedded in this file; no analysis is re-run when the dashboard is opened.
  Use <code>--excel &lt;path&gt;</code> for the Excel to-do workbook.
</footer>
<script id="dashboard-data" type="application/json">__SESSIONWALKER_DASHBOARD_DATA__</script>
<script>
"use strict";
const DATA = JSON.parse(document.getElementById("dashboard-data").textContent);
const S = DATA.solution;

function esc(v) {
  if (v === null || v === undefined) return "";
  return String(v)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}
function fmtDate(iso) {
  if (!iso) return "-";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return esc(iso);
  return esc(d.toLocaleString());
}
function badge(text, cls) {
  return '<span class="badge ' + esc(cls || "muted") + '">' + esc(text) + "</span>";
}
function statusBadge(status) {
  const map = {
    "Clean": "clean", "Needs Review": "review", "No Session Usage": "info", "Not Analyzed": "high"
  };
  return badge(status, map[status] || "muted");
}
function priorityBadge(priority) {
  return badge(priority, String(priority).toLowerCase());
}

function showTab(name) {
  document.querySelectorAll(".page").forEach(p => p.classList.remove("active"));
  document.getElementById("page-" + name).classList.add("active");
  document.querySelectorAll("nav button").forEach(b => b.classList.toggle("active", b.dataset.tab === name));
  if (name === "projects") renderProjects();
  if (name === "detail") renderProjectDetail();
  if (name === "findings") renderFindings();
  if (name === "investigations") renderInvestigations();
  if (name === "actions") renderActions();
  window.scrollTo({ top: 0 });
}

/* ---------------- Overview ---------------- */
function renderOverview() {
  const el = document.getElementById("page-overview");
  const card = (label, value, cls, hint) =>
    '<div class="card ' + (cls || "") + '"><div class="label">' + esc(label) + '</div>' +
    '<div class="value">' + esc(value) + '</div>' +
    (hint ? '<div class="hint">' + esc(hint) + "</div>" : "") + "</div>";

  const notAnalyzed = S.projectsNotAnalyzed > 0;
  const withIssues = S.projectsWithIssues > 0;
  const noSession = S.projectsWithoutSessionUsage > 0;
  const rejected = S.totalRejectedDetections > 0;

  let html = `
  <h2>Solution Information</h2>
  <div class="panel">
    <dl class="kv">
      <dt>Solution</dt><dd>${esc(S.solutionName)}</dd>
      <dt>Solution path</dt><dd>${esc(S.solutionPath)}</dd>
      <dt>Analysis timestamp</dt><dd>${fmtDate(S.analyzedAtUtc)}</dd>
    </dl>
  </div>
  <h2>Analysis Summary</h2>
  <div class="grid cards">
    ${card("Projects", S.totalProjects, "", "analyzed")}
    ${card("Documents", S.totalDocuments, "", "analyzed")}
    ${card("Controllers", S.totalControllers, "", "discovered")}
    ${card("Actions analyzed", S.totalActions, "", "MVC actions")}
    ${card("Actions using Session", S.totalActionsWithSession, S.totalActionsWithSession > 0 ? "ok" : "")}
    ${card("Session operations", S.totalSessionOperations, S.totalSessionOperations > 0 ? "ok" : "")}
    ${card("Diagnostics", S.totalDiagnostics, withIssues ? "warn" : "ok")}
    ${card("Compilation errors", S.totalCompilationErrors, S.totalCompilationErrors > 0 ? "bad" : "ok")}
    ${card("Warnings", S.totalWarnings, S.totalWarnings > 0 ? "warn" : "ok")}
    ${card("Projects with issues", S.projectsWithIssues, withIssues ? "bad" : "ok")}
    ${card("Projects not analyzed", S.projectsNotAnalyzed, notAnalyzed ? "bad" : "ok")}
    ${card("Projects w/o Session usage", S.projectsWithoutSessionUsage, noSession ? "warn" : "ok")}
    ${card("Rejected detections", S.totalRejectedDetections, rejected ? "bad" : "ok", "Session-like accesses not detected")}
  </div>
  <h2>What was NOT detected — investigate</h2>
  <div class="panel">
    ${rejected ? '<div><strong>Rejected Session-like accesses:</strong> ' + esc(S.totalRejectedDetections) + ' found. Open <em>Investigations</em> to see why they were not detected.</div>' : '<div class="muted">No Session-like access was rejected — every candidate either matched the known Session type mapping or was not Session-shaped.</div>'}
    ${S.projectsWithoutSessionUsage > 0 ? '<div style="margin-top:8px"><strong>Projects with controllers but zero Session findings:</strong> ' + esc(S.projectsWithoutSessionUsage) + '. Confirm these are genuine negatives.</div>' : ""}
    ${S.projectsNotAnalyzed > 0 ? '<div style="margin-top:8px"><strong>Projects not analyzed:</strong> ' + esc(S.projectsNotAnalyzed) + '. Their Session usage is unknown.</div>' : ""}
  </div>`;
  el.innerHTML = html;
}

/* ---------------- Projects ---------------- */
function renderProjects() {
  const rows = DATA.projects.map(p => `
    <tr class="clickable" data-project="${escAttr(p.projectName)}" onclick="selectProject(this.dataset.project)">
      <td>${priorityBadge(p.priority)}</td>
      <td><strong>${esc(p.projectName)}</strong></td>
      <td>${esc(p.assemblyName)}</td>
      <td>${esc(p.documents)}</td>
      <td>${esc(p.controllers)}</td>
      <td>${esc(p.actions)}</td>
      <td>${esc(p.sessionActions)}</td>
      <td>${esc(p.sessionOperations)}</td>
      <td>${esc(p.compilationErrors)}</td>
      <td>${statusBadge(p.status)}</td>
      <td>${esc(p.developerAction)}</td>
    </tr>`).join("");

  document.getElementById("page-projects").innerHTML = `
    <h2>Project Analysis</h2>
    <div class="counter">${DATA.projects.length} project(s) — click a row to open Project Detail.</div>
    <div class="scroll">
      <table>
        <thead><tr>
          <th>Priority</th><th>Project</th><th>Assembly</th><th>Documents</th><th>Controllers</th>
          <th>Actions</th><th>Session Actions</th><th>Session Ops</th><th>Compilation Errors</th>
          <th>Status</th><th>Developer Action</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="11" class="empty">No projects in this analysis.</td></tr>'}</tbody>
      </table>
    </div>`;
}

function escAttr(v) { return esc(v).replace(/"/g, "&quot;"); }

function selectProject(name) {
  currentProject = name;
  showTab("detail");
}

/* ---------------- Project detail ---------------- */
let currentProject = null;

function maxConfidence(ops) {
  let best = null;
  (ops || []).forEach(o => {
    if (o.confidence === "High") best = "High";
    else if (o.confidence === "Medium" && best !== "High") best = "Medium";
    else if (o.confidence === "Low" && !best) best = "Low";
  });
  return best;
}

function toggleAction(row) {
  const detail = row.nextElementSibling;
  if (detail && detail.classList.contains("detail-row")) {
    const visible = detail.style.display !== "none";
    detail.style.display = visible ? "none" : "";
    row.classList.toggle("expanded", !visible);
  }
}

function actionOperationsHtml(action) {
  const ops = action.operations || [];
  if (ops.length === 0) return "";
  return `
    <div class="scroll" style="margin:6px 0">
      <table>
        <thead><tr><th>Expression</th><th>File</th><th>Line</th><th>Operation</th>
        <th>Session Key</th><th>Read/Write</th><th>Detection Method</th><th>Confidence</th></tr></thead>
        <tbody>${ops.map(o => `
          <tr>
            <td>${esc(o.expression)}</td><td>${esc(o.file)}</td><td>${esc(o.line)}</td>
            <td>${esc(o.operation)}</td><td>${esc(o.sessionKey)}</td><td>${esc(o.sessionType)}</td>
            <td>${esc(o.detectionMethod)}</td><td>${badge(o.confidence, o.confidence.toLowerCase())}</td>
          </tr>`).join("")}</tbody>
      </table>
    </div>`;
}

function renderProjectDetail() {
  const el = document.getElementById("page-detail");
  if (!currentProject && DATA.projects.length > 0) currentProject = DATA.projects[0].projectName;
  const p = DATA.projects.find(x => x.projectName === currentProject);

  if (!p) {
    el.innerHTML = '<h2>Project Detail</h2><div class="empty">No projects available.</div>';
    return;
  }

  const options = DATA.projects
    .map(x => '<option value="' + escAttr(x.projectName) + '"' + (x.projectName === p.projectName ? " selected" : "") + ">" + esc(x.projectName) + "</option>")
    .join("");

  let totalOps = 0;
  let totalActions = 0;
  const controllers = p.controllerDetails || [];
  controllers.forEach(c => totalActions += (c.actions || []).length);

  const controllerHtml = controllers.map(c => {
    const rows = (c.actions || []).map(a => {
      totalOps += a.sessionOperations || 0;
      const conf = maxConfidence(a.operations);
      const usage = a.sessionOperations > 0
        ? esc(a.sessionOperations + " op(s) (" + a.reads + "R/" + a.writes + "W)")
        : badge("none", "muted");
      const readonly = a.sessionOperations > 0
        ? (a.canBeReadOnly ? badge("YES", "low") : badge("NO", "high"))
        : badge("—", "muted");
      const opsHtml = actionOperationsHtml(a);
      return `
        <tr class="clickable${opsHtml ? "" : " no-detail"}" onclick="toggleAction(this)">
          <td>${esc(c.controller)}</td>
          <td>${esc(a.action)}()</td>
          <td>${esc(a.file)}</td>
          <td>${esc(a.line)}</td>
          <td>${usage}</td>
          <td>${conf ? badge(conf, conf.toLowerCase()) : badge("—", "muted")}</td>
          <td>${readonly}</td>
        </tr>` + (opsHtml ? `<tr class="detail-row" style="display:none"><td colspan="7">${opsHtml}</td></tr>` : "");
    }).join("");

    return `
      <h3>${esc(c.controller)} (${(c.actions || []).length} action(s))</h3>
      <div class="scroll">
        <table>
          <thead><tr>
            <th>Controller</th><th>Action</th><th>File</th><th>Line</th>
            <th>Session Usage</th><th>Confidence</th><th>ReadOnly eligible</th>
          </tr></thead>
          <tbody>${rows || '<tr><td colspan="7" class="empty">No actions discovered in this controller.</td></tr>'}</tbody>
        </table>
      </div>`;
  }).join("");

  const rejected = DATA.diagnostics.filter(d => d.project === p.projectName && d.category === "Detection Gap");

  el.innerHTML = `
    <h2>Project Detail</h2>
    <div class="filters">
      <select onchange="currentProject=this.value; renderProjectDetail()">${options}</select>
    </div>
    <div class="panel">
      <h3 style="margin-top:0">General Information</h3>
      <dl class="kv">
        <dt>Project name</dt><dd>${esc(p.projectName)}</dd>
        <dt>Assembly name</dt><dd>${esc(p.assemblyName)}</dd>
        <dt>Project path</dt><dd>${esc(p.projectPath)}</dd>
        <dt>Documents</dt><dd>${esc(p.documents)}</dd>
        <dt>Status</dt><dd>${statusBadge(p.status)} ${priorityBadge(p.priority)}</dd>
        <dt>Developer action</dt><dd>${esc(p.developerAction)}</dd>
      </dl>
    </div>
    <h2>Analysis Result</h2>
    <div class="counter">${controllers.length} controller(s), ${totalActions} action(s), ${totalOps} detected Session operation(s). Click an action row to inspect its operations.</div>
    ${controllerHtml || '<div class="empty">No controllers discovered for this project — open <em>Investigations</em> for possible reasons.</div>'}
    ${rejected.length ? `
      <h2>Rejected (not detected) Session-like accesses</h2>
      <div class="scroll"><table>
        <thead><tr><th>File</th><th>Line</th><th>Message</th><th>Impact</th><th>Suggested Fix</th></tr></thead>
        <tbody>${rejected.map(d => `
          <tr>
            <td>${esc(d.file)}</td><td>${esc(d.line)}</td>
            <td>${esc(d.message)}</td><td>${esc(d.impact)}</td><td>${esc(d.suggestedFix)}</td>
          </tr>`).join("")}
        </tbody></table></div>` : ""}`;
}

/* ---------------- Session findings ---------------- */
let findingFilter = { text: "", type: "", confidence: "" };

function findingMatches(f) {
  const text = (findingFilter.text || "").toLowerCase();
  if (findingFilter.type && f.sessionType !== findingFilter.type) return false;
  if (findingFilter.confidence && f.confidence !== findingFilter.confidence) return false;
  if (!text) return true;
  return [f.project, f.controller, f.action, f.file, f.expression, f.sessionKey, f.operation, f.detectionMethod]
    .join(" ").toLowerCase().indexOf(text) >= 0;
}

function updateFindingsTable() {
  const rows = DATA.sessionFindings.filter(findingMatches);
  document.getElementById("findings-count").textContent = rows.length + " operation(s)";
  document.getElementById("findings-body").innerHTML = rows.map(f => `
    <tr>
      <td>${esc(f.project)}</td><td>${esc(f.controller)}</td><td>${esc(f.action)}()</td>
      <td>${esc(f.file)}</td><td>${esc(f.line)}</td>
      <td>${esc(f.operation)}${f.mutation && f.mutation !== f.operation ? " (" + esc(f.mutation) + ")" : ""}</td>
      <td>${esc(f.sessionKey)}</td>
      <td>${esc(f.sessionType)}</td>
      <td>${esc(f.detectionMethod)}</td>
      <td>${badge(f.confidence, f.confidence.toLowerCase())}</td>
      <td>${esc(f.expression)}</td>
      <td>${badge(f.canBeReadOnly === "YES" ? "YES" : (f.canBeReadOnly === "NO" ? "NO" : "N/A"), f.canBeReadOnly === "YES" ? "low" : (f.canBeReadOnly === "NO" ? "high" : "muted"))}</td>
    </tr>`).join("") || '<tr><td colspan="12" class="empty">No Session operations match the current filters.</td></tr>';
}

function renderFindings() {
  const el = document.getElementById("page-findings");
  el.innerHTML = `
    <h2>Session Detection View</h2>
    <div class="filters">
      <input id="f-text" placeholder="Filter: project / controller / action / file / expression / key..." value="${escAttr(findingFilter.text || "")}" oninput="findingFilter.text=this.value; updateFindingsTable()">
      <select id="f-type" onchange="findingFilter.type=this.value; updateFindingsTable()">
        <option value="">All types</option>
        <option value="Read" ${findingFilter.type === "Read" ? "selected" : ""}>Read</option>
        <option value="Write" ${findingFilter.type === "Write" ? "selected" : ""}>Write</option>
      </select>
      <select id="f-conf" onchange="findingFilter.confidence=this.value; updateFindingsTable()">
        <option value="">All confidence</option>
        <option value="High" ${findingFilter.confidence === "High" ? "selected" : ""}>High</option>
        <option value="Medium" ${findingFilter.confidence === "Medium" ? "selected" : ""}>Medium</option>
        <option value="Low" ${findingFilter.confidence === "Low" ? "selected" : ""}>Low</option>
      </select>
      <span class="counter" id="findings-count" style="margin:6px 0 0 auto"></span>
    </div>
    <div class="scroll">
      <table>
        <thead><tr>
          <th>Project</th><th>Controller</th><th>Action</th><th>File</th><th>Line</th>
          <th>Operation</th><th>Session Key</th><th>Read/Write</th><th>Detection Method</th>
          <th>Confidence</th><th>Expression</th><th>ReadOnly eligible</th>
        </tr></thead>
        <tbody id="findings-body"></tbody>
      </table>
    </div>`;
  updateFindingsTable();
}

/* ---------------- Investigations ---------------- */
function renderInvestigations() {
  const el = document.getElementById("page-investigations");
  const kinds = [
    ["CompilationProblem", "Compilation & Reference Problems"],
    ["RejectedDetection", "Rejected / Not-Detected Session Accesses"],
    ["NoSessionDetection", "No Session Usage Detected"],
    ["AnalyzerFailure", "Analyzer Failures"]
  ];
  let html = `<h2>Investigation View</h2>
    <div class="counter">${DATA.investigations.length} case(s) need attention.</div>`;

  kinds.forEach(kind => {
    const cases = DATA.investigations.filter(c => c.kind === kind[0]);
    if (cases.length === 0) return;
    html += `<h3>${esc(kind[1])} (${cases.length})</h3>`;
    cases.forEach(c => {
      html += `
      <div class="panel case ${esc(c.severity.toLowerCase())}">
        <div class="title">${esc(c.title)}</div>
        <div class="meta">
          Project: <strong>${esc(c.project)}</strong> ·
          Component: ${esc(c.component)} ·
          Severity: ${priorityBadge(c.severity)}
          ${c.file && c.file !== "-" ? " · File: " + esc(c.file) : ""}
        </div>
        <ul>${c.evidence.map(e => "<li>" + esc(e) + "</li>").join("")}</ul>
        <div class="reasons">
          <div class="rt">Possible reasons</div>
          ${c.possibleReasons.map(r => '<span class="pill">' + esc(r) + "</span>").join("") || '<span class="muted">None recorded.</span>'}
        </div>
        <div><strong>Action:</strong> ${esc(c.suggestedAction)}</div>
        <div style="margin-top:4px"><strong>Suggested fix:</strong> ${esc(c.suggestedFix)}</div>
      </div>`;
    });
  });

  html += DATA.investigations.length === 0
    ? '<div class="empty">No investigation cases — every project either has findings or is clean.</div>'
    : "";
  el.innerHTML = html;
}

/* ---------------- Action Center ---------------- */
const TODO_KEY = "sessionwalker-todo-";
function todoDone(id) { return localStorage.getItem(TODO_KEY + id) === "1"; }
function toggleTodo(id, checked) {
  if (checked) localStorage.setItem(TODO_KEY + id, "1");
  else localStorage.removeItem(TODO_KEY + id);
  renderActions();
}
function renderActions() {
  const el = document.getElementById("page-actions");
  const counts = { High: 0, Medium: 0, Low: 0 };
  DATA.todoItems.forEach(t => { counts[t.priority] = (counts[t.priority] || 0) + 1; });
  const all = DATA.todoItems.map(t => Object.assign({ done: todoDone(t.id) }, t));
  const open = all.filter(t => !t.done).length;

  const group = (title, items) => {
    if (!items.length) return "";
    return `
      <h3>${esc(title)} (${items.length})</h3>
      ${items.map(t => `
        <div class="todo ${t.done ? "done" : ""}">
          <input type="checkbox" ${t.done ? "checked" : ""} onchange="toggleTodo('${escAttr(t.id)}', this.checked)">
          <div class="body">
            <div>
              <span class="badge muted">${esc(t.id)}</span> ${priorityBadge(t.priority)}
              <span class="badge muted">${esc(t.category)}</span>
              <span class="badge muted">${esc(t.project)}</span>
            </div>
            <div class="problem">${esc(t.problem)}</div>
            <div class="evidence">Evidence: ${esc(t.evidence)}</div>
            <div class="solution"><strong>Suggested:</strong> ${esc(t.suggestedSolution)}</div>
            <div class="meta">Component: ${esc(t.component)} · Status: ${esc(t.status)} · Created: ${fmtDate(t.createdDate)}</div>
          </div>
        </div>`).join("")}`;
  };

  el.innerHTML = `
    <h2>Developer Action Center</h2>
    <div class="grid cards">
      <div class="card"><div class="label">Open tasks</div><div class="value">${open}</div></div>
      <div class="card bad"><div class="label">High</div><div class="value">${counts.High || 0}</div></div>
      <div class="card warn"><div class="label">Medium</div><div class="value">${counts.Medium || 0}</div></div>
      <div class="card ok"><div class="label">Low</div><div class="value">${counts.Low || 0}</div></div>
    </div>
    ${group("High priority", all.filter(t => t.priority === "High" && !t.done))}
    ${group("Medium priority", all.filter(t => t.priority === "Medium" && !t.done))}
    ${group("Low priority", all.filter(t => t.priority === "Low" && !t.done))}
    ${group("Completed (this browser)", all.filter(t => t.done))}
    ${all.length === 0 ? '<div class="empty">No tasks generated — the analysis found nothing actionable.</div>' : ""}`;
}

/* ---------------- Boot ---------------- */
document.getElementById("header-sub").textContent = S.solutionName + " · " + S.solutionPath + " · analyzed " + new Date(S.analyzedAtUtc).toLocaleString();
renderOverview();
</script>
</body>
</html>
""";
}
