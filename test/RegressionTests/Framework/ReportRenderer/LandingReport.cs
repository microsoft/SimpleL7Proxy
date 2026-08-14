using System.Globalization;
using System.Net;
using System.Text.Json;

namespace RegressionReportRenderer;

internal static class LandingReport
{
    public static string Render(IReadOnlyList<HistoryEntry> entries, IReadOnlyList<string> domains)
    {
        var latest = entries.FirstOrDefault();
        var latestMarkup = latest == null
            ? "<p class=\"empty\">No regression runs have been recorded.</p>"
            : RenderLatest(latest, domains.Count);
        var historyRows = entries.Count == 0
            ? string.Empty
            : string.Concat(entries.Select(entry => RenderHistoryRow(entry, domains)));
        var purposeOptions = string.Concat(entries
            .SelectMany(entry => entry.Manifest.Executions)
            .Select(execution => execution.Label)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => $"<option value=\"{Encode(value)}\">{Encode(value)}</option>"));

        return Template
            .Replace("%%LATEST%%", latestMarkup, StringComparison.Ordinal)
            .Replace("%%HISTORY%%", historyRows, StringComparison.Ordinal)
            .Replace("%%PURPOSE_OPTIONS%%", purposeOptions, StringComparison.Ordinal)
            .Replace("%%RUN_COUNT%%", entries.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%UPDATED%%", WebUtility.HtmlEncode(DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture)), StringComparison.Ordinal);
    }

    private static string RenderLatest(HistoryEntry entry, int domainCount)
    {
        var summary = Summarize(entry.Manifest);
        var state = summary.Failed > 0 ? "failed" : "passed";
        var labels = entry.Manifest.Executions.Select(execution => execution.Label)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
        var contents = labels.Count == 0 ? "Unlabeled regression run" : string.Join(", ", labels);
        var parsed = DateTimeOffset.TryParseExact(entry.FolderName, "yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTimestamp);
        var dateHeading = parsed
            ? parsedTimestamp.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)
            : entry.FolderName;
        var timeLabel = parsed
            ? parsedTimestamp.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : entry.FolderName;
        var dateTime = parsed
            ? parsedTimestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : string.Empty;
        var exercisedDomains = entry.Manifest.Executions.SelectMany(execution => execution.Tests)
            .Select(test => test.Domain).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Count();
        var statusText = state == "failed" ? "Failed" : "Passed";
        var resultValue = state == "failed"
            ? summary.Failed.ToString(CultureInfo.InvariantCulture)
            : $"{summary.Passed}<span>/{summary.Total}</span>";
        var resultCaption = state == "failed"
            ? summary.Failed == 1 ? "issue to review" : "issues to review"
            : "tests passed";
        return $"<a class=\"latest {state}\" href=\"{Encode(entry.ReportPath)}\" aria-label=\"Open latest report from {Encode(dateHeading)}: {Encode(contents)}\">" +
            $"<div class=\"latest-copy\"><div class=\"latest-kicker\"><span class=\"status-badge {state}\"><span class=\"status-dot\"></span>{statusText}</span><span>Latest execution</span></div>" +
            $"<h2><time datetime=\"{dateTime}\">{Encode(dateHeading)}</time></h2><p class=\"latest-time\">{Encode(timeLabel)} &middot; {Encode(contents)}</p>" +
            $"<p class=\"coverage\">{exercisedDomains} of {domainCount} domains exercised &middot; {Encode(FeatureList(summary.Features))}</p></div>" +
            $"<div class=\"latest-result\"><strong>{resultValue}</strong><small>{resultCaption}</small><span class=\"open\">View report <span aria-hidden=\"true\">&#8594;</span></span></div></a>";
    }

    private static string RenderHistoryRow(HistoryEntry entry, IReadOnlyList<string> domains)
    {
        var summary = Summarize(entry.Manifest);
        var state = summary.Failed > 0 ? "failed" : "passed";
        var labels = entry.Manifest.Executions.Select(execution => execution.Label)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
        var contents = labels.Count == 0 ? "Unlabeled regression run" : string.Join(", ", labels);
        var parsed = DateTimeOffset.TryParseExact(entry.FolderName, "yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTimestamp);
        var dateHeading = parsed
            ? parsedTimestamp.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)
            : entry.FolderName;
        var timeLabel = parsed
            ? parsedTimestamp.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : entry.FolderName;
        var dateTime = parsed
            ? parsedTimestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : string.Empty;
        var date = !parsed
            ? string.Empty
            : parsedTimestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var statusText = state == "failed" ? "Failed" : "Passed";
        var resultValue = state == "failed"
            ? summary.Failed.ToString(CultureInfo.InvariantCulture)
            : $"{summary.Passed}/{summary.Total}";
        var resultCaption = state == "failed"
            ? summary.Failed == 1 ? "issue to review" : "issues to review"
            : "tests passed";
        var allTests = entry.Manifest.Executions.SelectMany(execution => execution.Tests).ToList();
        var executedDomains = allTests.Select(test => test.Domain).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).ToList();
        var searchText = string.Join(" ", labels.Concat(summary.Features).Concat(executedDomains).Append(entry.FolderName));
        var purposes = JsonSerializer.Serialize(labels);
        var children = string.Concat(entry.Manifest.Executions.Select(RenderChildRun));
        var domainMarkup = string.Concat(domains.Select(domain =>
        {
            var tests = allTests.Where(test => string.Equals(test.Domain, domain, StringComparison.Ordinal))
                .OrderBy(test => test.Feature, StringComparer.Ordinal)
                .ThenBy(test => test.Title, StringComparer.Ordinal)
                .ToList();
            var passed = tests.Count(test => test.Outcome == "Passed");
            var failed = tests.Count(test => test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted");
            var other = tests.Count - passed - failed;
            var domainState = tests.Count == 0 ? "not-run" : failed > 0 ? "failed" : other > 0 ? "other" : "passed";
            var domainStatus = domainState switch
            {
                "passed" => "Passed",
                "failed" => "Failed",
                "other" => "Other",
                _ => "Not run"
            };
            var domainCounts = domainState switch
            {
                "passed" => $"{passed}/{tests.Count} passed",
                "failed" => $"{failed} failed &middot; {passed} passed{(other > 0 ? $" &middot; {other} other" : string.Empty)}",
                "other" => $"{other} other &middot; {passed} passed",
                _ => "No tests recorded"
            };
            if (tests.Count == 0)
            {
                return $"<div class=\"domain-card not-run\"><span class=\"domain-name\"><span class=\"status-badge not-run\"><span class=\"status-dot\"></span>{domainStatus}</span><strong>{Encode(domain)}</strong></span><span class=\"domain-counts\">{domainCounts}</span><span></span></div>";
            }

            var testMarkup = string.Concat(tests.Select(test =>
            {
                var testState = test.Outcome switch
                {
                    "Passed" => "passed",
                    "Failed" or "Error" or "Timeout" or "Aborted" => "failed",
                    _ => "other"
                };
                var title = string.IsNullOrWhiteSpace(test.Title) ? test.Name : test.Title;
                return $"<div class=\"domain-test\"><span class=\"status-badge {testState}\"><span class=\"status-dot\"></span>{Encode(test.Outcome)}</span>" +
                    $"<div><strong>{Encode(title)}</strong><p>{Encode(test.Feature)}</p></div></div>";
            }));
            var open = domainState is "failed" or "other" ? " open" : string.Empty;
            return $"<details class=\"domain-card {domainState}\"{open}><summary><span class=\"domain-name\"><span class=\"status-badge {domainState}\"><span class=\"status-dot\"></span>{domainStatus}</span>" +
                $"<strong>{Encode(domain)}</strong><span class=\"expanded-label\">Expanded</span></span><span class=\"domain-counts\">{domainCounts}</span><span class=\"domain-chevron\" aria-hidden=\"true\">&#8250;</span></summary>" +
                $"<div class=\"domain-tests\">{testMarkup}</div></details>";
        }));
        var executionIssues = entry.Manifest.Executions
            .Where(execution => (execution.ExitCode != 0 || !string.IsNullOrEmpty(execution.ParseError)) &&
                !execution.Tests.Any(test => test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted"))
            .Select(execution =>
            {
                var reason = !string.IsNullOrEmpty(execution.ParseError)
                    ? execution.ParseError
                    : $"exit code {execution.ExitCode}";
                return $"<li><strong>{Encode(execution.Label)}</strong>: {Encode(reason)}; no failed domain test result was recorded.</li>";
            })
            .ToList();
        var executionIssueMarkup = executionIssues.Count == 0
            ? string.Empty
            : $"<div class=\"execution-issues\"><strong>Execution issues</strong><ul>{string.Concat(executionIssues)}</ul></div>";
        return $"<details class=\"history-entry\" data-state=\"{state}\" data-search=\"{Encode(searchText)}\" data-purposes=\"{Encode(purposes)}\" data-date=\"{date}\">" +
            $"<summary class=\"history-row\"><span class=\"timeline-marker {state}\"><span></span></span>" +
            $"<div class=\"run-identity\"><div class=\"run-meta\"><span class=\"status-badge {state}\"><span class=\"status-dot\"></span>{statusText}</span><span>{Encode(timeLabel)}</span></div>" +
            $"<h3><time datetime=\"{dateTime}\">{Encode(dateHeading)}</time></h3><p class=\"run-purpose\">{Encode(contents)}</p></div>" +
            $"<div class=\"run-results\"><strong>{resultValue}</strong><span>{resultCaption}</span></div>" +
            $"<span class=\"disclosure\" aria-hidden=\"true\">&#8250;</span></summary>" +
            $"<div class=\"history-details\"><div class=\"detail-heading\"><div><span class=\"detail-label\">Run scope</span><p>{Encode(contents)}</p></div>" +
            $"<a class=\"report-action\" href=\"{Encode(entry.ReportPath)}\">Open report <span aria-hidden=\"true\">&#8594;</span></a></div>" +
            $"{executionIssueMarkup}<div class=\"domain-heading\"><h4>Domain status</h4><span>{domains.Count} domains</span></div><div class=\"domain-list\">{domainMarkup}</div>" +
            $"<details class=\"execution-breakdown\"><summary>Execution details ({RunCount(entry.Manifest.Executions.Count, "run")})</summary><div class=\"child-list\">{children}</div></details></div></details>";
    }

    private static string RenderChildRun(ExecutionRecord execution)
    {
        var features = execution.Tests.Select(test => test.Feature).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        var failed = execution.Tests.Count(test => test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted");
        var state = failed > 0 || execution.ExitCode != 0 || !string.IsNullOrEmpty(execution.ParseError) ? "failed" : "passed";
        var statusText = state == "failed" ? "Failed" : "Passed";
        var issueCount = Math.Max(failed, 1);
        var resultValue = state == "failed"
            ? issueCount.ToString(CultureInfo.InvariantCulture)
            : $"{execution.Summary.Passed}/{execution.Tests.Count}";
        var resultCaption = state == "failed"
            ? issueCount == 1 ? "issue to review" : "issues to review"
            : "tests passed";
        return $"<div class=\"child-row\"><span class=\"status-badge {state}\"><span class=\"status-dot\"></span>{statusText}</span>" +
            $"<div><strong>{Encode(execution.Label)}</strong><p>{Encode(FeatureList(features))}</p></div>" +
            $"<span class=\"child-result\"><strong>{resultValue}</strong> {resultCaption}</span></div>";
    }

    private static LandingSummary Summarize(ReportManifest manifest)
    {
        var tests = manifest.Executions.SelectMany(execution => execution.Tests).ToList();
        var failed = manifest.Executions.Sum(execution =>
        {
            var failedTests = execution.Tests.Count(test => test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted");
            return failedTests > 0
                ? failedTests
                : execution.ExitCode != 0 || !string.IsNullOrEmpty(execution.ParseError) ? 1 : 0;
        });
        var passed = tests.Count(test => test.Outcome == "Passed");
        var features = tests.Select(test => test.Feature).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        return new LandingSummary(tests.Count, passed, failed, tests.Count - passed - tests.Count(test =>
            test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted"), features);
    }

    private static string FeatureList(IReadOnlyCollection<string> features)
        => features.Count == 0 ? "Legacy run without feature metadata" : string.Join(", ", features);

    private static string RunCount(int count, string noun)
        => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Regression test runs</title>
    <style>
        :root { color-scheme:light; --page:#f5f7f8; --surface:#ffffff; --surface-soft:#eef2f4; --line:#d8dee3; --line-strong:#bdc7cf; --text:#151b1f; --muted:#5d6871; --quiet:#7c8790; --accent:#0869b8; --accent-hover:#075898; --pass:#16784a; --pass-soft:#e6f4ec; --fail:#b42318; --fail-soft:#fcebea; --other:#8a5a00; --other-soft:#fff4d6; --not-run:#68747d; --not-run-soft:#edf1f3; --focus:#1473e6; }
        * { box-sizing:border-box; } html { background:var(--page); } body { margin:0; min-width:320px; background:var(--page); color:var(--text); font-family:"Aptos","Segoe UI Variable",sans-serif; font-size:15px; line-height:1.45; }
        button,input,select { font:inherit; letter-spacing:0; } button,summary,a,input,select { -webkit-tap-highlight-color:transparent; } [hidden] { display:none !important; }
        main { width:min(1160px,calc(100% - 40px)); margin:0 auto 64px; } .masthead { display:flex; align-items:flex-start; justify-content:space-between; gap:24px; padding:42px 0 24px; }
        .eyebrow { display:block; margin-bottom:6px; color:var(--accent); font-size:12px; font-weight:700; text-transform:uppercase; } h1,h2,h3,h4,p { margin:0; } h1 { font-family:"Aptos Display","Segoe UI Variable Display",sans-serif; font-size:38px; font-weight:650; line-height:1.08; letter-spacing:0; } .subtitle { max-width:720px; margin-top:9px; color:var(--muted); font-size:16px; }
        .help { position:relative; z-index:3; } .help>summary { display:grid; width:34px; height:34px; place-items:center; border:1px solid var(--line-strong); border-radius:50%; background:var(--surface); color:var(--muted); cursor:pointer; font-weight:750; list-style:none; } .help>summary::-webkit-details-marker { display:none; } .help>summary:hover { color:var(--text); border-color:var(--quiet); } .help-panel { position:absolute; top:44px; right:0; width:320px; padding:16px; background:var(--surface); border:1px solid var(--line); border-radius:8px; box-shadow:0 16px 44px rgba(27,39,47,.14); } .help-panel h2 { font-size:16px; } .help-panel dl { margin:12px 0 0; } .help-panel dt { margin-top:10px; font-weight:700; } .help-panel dd { margin:2px 0 0; color:var(--muted); font-size:13px; }
        .latest { display:grid; grid-template-columns:minmax(0,1fr) 180px; align-items:stretch; min-height:210px; color:inherit; text-decoration:none; background:var(--surface); border:1px solid var(--line); border-radius:8px; overflow:hidden; box-shadow:0 1px 1px rgba(20,31,38,.03); } .latest:hover { border-color:var(--line-strong); box-shadow:0 12px 32px rgba(24,39,49,.08); } .latest-copy { min-width:0; padding:30px 32px; } .latest-kicker { display:flex; align-items:center; gap:12px; color:var(--muted); font-size:13px; font-weight:650; } .latest h2 { max-width:780px; margin-top:18px; font-family:"Aptos Display","Segoe UI Variable Display",sans-serif; font-size:30px; font-weight:650; line-height:1.18; overflow-wrap:anywhere; } .latest-time { margin-top:10px; color:var(--muted); } .coverage { margin-top:5px; color:var(--quiet); font-size:13px; }
        .latest-result { display:flex; min-width:180px; padding:28px; align-items:flex-start; flex-direction:column; justify-content:center; background:var(--surface-soft); border-left:1px solid var(--line); } .latest.passed .latest-result { background:var(--pass-soft); } .latest.failed .latest-result { background:var(--fail-soft); } .latest-result>strong { font-size:34px; font-weight:700; line-height:1; } .latest-result>strong span { color:var(--muted); font-size:20px; } .latest-result small { margin-top:6px; color:var(--muted); } .open { margin-top:auto; color:var(--accent); font-weight:700; white-space:nowrap; }
        .status-badge { display:inline-flex; width:max-content; min-height:24px; padding:2px 8px; align-items:center; gap:6px; border-radius:999px; font-size:12px; font-weight:750; line-height:1; } .status-badge.passed { background:var(--pass-soft); color:var(--pass); } .status-badge.failed { background:var(--fail-soft); color:var(--fail); } .status-badge.other { background:var(--other-soft); color:var(--other); } .status-badge.not-run { background:var(--not-run-soft); color:var(--not-run); } .status-dot { width:7px; height:7px; flex:0 0 7px; border-radius:50%; background:currentColor; }
        .history { margin-top:44px; } .history-heading { display:flex; align-items:flex-end; justify-content:space-between; gap:20px; padding-bottom:14px; border-bottom:1px solid var(--line-strong); } .history-heading h2 { font-family:"Aptos Display","Segoe UI Variable Display",sans-serif; font-size:25px; font-weight:650; } .history-heading p { margin-top:3px; color:var(--muted); font-size:13px; } #visible-count { color:var(--muted); font-size:13px; white-space:nowrap; }
        .filters { display:grid; grid-template-columns:minmax(220px,1.5fr) minmax(180px,1fr) auto auto auto; gap:12px; padding:18px 0; align-items:end; border-bottom:1px solid var(--line); } .field { display:flex; min-width:0; flex-direction:column; gap:5px; color:var(--muted); font-size:12px; font-weight:650; } .field input,.field select { width:100%; height:42px; padding:0 12px; color:var(--text); background:var(--surface); border:1px solid var(--line-strong); border-radius:7px; outline:none; } .field input:focus,.field select:focus { border-color:var(--focus); box-shadow:0 0 0 3px rgba(20,115,230,.13); } .field input::placeholder { color:var(--quiet); } .date-field { width:150px; }
        .failure-toggle { display:flex; height:42px; padding:0 12px; align-items:center; gap:9px; color:var(--text); background:var(--surface); border:1px solid var(--line-strong); border-radius:7px; cursor:pointer; white-space:nowrap; } .failure-toggle input { position:absolute; width:1px; height:1px; opacity:0; } .toggle-track { position:relative; width:30px; height:18px; flex:0 0 30px; border-radius:999px; background:#a5afb7; } .toggle-track::after { position:absolute; top:3px; left:3px; width:12px; height:12px; border-radius:50%; background:#fff; content:""; transition:transform .16s ease; } .failure-toggle input:checked+.toggle-track { background:var(--fail); } .failure-toggle input:checked+.toggle-track::after { transform:translateX(12px); } .failure-toggle:has(input:focus-visible) { outline:3px solid rgba(20,115,230,.2); outline-offset:2px; }
        .clear-button { height:42px; padding:0 4px; color:var(--accent); background:transparent; border:0; cursor:pointer; font-weight:700; } .clear-button:hover { color:var(--accent-hover); text-decoration:underline; }
        .history-list { position:relative; } .history-list::before { position:absolute; top:0; bottom:0; left:19px; width:1px; background:var(--line); content:""; } .history-entry { position:relative; border-bottom:1px solid var(--line); } .history-entry>summary { list-style:none; cursor:pointer; } .history-entry>summary::-webkit-details-marker { display:none; } .history-entry[open] { background:var(--surface); } .history-entry[open] .disclosure { transform:rotate(90deg); }
        .history-row { display:grid; grid-template-columns:40px minmax(0,1fr) 110px 24px; min-height:96px; padding:19px 8px 19px 0; align-items:center; gap:16px; } .history-row:hover { background:rgba(255,255,255,.55); } .timeline-marker { position:relative; z-index:1; display:grid; width:40px; height:40px; place-items:center; } .timeline-marker>span { width:11px; height:11px; border:3px solid var(--page); border-radius:50%; background:var(--pass); box-shadow:0 0 0 1px var(--pass); } .timeline-marker.failed>span { background:var(--fail); box-shadow:0 0 0 1px var(--fail); }
        .run-identity { min-width:0; } .run-meta { display:flex; align-items:center; gap:10px; } .run-meta>span:last-child { color:var(--muted); font-size:12px; } .run-identity h3 { margin-top:8px; font-size:18px; font-weight:680; line-height:1.25; overflow-wrap:anywhere; } .run-purpose { margin-top:3px; color:var(--muted); font-size:13px; overflow-wrap:anywhere; } .run-results { display:flex; flex-direction:column; align-items:flex-end; } .run-results strong { font-size:17px; } .run-results span { color:var(--muted); font-size:12px; } .disclosure { color:var(--muted); font-family:Georgia,serif; font-size:27px; line-height:1; transition:transform .16s ease; }
        .history-details { margin-left:56px; padding:0 48px 22px 20px; border-left:2px solid #b8d9c8; } .history-entry[data-state="failed"]>.history-details { border-left-color:#e4b3af; } .detail-heading { display:flex; justify-content:space-between; align-items:flex-start; gap:24px; padding:18px 0; border-top:1px solid var(--line); } .detail-label { color:var(--muted); font-size:11px; font-weight:750; text-transform:uppercase; } .detail-heading p { margin-top:3px; color:var(--text); } .report-action { display:inline-flex; min-height:38px; padding:0 13px; align-items:center; gap:8px; color:#fff; background:var(--accent); border-radius:7px; text-decoration:none; font-weight:700; white-space:nowrap; } .report-action:hover { background:var(--accent-hover); }
        .execution-issues { margin:0 0 18px; padding:13px 15px; color:#6f4700; background:var(--other-soft); border:1px solid #e8cf8b; border-radius:7px; } .execution-issues ul { margin:6px 0 0; padding-left:20px; } .execution-issues li+li { margin-top:4px; }
        .domain-heading { display:flex; align-items:center; justify-content:space-between; gap:16px; margin:4px 0 10px; } .domain-heading h4 { font-size:16px; } .domain-heading>span { color:var(--muted); font-size:12px; } .domain-list { display:grid; gap:9px; }
        .domain-card { background:var(--surface); border:1px solid var(--line); border-left:4px solid var(--pass); border-radius:7px; overflow:hidden; } .domain-card.failed { border-left-color:var(--fail); } .domain-card.other { border-left-color:var(--other); } .domain-card.not-run { border-left-color:#9aa5ad; background:#fafbfc; } .domain-card[open] { position:relative; overflow:visible; background:transparent; border:0; border-radius:0; box-shadow:none; } .domain-card[open]::after { position:absolute; top:58px; left:13px; width:15px; height:17px; border-bottom:2px solid #9bcbb1; border-left:2px solid #9bcbb1; border-bottom-left-radius:7px; content:""; } .domain-card.failed[open]::after { border-color:#e5aaa5; } .domain-card.other[open]::after { border-color:#dfc675; }
        .domain-card>summary,.domain-card.not-run { display:grid; grid-template-columns:minmax(0,1fr) auto 28px; min-height:58px; padding:10px 12px; align-items:center; gap:12px; list-style:none; } .domain-card>summary { cursor:pointer; } .domain-card>summary::-webkit-details-marker { display:none; } .domain-card>summary:hover { background:#fafbfc; } .domain-card[open]>summary { background:#edf8f2; border:1px solid #9bcbb1; border-left:4px solid var(--pass); border-radius:7px; box-shadow:0 0 0 1px rgba(22,120,74,.1),0 5px 14px rgba(26,48,37,.06); } .domain-card.failed[open]>summary { background:var(--fail-soft); border-color:#e5aaa5; border-left-color:var(--fail); } .domain-card.other[open]>summary { background:var(--other-soft); border-color:#dfc675; border-left-color:var(--other); } .domain-card[open] .domain-chevron { color:var(--pass); background:#d9eee2; transform:rotate(90deg); } .domain-card.failed[open] .domain-chevron { color:var(--fail); background:#f7d9d6; } .domain-card.other[open] .domain-chevron { color:var(--other); background:#f3e5b4; }
        .domain-name { display:flex; min-width:0; align-items:center; flex-wrap:wrap; gap:8px 10px; } .domain-name>strong { overflow-wrap:anywhere; } .expanded-label { display:none; padding:3px 7px; color:var(--pass); background:#d9eee2; border-radius:999px; font-size:11px; font-weight:750; line-height:1; } .domain-card[open] .expanded-label { display:inline-flex; } .domain-card.failed[open] .expanded-label { color:var(--fail); background:#f7d9d6; } .domain-card.other[open] .expanded-label { color:var(--other); background:#f3e5b4; } .domain-counts { color:var(--muted); font-size:12px; text-align:right; white-space:nowrap; } .domain-chevron { display:grid; width:28px; height:28px; place-items:center; color:var(--muted); background:var(--not-run-soft); border-radius:50%; font-family:Georgia,serif; font-size:24px; line-height:1; transition:transform .16s ease; }
        .domain-tests { margin:8px 0 0 28px; overflow:hidden; background:#fbfcfc; border:1px solid var(--line); border-left:4px solid var(--pass); border-radius:7px; } .domain-card.failed .domain-tests { border-left-color:var(--fail); } .domain-card.other .domain-tests { border-left-color:var(--other); } .domain-test { display:grid; grid-template-columns:86px minmax(0,1fr); align-items:center; gap:12px; min-height:58px; padding:9px 14px; } .domain-test+.domain-test { border-top:1px solid #e8ecef; } .domain-test>div { min-width:0; } .domain-test strong { overflow-wrap:anywhere; } .domain-test p { margin-top:2px; color:var(--muted); font-size:12px; }
        .execution-breakdown { margin-top:18px; border-top:1px solid var(--line); } .execution-breakdown>summary { padding:13px 0; color:var(--muted); cursor:pointer; font-size:13px; font-weight:700; list-style:none; } .execution-breakdown>summary::-webkit-details-marker { display:none; } .execution-breakdown>summary::after { margin-left:7px; content:"+"; } .execution-breakdown[open]>summary::after { content:"-"; } .child-list { border-top:1px solid var(--line); } .child-row { display:grid; grid-template-columns:86px minmax(0,1fr) 110px; align-items:center; gap:14px; padding:12px 0; } .child-row+.child-row { border-top:1px solid #e4e8ec; } .child-row>div { min-width:0; } .child-row>div>strong { overflow-wrap:anywhere; } .child-row p { margin-top:2px; color:var(--muted); font-size:12px; } .child-result { color:var(--muted); font-size:12px; text-align:right; } .child-result strong { color:var(--text); }
        .no-results,.empty { padding:36px 12px; color:var(--muted); text-align:center; } footer { display:flex; justify-content:space-between; gap:20px; margin-top:22px; color:var(--quiet); font-size:12px; }
        summary:focus-visible,a:focus-visible,button:focus-visible { outline:3px solid rgba(20,115,230,.25); outline-offset:3px; }
        @media(max-width:900px) { .filters { grid-template-columns:1fr 1fr 150px 150px; } .failure-toggle { grid-column:1/2; width:max-content; } .clear-button { justify-self:start; } }
        @media(max-width:680px) { main { width:min(100% - 28px,1160px); margin-bottom:40px; } .masthead { padding-top:28px; } h1 { font-size:31px; } .subtitle { font-size:14px; } .latest { grid-template-columns:1fr; min-height:0; } .latest-copy { padding:24px 22px; } .latest h2 { font-size:24px; } .latest-result { min-width:0; padding:18px 22px; border-top:1px solid var(--line); border-left:0; flex-flow:row wrap; align-items:center; justify-content:flex-start; gap:6px 10px; } .latest-result>strong { font-size:25px; } .latest-result>strong span { font-size:16px; } .latest-result small { margin-top:0; } .latest-result .open { margin:0 0 0 auto; } .history { margin-top:34px; } .filters { grid-template-columns:1fr 1fr; } .search-field,.purpose-field { grid-column:1/-1; } .date-field { width:auto; } .history-row { grid-template-columns:36px minmax(0,1fr) 22px; min-height:128px; gap:10px; padding-right:4px; } .timeline-marker { width:36px; } .history-list::before { left:17px; } .run-results { grid-column:2; align-items:baseline; flex-flow:row; gap:6px; } .disclosure { grid-column:3; grid-row:1/3; } .history-details { margin-left:42px; padding:0 4px 20px 10px; } .detail-heading { align-items:flex-start; flex-direction:column; } .domain-card>summary,.domain-card.not-run { grid-template-columns:minmax(0,1fr) 20px; gap:8px; } .domain-counts { grid-column:1; text-align:left; white-space:normal; } .domain-card.not-run>span:last-child { display:none; } .domain-card[open]::after { left:9px; width:15px; } .domain-tests { margin-left:24px; } .domain-test { grid-template-columns:78px minmax(0,1fr); padding-inline:10px; } .child-row { grid-template-columns:80px minmax(0,1fr); } .child-result { grid-column:2; text-align:left; } footer { flex-direction:column; gap:3px; } }
        @media(max-width:440px) { .filters { grid-template-columns:1fr; } .search-field,.purpose-field { grid-column:auto; } .help-panel { width:min(320px,calc(100vw - 28px)); } .latest-result { align-items:flex-start; flex-direction:column; } .latest-result .open { margin:8px 0 0; } .date-field { width:100%; } }
        @media(prefers-reduced-motion:reduce) { .toggle-track::after,.disclosure { transition:none; } }
    </style>
</head>
<body><main>
    <header class="masthead"><div><span class="eyebrow">Quality assurance</span><h1>Regression test runs</h1><p class="subtitle">Reports by execution date, with status across all seven regression domains.</p></div>
        <details class="help"><summary aria-label="About regression results" title="About regression results">i</summary><div class="help-panel"><h2>About these results</h2><dl><dt>Report date</dt><dd>The UTC date and time when the master execution started.</dd><dt>Domain status</dt><dd>Passed, Failed, Other, or Not run. Not run means the report contains no test result for that domain.</dd><dt>Test count</dt><dd>The number executed; parameterized tests count once per data row.</dd></dl></div></details>
    </header>
    %%LATEST%%
    <section class="history" aria-labelledby="history-title"><div class="history-heading"><div><h2 id="history-title">Run history</h2><p>Newest execution first</p></div><span id="visible-count" aria-live="polite">%%RUN_COUNT%% runs</span></div>
        <div class="filters" role="search"><label class="field search-field">Search<input id="run-search" type="search" placeholder="Run purpose or feature"></label>
            <label class="field purpose-field">Purpose<select id="purpose-filter"><option value="">All purposes</option>%%PURPOSE_OPTIONS%%</select></label>
            <label class="field date-field">From<input id="date-from" type="date"></label><label class="field date-field">To<input id="date-to" type="date"></label>
            <label class="failure-toggle"><input id="failures-only" type="checkbox"><span class="toggle-track" aria-hidden="true"></span><span>Failures only</span></label><button class="clear-button" id="clear-filters" type="button" hidden>Clear filters</button>
        </div><div class="history-list">%%HISTORY%%<p class="no-results" id="no-results" hidden>No runs match these filters.</p></div>
    </section>
    <footer><span>Generated from MSTest TRX results</span><span>Updated %%UPDATED%%</span></footer>
</main>
<script>
(() => {
    const rows = [...document.querySelectorAll('.history-entry')];
    const search = document.getElementById('run-search');
    const purpose = document.getElementById('purpose-filter');
    const dateFrom = document.getElementById('date-from');
    const dateTo = document.getElementById('date-to');
    const failuresOnly = document.getElementById('failures-only');
    const clear = document.getElementById('clear-filters');
    const count = document.getElementById('visible-count');
    const empty = document.getElementById('no-results');
    function apply() {
        const query = search.value.trim().toLowerCase();
        const selectedPurpose = purpose.value;
        let visible = 0;
        for (const row of rows) {
            const purposes = JSON.parse(row.dataset.purposes || '[]');
            const matches = (!query || row.dataset.search.toLowerCase().includes(query)) &&
                (!selectedPurpose || purposes.includes(selectedPurpose)) &&
                (!dateFrom.value || row.dataset.date >= dateFrom.value) &&
                (!dateTo.value || row.dataset.date <= dateTo.value) &&
                (!failuresOnly.checked || row.dataset.state === 'failed');
            row.hidden = !matches;
            if (matches) visible++;
        }
        dateFrom.max = dateTo.value;
        dateTo.min = dateFrom.value;
        count.textContent = `${visible} of ${rows.length} runs`;
        empty.hidden = visible !== 0;
        clear.hidden = !(query || selectedPurpose || dateFrom.value || dateTo.value || failuresOnly.checked);
    }
    for (const control of [search, purpose, dateFrom, dateTo, failuresOnly]) {
        control.addEventListener(control === search ? 'input' : 'change', apply);
    }
    clear.addEventListener('click', () => {
        search.value = '';
        purpose.value = '';
        dateFrom.value = '';
        dateTo.value = '';
        failuresOnly.checked = false;
        apply();
        search.focus();
    });
    apply();
})();
</script></body></html>
""";
}
