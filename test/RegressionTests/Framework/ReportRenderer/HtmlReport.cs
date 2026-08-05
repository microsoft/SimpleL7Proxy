using System.Globalization;
using System.Net;
using System.Text;

namespace RegressionReportRenderer;

internal static class HtmlReport
{
    public static string Render(ReportManifest manifest)
    {
        var tests = manifest.Executions
            .SelectMany(execution => execution.Tests.Select(test =>
            {
                test.ExecutionLabel = execution.Label;
                return test;
            }))
            .OrderBy(test => test.Domain, StringComparer.Ordinal)
            .ThenBy(test => test.Feature, StringComparer.Ordinal)
            .ThenBy(test => OutcomeRank(test.Outcome))
            .ThenBy(test => test.Title, StringComparer.Ordinal)
            .ThenBy(test => test.Name, StringComparer.Ordinal)
            .ToList();

        var total = tests.Count;
        var passed = tests.Count(test => test.Outcome == "Passed");
        var failed = tests.Count(test => OutcomeClass(test.Outcome) == "failed");
        var other = total - passed - failed;
        var overallFailed = failed > 0 || manifest.Executions.Any(execution =>
            execution.ExitCode != 0 || !string.IsNullOrEmpty(execution.ParseError));

        var featureOptions = tests
            .Select(test => (Key: HierarchyKey(test), Label: $"{test.Domain} / {test.Feature}"))
            .Distinct()
            .OrderBy(item => item.Label, StringComparer.Ordinal)
            .Select(item => $"<option value=\"{Encode(item.Key)}\">{Encode(item.Label)}</option>");

        return Template
            .Replace("%%PAGE_TITLE%%", Encode($"Regression results: {manifest.MasterRunId}"), StringComparison.Ordinal)
            .Replace("%%MASTER_RUN_ID%%", Encode(manifest.MasterRunId), StringComparison.Ordinal)
            .Replace("%%EXECUTION_SUMMARY%%", Encode($"{manifest.Executions.Count} executions - Combined test time {FormatDuration(tests.Sum(test => test.DurationMs))} - Updated {manifest.UpdatedUtc}"), StringComparison.Ordinal)
            .Replace("%%OVERALL_CLASS%%", overallFailed ? "failed" : "passed", StringComparison.Ordinal)
            .Replace("%%OVERALL_STATUS%%", overallFailed ? "FAILED" : "PASSED", StringComparison.Ordinal)
            .Replace("%%TOTAL%%", total.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%PASSED%%", passed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%FAILED%%", failed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%OTHER%%", other.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%FEATURE_OPTIONS%%", string.Concat(featureOptions), StringComparison.Ordinal)
            .Replace("%%HIERARCHY%%", RenderHierarchy(tests), StringComparison.Ordinal)
            .Replace("%%DIAGNOSTICS%%", string.Concat(manifest.Executions.Select(RenderDiagnostics)), StringComparison.Ordinal)
            .Replace("%%EXECUTION_COUNT%%", manifest.Executions.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string RenderHierarchy(IReadOnlyList<TestRecord> tests)
    {
        var builder = new StringBuilder();
        foreach (var domain in tests.GroupBy(test => test.Domain))
        {
            builder.Append("<section class=\"domain-group\" data-domain=\"").Append(Encode(domain.Key)).Append("\">")
                .Append("<div class=\"domain-header\"><h2>").Append(Encode(domain.Key)).Append("</h2><span>")
                .Append(domain.Count(test => test.Outcome == "Passed")).Append(" / ").Append(domain.Count()).Append(" passed</span></div>");
            foreach (var feature in domain.GroupBy(test => test.Feature))
            {
                var first = feature.First();
                builder.Append("<section class=\"feature-group\" data-feature-group=\"").Append(Encode(HierarchyKey(first))).Append("\">")
                    .Append("<div class=\"feature-header\"><div><h3>").Append(Encode(feature.Key)).Append("</h3><p>")
                    .Append(Encode(first.Why)).Append("</p></div><span>")
                    .Append(feature.Count(test => test.Outcome == "Passed")).Append(" / ").Append(feature.Count()).Append(" passed</span></div>")
                    .Append("<div class=\"feature-tests\">");
                foreach (var test in feature) builder.Append(RenderTest(test));
                builder.Append("</div></section>");
            }
            builder.Append("</section>");
        }
        return builder.ToString();
    }

    private static string RenderTest(TestRecord test)
    {
        var state = OutcomeClass(test.Outcome);
        var hasDetails = !string.IsNullOrEmpty(test.Stdout) || !string.IsNullOrEmpty(test.Stderr) ||
                         !string.IsNullOrEmpty(test.ErrorMessage) || !string.IsNullOrEmpty(test.StackTrace) ||
                         test.Artifacts.Count > 0;
        var search = string.Join(' ', new[]
        {
            test.Name, test.Title, test.Description, test.Domain, test.Feature, test.Why,
            test.ClassName, string.Join(' ', test.Categories), test.ExecutionLabel, test.Outcome
        }).ToLowerInvariant();
        var columns = $"<span class=\"test-status\"><span class=\"status-dot {state}\"></span>{Encode(test.Outcome)}</span>" +
                      $"<span class=\"test-summary\" title=\"{Encode(test.Name)}\"><span class=\"test-title\">{Encode(test.Title)}</span><span class=\"test-description\">{Encode(test.Description)}</span></span>" +
                      $"<span class=\"duration\">{Encode(FormatDuration(test.DurationMs))}</span>" +
                      $"<span class=\"detail-label\">{(hasDetails ? "Details" : string.Empty)}</span>";
        var attributes = $"class=\"test-row {state}{(hasDetails ? " has-details" : string.Empty)}\" data-status=\"{state}\" data-feature=\"{Encode(HierarchyKey(test))}\" data-search=\"{Encode(search)}\"";
        if (!hasDetails) return $"<div {attributes}>{columns}</div>";

        var details = RenderOutput("Test output", test.Stdout) + RenderOutput("Standard error", test.Stderr) +
                      RenderOutput("Failure", test.ErrorMessage) + RenderOutput("Stack trace", test.StackTrace) +
                      (test.Artifacts.Count == 0
                          ? string.Empty
                          : "<h5>Artifacts</h5><div class=\"artifact-links\">" +
                            string.Join(" &middot; ", test.Artifacts.Select(path =>
                                $"<a href=\"{Encode(path)}\">{Encode(Path.GetFileName(path))}</a>")) +
                            "</div>");
        return $"<details {attributes}{(state == "failed" ? " open" : string.Empty)}><summary>{columns}</summary>" +
               $"<div class=\"test-body\"><div class=\"test-context\"><span><strong>Test:</strong> {Encode(test.Name)}</span>" +
               $"<span><strong>Hierarchy:</strong> {Encode(test.Domain)} / {Encode(test.Feature)}</span>" +
               $"<span><strong>Source:</strong> {Encode(ShortClass(test.ClassName))}</span>" +
               $"<span><strong>Execution:</strong> {Encode(test.ExecutionLabel)}</span><span><strong>Started:</strong> {Encode(test.StartTime)}</span></div>{details}</div></details>";
    }

    private static string RenderDiagnostics(ExecutionRecord execution)
    {
        var failed = execution.ExitCode != 0 || execution.Summary.Failed > 0 || !string.IsNullOrEmpty(execution.ParseError);
        var state = failed ? "failed" : "passed";
        var links = new List<string>();
        if (!string.IsNullOrEmpty(execution.TrxPath)) links.Add($"<a href=\"{Encode(execution.TrxPath)}\">TRX</a>");
        if (!string.IsNullOrEmpty(execution.ConsoleLog)) links.Add($"<a href=\"{Encode(execution.ConsoleLog)}\">Full console log</a>");
        return $"<details class=\"diagnostic-item {state}\"{(failed ? " open" : string.Empty)}><summary>" +
               $"<span class=\"status-dot {state}\"></span><span class=\"execution-name\">{Encode(execution.Label)}</span>" +
               $"<span class=\"counts\">{execution.Summary.Passed} passed - {execution.Summary.Failed} failed - {execution.Summary.Skipped + execution.Summary.Inconclusive} other</span>" +
               $"<span class=\"exit-code\">Exit {execution.ExitCode}</span></summary><div class=\"diagnostic-body\"><dl>" +
               $"<div><dt>Started</dt><dd>{Encode(execution.StartedUtc)}</dd></div><div><dt>Completed</dt><dd>{Encode(execution.CompletedUtc)}</dd></div>" +
               $"<div><dt>Exit code</dt><dd>{execution.ExitCode}</dd></div><div><dt>Artifacts</dt><dd>{(links.Count > 0 ? string.Join(" &middot; ", links) : "None")}</dd></div></dl>" +
               RenderOutput("TRX parse error", execution.ParseError) +
               $"<details class=\"raw-diagnostics\"><summary>Command and console</summary><div class=\"raw-body\"><h5>Command</h5><pre>{Encode(execution.Command)}</pre>" +
               RenderOutput("Console output (last 200 lines)", execution.ConsoleTail) + "</div></details></div></details>";
    }

    private static string RenderOutput(string title, string value)
        => string.IsNullOrEmpty(value) ? string.Empty : $"<h5>{Encode(title)}</h5><pre>{Encode(value)}</pre>";

    private static string OutcomeClass(string outcome)
        => outcome switch
        {
            "Passed" => "passed",
            "Failed" or "Error" or "Timeout" or "Aborted" => "failed",
            "NotExecuted" or "NotRunnable" or "Disconnected" => "skipped",
            _ => "inconclusive"
        };

    private static int OutcomeRank(string outcome)
        => outcome switch
        {
            "Failed" => 0,
            "Error" => 1,
            "Timeout" => 2,
            "Aborted" => 3,
            "Inconclusive" => 4,
            "NotExecuted" => 5,
            "Passed" => 6,
            _ => 99
        };

    private static string HierarchyKey(TestRecord test) => $"{test.Domain}::{test.Feature}";
    private static string ShortClass(string value) => value.Split('.').LastOrDefault() ?? value;
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FormatDuration(double milliseconds)
    {
        if (milliseconds < 1000) return $"{milliseconds:F0} ms";
        var seconds = milliseconds / 1000;
        if (seconds < 60) return $"{seconds:F2} s";
        var minutes = (int)(seconds / 60);
        if (minutes < 60) return $"{minutes}m {seconds % 60:F1}s";
        return $"{minutes / 60}h {minutes % 60}m";
    }

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>%%PAGE_TITLE%%</title>
  <style>
    :root { color-scheme: light; --page:#f3f5f7; --surface:#fff; --line:#d7dde3; --text:#17202a; --muted:#5f6b76; --pass:#147d4a; --fail:#b42318; --warn:#8a5a00; --focus:#1769aa; --code:#111820; --code-text:#e8edf2; }
    * { box-sizing:border-box; } body { margin:0; background:var(--page); color:var(--text); font-family:"Segoe UI",Tahoma,sans-serif; font-size:14px; line-height:1.45; }
    main { width:min(1500px,calc(100% - 32px)); margin:24px auto 48px; } header { display:flex; justify-content:space-between; gap:24px; margin-bottom:18px; }
    h1 { margin:0 0 4px; font-size:24px; } h2 { margin:0; font-size:17px; } h3 { margin:0 0 2px; font-size:14px; } h5 { margin:14px 0 5px; font-size:12px; } p { margin:4px 0; } .muted { color:var(--muted); }
    .status { border:1px solid; border-radius:6px; padding:7px 12px; font-weight:700; } .status.passed { color:var(--pass); background:#e7f5ed; } .status.failed { color:var(--fail); background:#fdecea; }
    .summary-grid { display:grid; grid-template-columns:repeat(4,minmax(120px,1fr)); gap:10px; margin-bottom:14px; } .metric { background:var(--surface); border:1px solid var(--line); border-radius:6px; padding:12px; } .metric strong { display:block; font-size:22px; } .metric span { color:var(--muted); font-size:12px; }
    .controls { display:flex; align-items:center; gap:10px; flex-wrap:wrap; background:var(--surface); border:1px solid var(--line); border-radius:6px; padding:10px; margin-bottom:10px; }
    .controls input,.controls select { height:34px; border:1px solid #b9c2cb; border-radius:4px; background:#fff; padding:0 10px; font:inherit; } .controls input { flex:1 1 320px; min-width:180px; } .controls select { flex:0 1 300px; min-width:170px; }
    .filter-group { display:flex; gap:4px; } .filter-button { height:34px; border:1px solid #b9c2cb; border-radius:4px; background:#fff; padding:0 10px; cursor:pointer; } .filter-button.active { color:#fff; background:#34495e; } .visible-count { margin-left:auto; color:var(--muted); font-size:12px; }
    .test-list { background:var(--surface); border:1px solid var(--line); border-radius:6px; overflow:hidden; } .test-list-header,div.test-row,.test-row>summary { display:grid; grid-template-columns:100px minmax(0,1fr) 90px 58px; align-items:center; column-gap:12px; }
    .test-list-header { min-height:34px; padding:0 12px; background:#eef2f5; color:var(--muted); font-size:11px; font-weight:700; text-transform:uppercase; }
    .domain-group+.domain-group { border-top:2px solid #cbd4dc; } .domain-header { display:flex; justify-content:space-between; gap:16px; padding:12px 14px; background:#dfe6ec; } .domain-header span,.feature-header>span { color:var(--muted); font-size:12px; white-space:nowrap; }
    .feature-group+.feature-group { border-top:1px solid #cfd7de; } .feature-header { display:flex; justify-content:space-between; gap:18px; padding:10px 14px; background:#f4f7f9; border-top:1px solid #cfd7de; } .feature-header p { margin:0; color:var(--muted); font-size:12px; }
    .test-row { min-height:52px; border-top:1px solid #e4e8ec; } div.test-row,.test-row>summary { padding:7px 12px; } .test-row>summary { min-height:52px; cursor:pointer; list-style:none; } .test-row>summary::-webkit-details-marker { display:none; } .test-row.failed { border-left:4px solid var(--fail); } .test-row[hidden],.feature-group[hidden],.domain-group[hidden] { display:none; }
    .test-status { display:flex; align-items:center; gap:7px; font-size:12px; font-weight:700; } .status-dot { width:9px; height:9px; border-radius:50%; flex:0 0 9px; background:#53606d; } .status-dot.passed { background:var(--pass); } .status-dot.failed { background:var(--fail); } .status-dot.inconclusive { background:var(--warn); }
    .test-summary { display:flex; flex-direction:column; min-width:0; gap:2px; } .test-title { font-weight:650; overflow-wrap:anywhere; } .test-description,.duration,.detail-label { color:var(--muted); font-size:12px; overflow-wrap:anywhere; } .duration,.detail-label { text-align:right; } .duration { white-space:nowrap; } .detail-label { color:var(--focus); }
    .test-body { grid-column:1/-1; min-width:0; overflow:hidden; border-top:1px solid var(--line); background:#fafbfc; padding:10px 14px 14px; } .test-context { display:flex; flex-wrap:wrap; gap:18px; color:var(--muted); font-size:12px; margin-bottom:8px; } .test-context span { overflow-wrap:anywhere; }
    .diagnostics { margin-top:22px; background:var(--surface); border:1px solid var(--line); border-radius:6px; overflow:hidden; } .diagnostics>summary { cursor:pointer; padding:11px 13px; font-weight:700; } .diagnostics-list { border-top:1px solid var(--line); padding:8px; }
    .diagnostic-item { border:1px solid var(--line); border-radius:5px; margin:6px 0; overflow:hidden; } .diagnostic-item>summary { cursor:pointer; display:flex; align-items:center; gap:9px; padding:9px 11px; } .execution-name { font-weight:700; } .counts { flex:1; color:var(--muted); font-size:12px; } .exit-code { color:var(--muted); font-size:12px; } .diagnostic-body { border-top:1px solid var(--line); padding:11px 13px 14px; }
    dl { display:grid; grid-template-columns:repeat(4,minmax(140px,1fr)); gap:8px 18px; } dt { color:var(--muted); font-size:11px; text-transform:uppercase; } dd { margin:2px 0 0; overflow-wrap:anywhere; } pre { margin:0; background:var(--code); color:var(--code-text); border-radius:5px; padding:10px 12px; overflow:auto; white-space:pre-wrap; font-family:Consolas,monospace; font-size:12px; }
    footer { margin-top:18px; color:var(--muted); font-size:12px; } @media(max-width:720px) { header { flex-direction:column; } .summary-grid { grid-template-columns:repeat(2,1fr); } .test-list-header,div.test-row,.test-row>summary { grid-template-columns:78px minmax(0,1fr) 60px; gap:8px; } .detail-label,.test-list-header>span:last-child { display:none; } dl { grid-template-columns:1fr; } }
  </style>
</head>
<body><main>
  <header><div><h1>Regression Results</h1><p><strong>Master execution:</strong> %%MASTER_RUN_ID%%</p><p class="muted">%%EXECUTION_SUMMARY%%</p></div><div class="status %%OVERALL_CLASS%%">%%OVERALL_STATUS%%</div></header>
  <section class="summary-grid"><div class="metric"><strong>%%TOTAL%%</strong><span>Tests</span></div><div class="metric"><strong>%%PASSED%%</strong><span>Passed</span></div><div class="metric"><strong>%%FAILED%%</strong><span>Failed</span></div><div class="metric"><strong>%%OTHER%%</strong><span>Skipped / Other</span></div></section>
  <section class="controls"><input id="test-search" type="search" placeholder="Filter by feature, value, scenario, or test name"><div class="filter-group"><button class="filter-button active" data-filter="all">All %%TOTAL%%</button><button class="filter-button" data-filter="failed">Failed %%FAILED%%</button><button class="filter-button" data-filter="passed">Passed %%PASSED%%</button><button class="filter-button" data-filter="other">Other %%OTHER%%</button></div><select id="feature-filter"><option value="">All features</option>%%FEATURE_OPTIONS%%</select><span id="visible-count" class="visible-count">Showing %%TOTAL%% tests</span></section>
  <section class="test-list"><div class="test-list-header"><span>Status</span><span>Scenario and why it matters</span><span>Duration</span><span></span></div>%%HIERARCHY%%<p id="no-results" class="muted" hidden>No tests match the current filters.</p></section>
  <details class="diagnostics"><summary>Run diagnostics (%%EXECUTION_COUNT%% executions)</summary><div class="diagnostics-list">%%DIAGNOSTICS%%</div></details>
  <footer>Generated from MSTest TRX results. Refresh after another execution appends to this master run.</footer>
</main>
<script>
(() => { const rows=[...document.querySelectorAll('.test-row')], search=document.getElementById('test-search'), feature=document.getElementById('feature-filter'), count=document.getElementById('visible-count'), empty=document.getElementById('no-results'), buttons=[...document.querySelectorAll('.filter-button')], groups=[...document.querySelectorAll('.feature-group')], domains=[...document.querySelectorAll('.domain-group')]; let status='all'; function apply(){ const query=search.value.trim().toLowerCase(), selected=feature.value; let visible=0; for(const row of rows){ const state=row.dataset.status; const statusMatch=status==='all'||state===status||(status==='other'&&state!=='passed'&&state!=='failed'); row.hidden=!(statusMatch&&(!query||row.dataset.search.includes(query))&&(!selected||row.dataset.feature===selected)); if(!row.hidden)visible++; } for(const group of groups)group.hidden=![...group.querySelectorAll('.test-row')].some(row=>!row.hidden); for(const domain of domains)domain.hidden=![...domain.querySelectorAll('.feature-group')].some(group=>!group.hidden); count.textContent=`Showing ${visible} of ${rows.length} tests`; empty.hidden=visible!==0; } for(const button of buttons)button.addEventListener('click',()=>{ status=button.dataset.filter; for(const item of buttons)item.classList.toggle('active',item===button); apply(); }); search.addEventListener('input',apply); feature.addEventListener('change',apply); })();
</script></body></html>
""";
}
