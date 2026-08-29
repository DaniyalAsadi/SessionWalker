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
/// server-side dependencies (no CDN fonts, no icon libraries) — open it in
/// any browser, offline. Pages:
///   - Overview (solution summary + what was not detected)
///   - Projects (health table)
///   - Project Detail (per-controller/action findings)
///   - Session Findings (all detected operations, filterable)
///   - Investigations (why something may be missing / failed)
///   - Action Center (generated to-do list)
///
/// Visual design system ("trace console"):
///   The subject is a Roslyn analyzer that traces Session reads/writes
///   through a codebase, so the UI borrows from the instruments developers
///   already read: a compiler/analyzer console and an oscilloscope trace.
///   - Palette: deep ink-navy surfaces, an amber "signal" accent used for
///     brand + medium-severity, cyan/coral channel colors for Read/Write,
///     red for danger, green for OK.
///   - Type: a UI sans for chrome, a monospace face for every piece of real
///     source data (files, lines, expressions, keys) so the report reads
///     like it came out of the compiler, not a generic BI tool.
///   - Signature: a thin animated sweep line under the header (oscilloscope
///     trace, disabled under prefers-reduced-motion), ticked "ruler"
///     dividers under section headings, and three-bar "signal" glyphs that
///     encode detection confidence by amplitude as well as color.
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
<title>SessionWalker — Analysis Dashboard</title>
<style>
:root {
  color-scheme: dark;
  --ink: #0b1220;
  --steel: #121c30;
  --steel-2: #182543;
  --steel-3: #0e1729;
  --line: #253150;
  --paper: #e9edf6;
  --fog: #8a96b3;
  --signal: #e8a33d;
  --signal-dim: rgba(232, 163, 61, .16);
  --read: #5ac8e8;
  --read-dim: rgba(90, 200, 232, .14);
  --write: #f2795c;
  --write-dim: rgba(242, 121, 92, .14);
  --danger: #ef5a5a;
  --danger-dim: rgba(239, 90, 90, .14);
  --ok: #46d19c;
  --ok-dim: rgba(70, 209, 156, .14);
  --info: #7c9cff;
  --info-dim: rgba(124, 156, 255, .14);
  --radius: 8px;
  --header-h: 60px;
  --font-ui: "IBM Plex Sans", "Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
  --font-mono: "IBM Plex Mono", ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace;
}
* { box-sizing: border-box; }
html, body { height: 100%; }
body {
  margin: 0; background: var(--ink); color: var(--paper);
  font-family: var(--font-ui); font-size: 13.5px; line-height: 1.45;
}
button, input, select { font-family: inherit; }
:focus-visible { outline: 2px solid var(--signal); outline-offset: 2px; }
code {
  font-family: var(--font-mono); background: var(--steel-2); border: 1px solid var(--line);
  border-radius: 4px; padding: 1px 5px; font-size: 11.5px; color: var(--paper);
}

/* ---------------- Header + signature sweep ---------------- */
.topstrip {
  position: sticky; top: 0; z-index: 30; background: var(--steel);
  border-bottom: 1px solid var(--line); padding: 11px 20px;
  display: flex; align-items: baseline; gap: 14px; flex-wrap: wrap;
}
.brand { display: flex; align-items: baseline; gap: 8px; flex: 0 0 auto; }
.brand-mark { font-family: var(--font-mono); color: var(--signal); font-weight: 600; font-size: 15px; }
.brand-name { font-weight: 600; font-size: 14.5px; letter-spacing: .01em; }
.context {
  font-family: var(--font-mono); font-size: 11px; color: var(--fog);
  overflow-wrap: anywhere; flex: 1 1 260px;
}
.sweep { position: relative; height: 2px; overflow: hidden; background: var(--line); }
.sweep::after {
  content: ""; position: absolute; top: 0; left: -30%; width: 30%; height: 100%;
  background: linear-gradient(90deg, transparent, var(--signal), transparent);
  animation: sweep 5.5s linear infinite;
}
@keyframes sweep { from { left: -30%; } to { left: 100%; } }
@media (prefers-reduced-motion: reduce) {
  .sweep::after { animation: none; left: 0; width: 100%; opacity: .35; }
}

/* ---------------- Shell: rail + main ---------------- */
.shell { display: flex; align-items: flex-start; min-height: calc(100% - 62px); }
.rail {
  flex: 0 0 208px; width: 208px; position: sticky; top: var(--header-h);
  align-self: flex-start; height: calc(100vh - var(--header-h));
  overflow-y: auto; background: var(--steel); border-right: 1px solid var(--line);
  padding: 10px 0;
}
.rail button {
  display: flex; align-items: center; gap: 10px; width: 100%; text-align: left;
  background: transparent; border: 0; border-left: 3px solid transparent;
  color: var(--fog); padding: 9px 14px 9px 12px; font-size: 12.6px; letter-spacing: .01em;
  cursor: pointer;
}
.rail button svg { flex: 0 0 auto; width: 15px; height: 15px; opacity: .8; }
.rail button:hover { background: var(--steel-2); color: var(--paper); }
.rail button.active { background: var(--steel-2); color: var(--paper); border-left-color: var(--signal); }
.rail button.active svg { opacity: 1; color: var(--signal); }

main { flex: 1 1 auto; min-width: 0; padding: 20px 26px 64px; }
.page { display: none; }
.page.active { display: block; }

@media (max-width: 860px) {
  .shell { flex-direction: column; }
  .rail {
    position: sticky; top: var(--header-h); width: 100%; height: auto;
    display: flex; overflow-x: auto; border-right: 0; border-bottom: 1px solid var(--line);
  }
  .rail button { border-left: 0; border-bottom: 3px solid transparent; white-space: nowrap; }
  .rail button.active { border-left-color: transparent; border-bottom-color: var(--signal); }
  main { padding: 16px 16px 48px; }
}

/* ---------------- Section headings (ruler divider) ---------------- */
h2 {
  font-size: 15.5px; font-weight: 600; margin: 4px 0 14px; padding-bottom: 9px; color: var(--paper);
  background-image: repeating-linear-gradient(90deg, var(--line) 0 1px, transparent 1px 8px);
  background-position: 0 100%; background-repeat: repeat-x; background-size: 100% 1px;
}
h3 { font-size: 13px; font-weight: 600; margin: 20px 0 8px; color: var(--paper); }

/* ---------------- Stat cards ---------------- */
.stat-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(168px, 1fr)); gap: 10px; }
.stat {
  background: var(--steel); border: 1px solid var(--line); border-left: 3px solid var(--line);
  border-radius: var(--radius); padding: 12px 14px;
}
.stat--warn { border-left-color: var(--signal); }
.stat--bad { border-left-color: var(--danger); }
.stat--ok { border-left-color: var(--ok); }
.stat .label { font-family: var(--font-mono); font-size: 10.3px; letter-spacing: .06em; text-transform: uppercase; color: var(--fog); }
.stat .value { font-family: var(--font-mono); font-size: 24px; font-weight: 600; margin-top: 4px; }
.stat .hint { font-size: 11px; color: var(--fog); margin-top: 3px; }

/* ---------------- Panels / key-value ---------------- */
.panel { background: var(--steel); border: 1px solid var(--line); border-radius: var(--radius); padding: 14px 16px; margin-bottom: 14px; }
.kv { display: grid; grid-template-columns: 200px 1fr; gap: 5px 14px; }
.kv dt { color: var(--fog); font-size: 12px; }
.kv dd { margin: 0; font-family: var(--font-mono); font-size: 12.5px; overflow-wrap: anywhere; }

/* ---------------- Diagnostic log (overview "not detected" panel) ---------------- */
.log { font-family: var(--font-mono); font-size: 12px; border: 1px solid var(--line); border-radius: var(--radius); overflow: hidden; }
.log-row { display: flex; gap: 10px; padding: 10px 12px; border-bottom: 1px solid var(--line); background: var(--steel); }
.log-row:last-child { border-bottom: 0; }
.log-tag { flex: 0 0 auto; font-weight: 700; letter-spacing: .02em; }
.log-row--ok .log-tag { color: var(--ok); }
.log-row--warn .log-tag { color: var(--signal); }
.log-row--bad .log-tag { color: var(--danger); }
.log-row--info .log-tag { color: var(--info); }
.log-body { color: var(--fog); }
.log-body strong { color: var(--paper); font-weight: 600; }

/* ---------------- Tables ---------------- */
.scroll { overflow-x: auto; border: 1px solid var(--line); border-radius: var(--radius); }
table { border-collapse: collapse; width: 100%; background: var(--steel); font-size: 12.3px; }
th {
  background: var(--steel-2); color: var(--paper); text-align: left; font-size: 10.6px;
  letter-spacing: .05em; text-transform: uppercase; padding: 8px 9px; white-space: nowrap;
  top: var(--header-h); border-bottom: 1px solid var(--line);
}
td { border-bottom: 1px solid var(--line); padding: 7px 9px; vertical-align: top; }
td.mono, .mono { font-family: var(--font-mono); }
tr.clickable { cursor: pointer; }
tr.clickable:hover td { background: var(--steel-2); }
tr.expanded td { background: var(--steel-2); }
tr.no-detail { cursor: default; }
tr.no-detail:hover td { background: inherit; }
.detail-row td { background: var(--steel-3); }

/* ---------------- Badges ---------------- */
.badge {
  display: inline-flex; align-items: center; gap: 5px; padding: 2px 8px; border-radius: 4px;
  font-family: var(--font-mono); font-size: 10.3px; font-weight: 600; letter-spacing: .03em;
  text-transform: uppercase; white-space: nowrap; border: 1px solid transparent;
}
.badge--danger { background: var(--danger-dim); color: var(--danger); border-color: rgba(239,90,90,.35); }
.badge--signal { background: var(--signal-dim); color: var(--signal); border-color: rgba(232,163,61,.35); }
.badge--ok { background: var(--ok-dim); color: var(--ok); border-color: rgba(70,209,156,.35); }
.badge--info { background: var(--info-dim); color: var(--info); border-color: rgba(124,156,255,.35); }
.badge--muted { background: var(--steel-2); color: var(--fog); border-color: var(--line); }
.badge--read { background: var(--read-dim); color: var(--read); border-color: rgba(90,200,232,.35); }
.badge--write { background: var(--write-dim); color: var(--write); border-color: rgba(242,121,92,.35); }

/* Three-bar "signal" glyph — encodes confidence by amplitude, not just color */
.sig { display: inline-flex; align-items: flex-end; gap: 2px; height: 9px; }
.sig i { width: 2.5px; display: block; border-radius: 1px; background: currentColor; opacity: .25; }
.sig i:nth-child(1) { height: 4px; }
.sig i:nth-child(2) { height: 6.5px; }
.sig i:nth-child(3) { height: 9px; }
.sig.lit-1 i:nth-child(1) { opacity: 1; }
.sig.lit-2 i:nth-child(1), .sig.lit-2 i:nth-child(2) { opacity: 1; }
.sig.lit-3 i { opacity: 1; }

/* ---------------- Filters ---------------- */
.filters { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 10px; align-items: center; }
.filters input, .filters select {
  background: var(--steel); border: 1px solid var(--line); color: var(--paper);
  padding: 7px 10px; border-radius: 6px; font-size: 12.5px;
}
.filters input { min-width: 260px; }
.filters input::placeholder { color: var(--fog); }
.counter { font-size: 11.5px; color: var(--fog); margin: 6px 0; font-family: var(--font-mono); }

/* ---------------- Investigation cases ---------------- */
.case { border-left: 4px solid var(--info); }
.case.high, .case.danger { border-left-color: var(--danger); }
.case.medium, .case.signal { border-left-color: var(--signal); }
.case.low, .case.ok { border-left-color: var(--ok); }
.case .title { font-weight: 600; margin: 2px 0 4px; }
.case .meta { font-size: 11.5px; color: var(--fog); margin-bottom: 8px; }
.case ul { margin: 6px 0 10px 20px; padding: 0; }
.case li { margin: 2px 0; overflow-wrap: anywhere; }
.reasons { background: var(--steel-3); border: 1px dashed var(--line); border-radius: 6px; padding: 8px 10px; margin: 8px 0; }
.reasons .rt { font-family: var(--font-mono); font-size: 10.5px; color: var(--fog); text-transform: uppercase; letter-spacing: .04em; margin-bottom: 5px; }
.pill { display: inline-block; background: var(--steel-2); border: 1px solid var(--line); color: var(--paper); border-radius: 10px; padding: 2px 9px; font-size: 11px; margin: 0 4px 4px 0; }

/* ---------------- Action center / todo ---------------- */
.todo { display: flex; gap: 10px; padding: 11px 12px; border: 1px solid var(--line); border-radius: var(--radius); margin-bottom: 8px; background: var(--steel); align-items: flex-start; }
.todo input[type="checkbox"] { margin-top: 3px; accent-color: var(--signal); width: 15px; height: 15px; }
.todo .body { flex: 1; min-width: 0; }
.todo .problem { font-weight: 600; }
.todo .evidence { font-family: var(--font-mono); font-size: 11.5px; color: var(--fog); margin: 4px 0; overflow-wrap: anywhere; }
.todo .solution { font-size: 12.5px; margin-top: 5px; }
.todo .meta { font-size: 11px; color: var(--fog); margin-top: 6px; }
.todo.done { opacity: .5; }
.todo.done .problem { text-decoration: line-through; }

.muted { color: var(--fog); }
.empty { padding: 26px; text-align: center; color: var(--fog); }
footer { color: var(--fog); font-size: 11.5px; padding: 18px 20px 26px; font-family: var(--font-mono); }
</style>
</head>
<body>
<header class="topstrip">
  <div class="brand"><span class="brand-mark">&gt;_</span><span class="brand-name">SessionWalker</span></div>
  <div class="context" id="header-sub"></div>
</header>
<div class="sweep" aria-hidden="true"></div>
<div class="shell">
  <nav class="rail" aria-label="Dashboard sections">
    <button data-tab="overview" class="active" onclick="showTab('overview')">
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3"><rect x="1.5" y="1.5" width="5.5" height="5.5" rx="1"/><rect x="9" y="1.5" width="5.5" height="5.5" rx="1"/><rect x="1.5" y="9" width="5.5" height="5.5" rx="1"/><rect x="9" y="9" width="5.5" height="5.5" rx="1"/></svg>
      <span>Overview</span>
    </button>
    <button data-tab="projects" onclick="showTab('projects')">
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"><path d="M8 1.5 14.5 5 8 8.5 1.5 5Z"/><path d="M1.5 8.5 8 12l6.5-3.5"/><path d="M1.5 11.5 8 15l6.5-3.5"/></svg>
      <span>Projects</span>
    </button>
    <button data-tab="detail" onclick="showTab('detail')">
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"><path d="M3.5 1.5h6l3 3v10h-9Z"/><path d="M9.5 1.5v3h3"/><path d="M5.5 8.5h5M5.5 11h5"/></svg>
      <span>Project detail</span>
    </button>
    <button data-tab="findings" onclick="showTab('findings')">
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3"><circle cx="6.8" cy="6.8" r="4.3"/><path d="M10.2 10.2 14.5 14.5" stroke-linecap="round"/></svg>
      <span>Session findings</span>
    </button>
    <button data-tab="investigations" onclick="showTab('investigations')">
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"><path d="M6.3 1.8h3.4M6.8 1.8v4.1L3 12.3c-.6 1 .1 2.2 1.3 2.2h7.4c1.2 0 1.9-1.2 1.3-2.2L9.2 5.9V1.8"/><path d="M5 9.8h6"/></svg>
      <span>Investigations</span>
    </button>
    <button data-tab="actions" onclick="showTab('actions')">
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round" stroke-linecap="round"><rect x="1.8" y="1.8" width="12.4" height="12.4" rx="2"/><path d="M4.8 8.2l2 2 4.4-4.6"/></svg>
      <span>Action center</span>
    </button>
  </nav>
  <main>
    <section id="page-overview" class="page active"></section>
    <section id="page-projects" class="page"></section>
    <section id="page-detail" class="page"></section>
    <section id="page-findings" class="page"></section>
    <section id="page-investigations" class="page"></section>
    <section id="page-actions" class="page"></section>
  </main>
</div>
<footer>
  Data lives inside this file — nothing is re-analyzed when you open it. Run with --excel &lt;path&gt; for the Excel task list.
</footer>
<script id="dashboard-data" type="application/json">__SESSIONWALKER_DASHBOARD_DATA__</script>
<script>
"use strict";
const DATA = JSON.parse(document.getElementById("dashboard-data").textContent);
const S = DATA.solution;

/* ---------------- Helpers ---------------- */
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
  return '<span class="badge badge--' + esc(cls || "muted") + '">' + esc(text) + "</span>";
}
function sigBars(lit) {
  return '<span class="sig lit-' + lit + '" aria-hidden="true"><i></i><i></i><i></i></span>';
}
/** Urgency scale: High = red/urgent, Medium = amber, Low = green/calm. Used for priority, severity and status fields. */
function severityBadge(level) {
  const key = String(level || "").toLowerCase();
  const cls = key === "high" ? "danger" : key === "medium" ? "signal" : key === "low" ? "ok" : "muted";
  return badge(level, cls);
}
/** Confidence scale: how sure the detector is, not how bad the finding is — High reads as solid/green, Low as a faint signal, not an alarm. */
function confidenceBadge(level) {
  const key = String(level || "").toLowerCase();
  const cls = key === "high" ? "ok" : key === "medium" ? "signal" : key === "low" ? "muted" : "muted";
  const lit = key === "high" ? 3 : key === "medium" ? 2 : key === "low" ? 1 : 0;
  return '<span class="badge badge--' + cls + '">' + sigBars(lit) + esc(level) + "</span>";
}
function channelBadge(value) {
  const key = String(value || "").toLowerCase();
  if (key === "read") return badge(value, "read");
  if (key === "write") return badge(value, "write");
  return badge(value, "muted");
}
function boolBadge(v) {
  const s = typeof v === "boolean" ? (v ? "YES" : "NO") : (v === null || v === undefined ? "" : String(v).toUpperCase());
  if (s === "YES") return badge("YES", "ok");
  if (s === "NO") return badge("NO", "danger");
  return badge("N/A", "muted");
}
function statusBadge(status) {
  const map = { "Clean": "ok", "Needs Review": "signal", "No Session Usage": "info", "Not Analyzed": "danger" };
  return badge(status, map[status] || "muted");
}

function syncHeaderHeight() {
  const strip = document.querySelector(".topstrip");
  const sweep = document.querySelector(".sweep");
  document.documentElement.style.setProperty("--header-h", (strip.offsetHeight + sweep.offsetHeight) + "px");
}
window.addEventListener("resize", syncHeaderHeight);

function showTab(name) {
  document.querySelectorAll(".page").forEach(p => p.classList.remove("active"));
  document.getElementById("page-" + name).classList.add("active");
  document.querySelectorAll(".rail button").forEach(b => b.classList.toggle("active", b.dataset.tab === name));
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
  const stat = (label, value, cls, hint) =>
    '<div class="stat ' + (cls || "") + '"><div class="label">' + esc(label) + '</div>' +
    '<div class="value">' + esc(value) + '</div>' +
    (hint ? '<div class="hint">' + esc(hint) + "</div>" : "") + "</div>";

  const notAnalyzed = S.projectsNotAnalyzed > 0;
  const withIssues = S.projectsWithIssues > 0;
  const noSession = S.projectsWithoutSessionUsage > 0;
  const rejected = S.totalRejectedDetections > 0;

  let logRows = "";
  logRows += rejected
    ? '<div class="log-row log-row--bad"><span class="log-tag">[WARN]</span><span class="log-body"><strong>' + esc(S.totalRejectedDetections) + '</strong> Session-like access(es) were rejected during detection. Open <strong>Investigations</strong> to see why.</span></div>'
    : '<div class="log-row log-row--ok"><span class="log-tag">[OK]</span><span class="log-body">No Session-like access was rejected — every candidate either matched the known Session type mapping or was not Session-shaped.</span></div>';
  if (noSession) {
    logRows += '<div class="log-row log-row--warn"><span class="log-tag">[WARN]</span><span class="log-body"><strong>' + esc(S.projectsWithoutSessionUsage) + '</strong> project(s) have controllers but zero Session findings. Confirm these are genuine negatives.</span></div>';
  }
  if (notAnalyzed) {
    logRows += '<div class="log-row log-row--info"><span class="log-tag">[INFO]</span><span class="log-body"><strong>' + esc(S.projectsNotAnalyzed) + '</strong> project(s) were not analyzed — their Session usage is unknown.</span></div>';
  }

  el.innerHTML = `
  <h2>Solution information</h2>
  <div class="panel">
    <dl class="kv">
      <dt>Solution</dt><dd>${esc(S.solutionName)}</dd>
      <dt>Solution path</dt><dd>${esc(S.solutionPath)}</dd>
      <dt>Analysis timestamp</dt><dd>${fmtDate(S.analyzedAtUtc)}</dd>
    </dl>
  </div>
  <h2>Analysis summary</h2>
  <div class="stat-grid">
    ${stat("Projects", S.totalProjects, "", "analyzed")}
    ${stat("Documents", S.totalDocuments, "", "analyzed")}
    ${stat("Controllers", S.totalControllers, "", "discovered")}
    ${stat("Actions analyzed", S.totalActions, "", "MVC actions")}
    ${stat("Actions using Session", S.totalActionsWithSession, S.totalActionsWithSession > 0 ? "stat--ok" : "")}
    ${stat("Session operations", S.totalSessionOperations, S.totalSessionOperations > 0 ? "stat--ok" : "")}
    ${stat("Diagnostics", S.totalDiagnostics, withIssues ? "stat--warn" : "stat--ok")}
    ${stat("Compilation errors", S.totalCompilationErrors, S.totalCompilationErrors > 0 ? "stat--bad" : "stat--ok")}
    ${stat("Warnings", S.totalWarnings, S.totalWarnings > 0 ? "stat--warn" : "stat--ok")}
    ${stat("Projects with issues", S.projectsWithIssues, withIssues ? "stat--bad" : "stat--ok")}
    ${stat("Projects not analyzed", S.projectsNotAnalyzed, notAnalyzed ? "stat--bad" : "stat--ok")}
    ${stat("Projects w/o Session usage", S.projectsWithoutSessionUsage, noSession ? "stat--warn" : "stat--ok")}
    ${stat("Rejected detections", S.totalRejectedDetections, rejected ? "stat--bad" : "stat--ok", "Session-like accesses not detected")}
  </div>
  <h2>What was not detected — investigate</h2>
  <div class="log">${logRows}</div>`;
}

/* ---------------- Projects ---------------- */
function renderProjects() {
  const rows = DATA.projects.map(p => `
    <tr class="clickable" data-project="${esc(p.projectName)}" onclick="selectProject(this.dataset.project)">
      <td>${severityBadge(p.priority)}</td>
      <td><strong>${esc(p.projectName)}</strong></td>
      <td class="mono">${esc(p.assemblyName)}</td>
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
    <h2>Project analysis</h2>
    <div class="counter">${DATA.projects.length} project(s) — click a row to open Project detail.</div>
    <div class="scroll">
      <table>
        <thead><tr>
          <th>Priority</th><th>Project</th><th>Assembly</th><th>Documents</th><th>Controllers</th>
          <th>Actions</th><th>Session actions</th><th>Session ops</th><th>Compilation errors</th>
          <th>Status</th><th>Developer action</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="11" class="empty">No projects in this analysis.</td></tr>'}</tbody>
      </table>
    </div>`;
}

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
        <th>Session key</th><th>Read/Write</th><th>Detection method</th><th>Confidence</th></tr></thead>
        <tbody>${ops.map(o => `
          <tr>
            <td class="mono">${esc(o.expression)}</td><td class="mono">${esc(o.file)}</td><td class="mono">${esc(o.line)}</td>
            <td>${esc(o.operation)}</td><td class="mono">${esc(o.sessionKey)}</td><td>${channelBadge(o.sessionType)}</td>
            <td>${esc(o.detectionMethod)}</td><td>${confidenceBadge(o.confidence)}</td>
          </tr>`).join("")}</tbody>
      </table>
    </div>`;
}

function renderProjectDetail() {
  const el = document.getElementById("page-detail");
  if (!currentProject && DATA.projects.length > 0) currentProject = DATA.projects[0].projectName;
  const p = DATA.projects.find(x => x.projectName === currentProject);

  if (!p) {
    el.innerHTML = '<h2>Project detail</h2><div class="empty">No projects available.</div>';
    return;
  }

  const options = DATA.projects
    .map(x => '<option value="' + esc(x.projectName) + '"' + (x.projectName === p.projectName ? " selected" : "") + ">" + esc(x.projectName) + "</option>")
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
      const readonly = a.sessionOperations > 0 ? boolBadge(a.canBeReadOnly) : badge("—", "muted");
      const opsHtml = actionOperationsHtml(a);
      const rowClass = opsHtml ? "clickable" : "clickable no-detail";
      const rowClick = opsHtml ? ' onclick="toggleAction(this)"' : "";
      return `
        <tr class="${rowClass}"${rowClick}>
          <td>${esc(c.controller)}</td>
          <td class="mono">${esc(a.action)}()</td>
          <td class="mono">${esc(a.file)}</td>
          <td class="mono">${esc(a.line)}</td>
          <td>${usage}</td>
          <td>${conf ? confidenceBadge(conf) : badge("—", "muted")}</td>
          <td>${readonly}</td>
        </tr>` + (opsHtml ? `<tr class="detail-row" style="display:none"><td colspan="7">${opsHtml}</td></tr>` : "");
    }).join("");

    return `
      <h3>${esc(c.controller)} (${(c.actions || []).length} action(s))</h3>
      <div class="scroll">
        <table>
          <thead><tr>
            <th>Controller</th><th>Action</th><th>File</th><th>Line</th>
            <th>Session usage</th><th>Confidence</th><th>Read-only eligible</th>
          </tr></thead>
          <tbody>${rows || '<tr><td colspan="7" class="empty">No actions discovered in this controller.</td></tr>'}</tbody>
        </table>
      </div>`;
  }).join("");

  const rejected = DATA.diagnostics.filter(d => d.project === p.projectName && d.category === "Detection Gap");

  el.innerHTML = `
    <h2>Project detail</h2>
    <div class="filters">
      <select onchange="currentProject=this.value; renderProjectDetail()">${options}</select>
    </div>
    <div class="panel">
      <h3 style="margin-top:0">General information</h3>
      <dl class="kv">
        <dt>Project name</dt><dd>${esc(p.projectName)}</dd>
        <dt>Assembly name</dt><dd>${esc(p.assemblyName)}</dd>
        <dt>Project path</dt><dd>${esc(p.projectPath)}</dd>
        <dt>Documents</dt><dd>${esc(p.documents)}</dd>
        <dt>Status</dt><dd>${statusBadge(p.status)} ${severityBadge(p.priority)}</dd>
        <dt>Developer action</dt><dd>${esc(p.developerAction)}</dd>
      </dl>
    </div>
    <h2>Analysis result</h2>
    <div class="counter">${controllers.length} controller(s), ${totalActions} action(s), ${totalOps} detected Session operation(s). Click an action row to inspect its operations.</div>
    ${controllerHtml || '<div class="empty">No controllers discovered for this project — open <strong>Investigations</strong> for possible reasons.</div>'}
    ${rejected.length ? `
      <h2>Rejected (not detected) Session-like accesses</h2>
      <div class="scroll"><table>
        <thead><tr><th>File</th><th>Line</th><th>Message</th><th>Impact</th><th>Suggested fix</th></tr></thead>
        <tbody>${rejected.map(d => `
          <tr>
            <td class="mono">${esc(d.file)}</td><td class="mono">${esc(d.line)}</td>
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
    <td class="mono">${esc(f.index)}</td>
      <td>${esc(f.project)}</td><td>${esc(f.controller)}</td><td class="mono">${esc(f.action)}()</td>
      <td class="mono">${esc(f.file)}</td><td class="mono">${esc(f.line)}</td>
      <td>${esc(f.operation)}${f.mutation && f.mutation !== f.operation ? " (" + esc(f.mutation) + ")" : ""}</td>
      <td class="mono">${esc(f.sessionKey)}</td>
      <td>${channelBadge(f.sessionType)}</td>
      <td>${esc(f.detectionMethod)}</td>
      <td>${confidenceBadge(f.confidence)}</td>
      <td class="mono">${esc(f.expression)}</td>
      <td>${boolBadge(f.canBeReadOnly)}</td>
    </tr>`).join("") || '<tr><td colspan="12" class="empty">No Session operations match the current filters.</td></tr>';
}

function renderFindings() {
  const el = document.getElementById("page-findings");
  el.innerHTML = `
    <h2>Session detection view</h2>
    <div class="filters">
      <input id="f-text" placeholder="Filter: project / controller / action / file / expression / key..." value="${esc(findingFilter.text || "")}" oninput="findingFilter.text=this.value; updateFindingsTable()">
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
      <span class="counter" id="findings-count" style="margin:0 0 0 auto"></span>
    </div>
    <div class="scroll">
      <table>
        <thead><tr>
          <th>#</th><th>Project</th><th>Controller</th><th>Action</th><th>File</th><th>Line</th>
          <th>Operation</th><th>Session key</th><th>Read/Write</th><th>Detection method</th>
          <th>Confidence</th><th>Expression</th><th>Read-only eligible</th>
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
    ["CompilationProblem", "Compilation & reference problems"],
    ["RejectedDetection", "Rejected / not-detected Session accesses"],
    ["NoSessionDetection", "No Session usage detected"],
    ["AnalyzerFailure", "Analyzer failures"]
  ];
  let html = `<h2>Investigation view</h2>
    <div class="counter">${DATA.investigations.length} case(s) need attention.</div>`;

  kinds.forEach(kind => {
    const cases = DATA.investigations.filter(c => c.kind === kind[0]);
    if (cases.length === 0) return;
    html += `<h3>${esc(kind[1])} (${cases.length})</h3>`;
    cases.forEach(c => {
      html += `
      <div class="panel case ${esc(String(c.severity).toLowerCase())}">
        <div class="title">${esc(c.title)}</div>
        <div class="meta">
          Project: <strong>${esc(c.project)}</strong> ·
          Component: ${esc(c.component)} ·
          Severity: ${severityBadge(c.severity)}
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
          <input type="checkbox" ${t.done ? "checked" : ""} onchange="toggleTodo('${esc(t.id)}', this.checked)">
          <div class="body">
            <div>
              <span class="badge badge--muted">${esc(t.id)}</span> ${severityBadge(t.priority)}
              <span class="badge badge--muted">${esc(t.category)}</span>
              <span class="badge badge--muted">${esc(t.project)}</span>
            </div>
            <div class="problem">${esc(t.problem)}</div>
            <div class="evidence">Evidence: ${esc(t.evidence)}</div>
            <div class="solution"><strong>Suggested:</strong> ${esc(t.suggestedSolution)}</div>
            <div class="meta">Component: ${esc(t.component)} · Status: ${esc(t.status)} · Created: ${fmtDate(t.createdDate)}</div>
          </div>
        </div>`).join("")}`;
  };

  el.innerHTML = `
    <h2>Action center</h2>
    <div class="stat-grid">
      <div class="stat"><div class="label">Open tasks</div><div class="value">${open}</div></div>
      <div class="stat stat--bad"><div class="label">High</div><div class="value">${counts.High || 0}</div></div>
      <div class="stat stat--warn"><div class="label">Medium</div><div class="value">${counts.Medium || 0}</div></div>
      <div class="stat stat--ok"><div class="label">Low</div><div class="value">${counts.Low || 0}</div></div>
    </div>
    ${group("High priority", all.filter(t => t.priority === "High" && !t.done))}
    ${group("Medium priority", all.filter(t => t.priority === "Medium" && !t.done))}
    ${group("Low priority", all.filter(t => t.priority === "Low" && !t.done))}
    ${group("Completed (this browser)", all.filter(t => t.done))}
    ${all.length === 0 ? '<div class="empty">No tasks generated — the analysis found nothing actionable.</div>' : ""}`;
}

/* ---------------- Boot ---------------- */
document.getElementById("header-sub").textContent = S.solutionName + " · " + S.solutionPath + " · analyzed " + new Date(S.analyzedAtUtc).toLocaleString();
syncHeaderHeight();
renderOverview();
</script>
</body>
</html>
""";
}