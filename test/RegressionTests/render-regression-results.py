#!/usr/bin/env python3

import argparse
import csv
import contextlib
import datetime as dt
import html
import json
import os
from pathlib import Path
import re
import shutil
import time
import xml.etree.ElementTree as ET

TRX_NAMESPACE = {"trx": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
OUTCOME_ORDER = {
    "Failed": 0,
    "Error": 1,
    "Timeout": 2,
    "Aborted": 3,
    "Inconclusive": 4,
    "NotExecuted": 5,
    "Passed": 6,
}
KNOWN_TEST_TERMS = re.compile(
    r"S7P|APIM|OAuth|HTTP|JSON|DTO|TTL|API|URI|URL|CSV|RBAC|"
    r"[A-Z]+(?=[A-Z][a-z]|$)|[A-Z]?[a-z]+|[0-9]+"
)
TEST_TERM_NORMALIZATION = {
    "Api": "API",
    "Apim": "APIM",
    "Dto": "DTO",
    "Gpt": "GPT",
    "Http": "HTTP",
    "Json": "JSON",
    "Oauth": "OAuth",
    "Rbac": "RBAC",
    "S7p": "S7P",
    "Ttl": "TTL",
    "Uri": "URI",
    "Url": "URL",
}
TEST_HIERARCHY_RULES = [
    ("PolicyScenarioIntegrationTests", ("IteratorHeader_",),
        "Traffic Routing", "Round-robin load balancer",
        "This ensures requests rotate across configured backends while honoring per-request SinglePass and MultiPass retry behavior."),
    ("PolicyScenarioIntegrationTests", ("V31Policy_AllConfiguredScenarios",),
     "Traffic Routing", "APIM policy failover",
     "This confirms configured APIM failover paths return the intended result before policy changes reach production traffic."),
    ("PolicyScenarioIntegrationTests", ("V31Policy_Sustains",),
     "Reliability & Capacity", "APIM policy under load",
     "This confirms the APIM policy can sustain concurrent demand, throttling, and recovery without losing started requests."),
    ("PriorityLoadIntegrationTests", (),
     "Reliability & Capacity", "Priority processing under load",
     "This confirms high, medium, and low priority traffic remains serviceable and observable under concurrent load."),
    ("PolicyStressStateTests", (),
     "Reliability & Capacity", "Rate-limit simulation",
     "This keeps stress-test evidence trustworthy by enforcing isolated, concurrency-safe token budgets and time windows."),
    ("ModelRemapTests", ("ModelMap_",),
     "AI Request Compatibility", "Model family mapping",
     "This prevents requests from carrying fields that are unsupported by the backend model family selected for routing."),
    ("ModelRemapTests", ("Detect_",),
     "AI Request Compatibility", "Model detection",
     "This ensures the proxy identifies the requested model without changing valid payloads or trusting nested lookalike fields."),
    ("ModelRemapTests", ("Override_",),
     "AI Request Compatibility", "Model override rewriting",
     "This keeps rerouted requests valid when the proxy switches a request to a different model family."),
    ("RuleTests", ("ProcessFirst_NumericComparison",),
     "Rules & Configuration", "Numeric threshold routing",
     "This prevents percentage and threshold rules from sending traffic down the wrong route."),
    ("RuleTests", ("RuleCondition_StringOperators",),
     "Rules & Configuration", "String matching",
     "This keeps header- and metadata-based routing decisions consistent across casing and positive or negative operators."),
    ("RuleTests", ("RuleCondition_MissingField", "Process_MissingField", "ProcessFirst_HashField_MissingSource"),
     "Rules & Configuration", "Missing-data behavior",
     "This ensures incomplete request metadata follows a defined safe path instead of producing accidental matches."),
    ("RuleTests", ("ProcessFirst_Between", "Process_Between", "RuleCondition_Between"),
     "Rules & Configuration", "Range boundary routing",
     "This ensures rollout percentages and numeric bands include or exclude boundary values exactly as configured."),
    ("RuleTests", ("RuleHash_", "Process_Hash", "Process_S7PHash"),
     "Rules & Configuration", "Deterministic traffic bucketing",
     "This keeps the same request identity in a stable percentage bucket so gradual rollouts do not shift unpredictably."),
    ("RuleTests", ("Process_MatchedRuleNames", "Process_NestedRules"),
     "Rules & Configuration", "Rule composition and traceability",
     "This ensures complex rule trees select the intended branch and report a traceable path explaining the decision."),
    ("RuleTests", ("RuleConfig_", "ParseRules_", "ParseAndTryParse_", "Parse_Wrapped", "ConfigJson_", "RuleSample_"),
     "Rules & Configuration", "Rule configuration validation",
     "This rejects unsafe or malformed rule configuration before it can affect live request routing."),
    ("RuleTests", ("Regex_",),
     "Rules & Configuration", "Pattern matching",
     "This ensures pattern-based routing matches intended values consistently and rejects invalid patterns during configuration."),
    ("RuleTests", ("Stress_",),
     "Reliability & Capacity", "Rule engine concurrency",
     "This confirms rule evaluation remains stable when many requests are evaluated concurrently."),
    ("RuleTests", ("ProcessFirst_",),
     "Rules & Configuration", "First-match routing",
     "This ensures a request follows the first applicable branch and uses the configured fallback when no condition matches."),
    ("RuleTests", ("Process_",),
     "Rules & Configuration", "Multi-rule evaluation",
     "This ensures all applicable routing rules are evaluated in a predictable order without losing valid results."),
    ("Test1", ("ProfileEnricher_",),
     "Identity & Profiles", "Profile enrichment",
     "This ensures tenant identity, profile headers, and profile rules are applied consistently before routing and authorization."),
    ("Test1", ("RequestDataDto",),
     "Request Lifecycle", "Request state compatibility",
     "This preserves routing and telemetry state across persistence, restart, and previously stored request payloads."),
    ("QueueTests", ("QueuedProbe_", "ConcurrentProbe_", "OrdinaryWork_"),
     "Queueing & Scheduling", "Probe availability",
     "This keeps health probes responsive without allowing ordinary traffic to consume reserved probe capacity."),
    ("QueueTests", ("Requeue_",),
     "Queueing & Scheduling", "Retry admission",
     "This ensures retryable work is not dropped when the admission limit for new work has been reached."),
    ("QueueTests", ("PriorityQueue_", "PreferredPriorityWorker_"),
     "Queueing & Scheduling", "Priority ordering",
     "This ensures urgent and preferred-priority requests reach the intended workers before lower-priority work."),
    ("QueueTests", ("ThousandMixedPriorityWorkers_",),
     "Queueing & Scheduling", "Concurrent delivery",
     "This guards against lost or duplicate requests when many priority workers process the queue concurrently."),
    ("QueueTests", ("BoostIndicator_", "AddAndRemoveRequest_"),
     "Queueing & Scheduling", "Per-user fairness",
     "This prevents one high-volume user from monopolizing shared worker capacity."),
    ("UserPriorityTests", (),
     "Queueing & Scheduling", "Per-user fairness",
     "This prevents one high-volume user from monopolizing shared worker capacity."),
]
TEST_HIERARCHY_DEFAULTS = {
    "PolicyScenarioIntegrationTests": (
        "Traffic Routing", "APIM policy behavior",
        "This confirms the proxy and APIM policy cooperate to select, retry, and report backend outcomes correctly."),
    "ModelRemapTests": (
        "AI Request Compatibility", "Model request compatibility",
        "This keeps model requests valid when the proxy detects, overrides, or reroutes model families."),
    "RuleTests": (
        "Rules & Configuration", "Rule evaluation",
        "This prevents incorrect rule evaluation from sending requests to the wrong route or applying the wrong settings."),
    "Test1": (
        "Request Lifecycle", "Request processing",
        "This preserves request identity, configuration, and state as requests move through the proxy lifecycle."),
    "QueueTests": (
        "Queueing & Scheduling", "Request delivery",
        "This ensures queued requests reach the correct worker in the intended order without loss or duplication."),
    "UserPriorityTests": (
        "Queueing & Scheduling", "Per-user fairness",
        "This prevents one high-volume user from monopolizing shared worker capacity."),
    "PolicyStressStateTests": (
        "Reliability & Capacity", "Rate-limit simulation",
        "This keeps stress-test throttle and capacity evidence accurate and repeatable."),
    "PriorityLoadIntegrationTests": (
        "Reliability & Capacity", "Priority processing under load",
        "This confirms priority traffic remains serviceable under concurrent load."),
}
SCENARIO_METADATA_OVERRIDES = {
    "IteratorHeader_ControlsSharedIteratorAttemptsAcrossThreeBackends": (
        "Per-request mode controls retry breadth",
        "Sends failing requests with default, MultiPass, SinglePass, and invalid iterator headers and confirms 3, 5, 3, and 3 backend attempts."),
    "RequestDataDtoV1_OldPayloadWithoutS7PHash_DefaultsToZero": (
        "Older saved requests restore with safe defaults",
        "Loads an older payload without S7P hash or iterator fields, defaults the hash to zero, and preserves the request's current iterator mode."),
    "RequestDataDtoV1_S7PHash_RoundTripsThroughJsonAndPopulate": (
        "Saved request state survives recovery",
        "Serializes and restores request identity, headers, hash, iterator mode, and APIM policy totals while preserving legacy JSON field names."),
    "V31Policy_AllConfiguredScenariosMatchExpectedBehavior": (
        "Configured failover scenarios return expected outcomes",
        "Runs every configured APIM policy scenario and checks response status, backend attempts, correlation, and backend decision logs."),
    "V31Policy_SustainsOneThousandRequestorsForThirtyMinutes": (
        "Policy remains stable under sustained concurrency",
        "Runs 1,000 concurrent requestors through throttling and recovery and checks that every started request reaches a terminal outcome."),
    "BuiltInPriorities_ProcessOneThousandConcurrentCurlRequests": (
        "All priority classes complete under load",
        "Runs 1,000 concurrent requests and checks that high, medium, and low priorities all complete successfully without malformed results."),
}


def utc_now():
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def parse_duration_ms(value):
    if not value:
        return 0.0
    match = re.fullmatch(r"(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)", value)
    if not match:
        return 0.0
    days = int(match.group(1) or 0)
    hours = int(match.group(2))
    minutes = int(match.group(3))
    seconds = float(match.group(4))
    return ((days * 24 + hours) * 3600 + minutes * 60 + seconds) * 1000


def format_duration(milliseconds):
    if milliseconds < 1000:
        return f"{milliseconds:.0f} ms"
    seconds = milliseconds / 1000
    if seconds < 60:
        return f"{seconds:.2f} s"
    minutes, remaining = divmod(seconds, 60)
    if minutes < 60:
        return f"{int(minutes)}m {remaining:.1f}s"
    hours, remaining_minutes = divmod(minutes, 60)
    return f"{int(hours)}h {int(remaining_minutes)}m"


def humanize_identifier(value):
    words = []
    for segment in re.split(r"[_\s]+", value or ""):
        segment_words = KNOWN_TEST_TERMS.findall(segment)
        for index, word in enumerate(segment_words):
            normalized = TEST_TERM_NORMALIZATION.get(word, word)
            if normalized.isdigit() and index > 0 and segment_words[index - 1].lower() in {"v", "version"}:
                words[-1] = "V" + normalized
            elif normalized.isdigit() and index > 0 and words[-1] in TEST_TERM_NORMALIZATION.values():
                words.append("V" + normalized)
            else:
                words.append(normalized)
    return " ".join(words).strip()


def split_data_row_name(display_name, method_name):
    for candidate in (display_name, method_name):
        marker = candidate.find(" (") if candidate else -1
        if marker > 0 and candidate.endswith(")"):
            return candidate[:marker], candidate[marker + 2:-1]
    return method_name or display_name, ""


def classify_test(class_name, base_name):
    suite = class_name.rsplit(".", 1)[-1] if class_name else "Unclassified"
    for rule_suite, prefixes, domain, feature, why in TEST_HIERARCHY_RULES:
        if suite == rule_suite and (not prefixes or base_name.startswith(prefixes)):
            return domain, feature, why
    return TEST_HIERARCHY_DEFAULTS.get(
        suite,
        (
            "Other Regression Coverage",
            humanize_identifier(suite.removesuffix("Tests")) or "Unclassified behavior",
            "This guards an existing supported behavior against regression.",
        ),
    )


def parse_data_values(data_values):
    if not data_values:
        return []
    try:
        return next(csv.reader([data_values], skipinitialspace=True))
    except (csv.Error, StopIteration):
        return [data_values]


def derive_scenario_metadata(base_name, segments, data_values):
    override = SCENARIO_METADATA_OVERRIDES.get(base_name)
    if override:
        return override

    values = parse_data_values(data_values)
    if base_name == "ProcessFirst_NumericComparison_ReturnsExpectedBranch" and len(values) >= 4:
        operator, actual, threshold, expected_route = values[:4]
        symbols = {
            "greaterThan": ">",
            "greaterThanOrEqual": ">=",
            "lessThan": "<",
            "lessThanOrEqual": "<=",
        }
        comparison = symbols.get(operator, operator)
        return (
            f"{actual} {comparison} {threshold} selects the {expected_route} route",
            f"Evaluates actual value {actual} against threshold {threshold} with {operator}; the expected route is {expected_route}.",
        )

    scenario_segments = segments[1:] if len(segments) > 1 else segments
    title = humanize_identifier("_".join(scenario_segments)) or humanize_identifier(base_name) or "Unnamed scenario"
    description = f"Checks this scenario: {humanize_identifier(base_name)}."
    if values:
        input_summary = ", ".join(values)
        title = f"{title} - {input_summary}"
        description = f"{description} Inputs: {input_summary}."
    return title, description


def derive_test_metadata(test):
    method_name = test.get("methodName", "")
    display_name = test.get("displayName", "") or test.get("name", "")
    base_name, data_values = split_data_row_name(display_name, method_name)
    segments = [segment for segment in base_name.split("_") if segment]
    domain, feature, why = classify_test(test.get("className", ""), base_name)

    explicit_title = test.get("explicitTitle", "").strip()
    explicit_description = test.get("explicitDescription", "").strip()
    definition_name = test.get("definitionName", "").strip()
    if (not explicit_title and definition_name and method_name and
            definition_name != method_name and
            not definition_name.startswith(method_name + " (")):
        explicit_title = definition_name

    scenario_title, scenario_description = derive_scenario_metadata(base_name, segments, data_values)
    title = explicit_title or scenario_title

    if explicit_description:
        description = explicit_description
    else:
        description = scenario_description

    test["title"] = title.strip() or "Unnamed test"
    test["description"] = description.strip() or f"Verifies {test['title']}."
    test["domain"] = domain
    test["feature"] = feature
    test["why"] = why
    test["metadataGenerated"] = True
    return test


def element_text(parent, path):
    element = parent.find(path, TRX_NAMESPACE)
    return element.text if element is not None and element.text else ""


def parse_trx(path):
    tree = ET.parse(path)
    root = tree.getroot()

    definitions = {}
    for definition in root.findall(".//trx:TestDefinitions/trx:UnitTest", TRX_NAMESPACE):
        method = definition.find("trx:TestMethod", TRX_NAMESPACE)
        categories = [
            item.get("TestCategory", "")
            for item in definition.findall("trx:TestCategory/trx:TestCategoryItem", TRX_NAMESPACE)
            if item.get("TestCategory")
        ]
        definitions[definition.get("id", "")] = {
            "definitionName": definition.get("name", ""),
            "className": method.get("className", "") if method is not None else "",
            "methodName": method.get("name", "") if method is not None else "",
            "categories": categories,
            "description": element_text(definition, "trx:Description"),
        }

    tests = []
    for result in root.findall(".//trx:Results/trx:UnitTestResult", TRX_NAMESPACE):
        definition = definitions.get(result.get("testId", ""), {})
        output = result.find("trx:Output", TRX_NAMESPACE)
        test = {
            "name": result.get("testName", definition.get("methodName", "Unknown test")),
            "displayName": result.get("testName", definition.get("definitionName", "")),
            "definitionName": definition.get("definitionName", ""),
            "methodName": definition.get("methodName", ""),
            "className": definition.get("className", ""),
            "categories": definition.get("categories", []),
            "explicitDescription": definition.get("description", ""),
            "outcome": result.get("outcome", "Unknown"),
            "durationMs": round(parse_duration_ms(result.get("duration", "")), 3),
            "startTime": result.get("startTime", ""),
            "endTime": result.get("endTime", ""),
            "stdout": element_text(output, "trx:StdOut") if output is not None else "",
            "stderr": element_text(output, "trx:StdErr") if output is not None else "",
            "errorMessage": element_text(output, "trx:ErrorInfo/trx:Message") if output is not None else "",
            "stackTrace": element_text(output, "trx:ErrorInfo/trx:StackTrace") if output is not None else "",
        }
        tests.append(derive_test_metadata(test))

    times = root.find("trx:Times", TRX_NAMESPACE)
    return {
        "trxRunId": root.get("id", ""),
        "trxRunName": root.get("name", ""),
        "trxStartedUtc": times.get("start", "") if times is not None else "",
        "trxCompletedUtc": times.get("finish", "") if times is not None else "",
        "tests": tests,
    }


def summarize(tests):
    summary = {
        "total": len(tests),
        "passed": 0,
        "failed": 0,
        "skipped": 0,
        "inconclusive": 0,
        "other": 0,
        "durationMs": round(sum(test.get("durationMs", 0) for test in tests), 3),
    }
    for test in tests:
        outcome = test.get("outcome", "")
        if outcome == "Passed":
            summary["passed"] += 1
        elif outcome in {"Failed", "Error", "Timeout", "Aborted"}:
            summary["failed"] += 1
        elif outcome in {"NotExecuted", "NotRunnable", "Disconnected"}:
            summary["skipped"] += 1
        elif outcome in {"Inconclusive", "Warning"}:
            summary["inconclusive"] += 1
        else:
            summary["other"] += 1
    return summary


def read_console_tail(path, maximum_lines=200):
    if not path.exists():
        return ""
    try:
        return "".join(path.read_text(encoding="utf-8", errors="replace").splitlines(keepends=True)[-maximum_lines:])
    except OSError as error:
        return f"Unable to read console log: {error}"


def relative_path(path, base):
    try:
        return os.path.relpath(path, base)
    except ValueError:
        return str(path)


@contextlib.contextmanager
def manifest_lock(lock_path, timeout_seconds=30):
    deadline = time.monotonic() + timeout_seconds
    while True:
        try:
            lock_path.mkdir(parents=False)
            break
        except FileExistsError:
            try:
                if time.time() - lock_path.stat().st_mtime > 120:
                    shutil.rmtree(lock_path)
                    continue
            except FileNotFoundError:
                continue
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Timed out waiting for report lock {lock_path}")
            time.sleep(0.05)
    try:
        yield
    finally:
        shutil.rmtree(lock_path, ignore_errors=True)


def write_json_atomic(path, value):
    temporary = path.with_suffix(path.suffix + f".{os.getpid()}.tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def outcome_class(outcome):
    if outcome == "Passed":
        return "passed"
    if outcome in {"Failed", "Error", "Timeout", "Aborted"}:
        return "failed"
    if outcome in {"NotExecuted", "NotRunnable", "Disconnected"}:
        return "skipped"
    return "inconclusive"


def escape(value):
    return html.escape("" if value is None else str(value), quote=True)


def render_output_block(title, value):
    if not value:
        return ""
    return f"<h5>{escape(title)}</h5><pre>{escape(value)}</pre>"


def short_suite_name(class_name):
    return class_name.rsplit(".", 1)[-1] if class_name else "Unclassified"


def test_has_details(test):
    return any(test.get(key) for key in ("stdout", "stderr", "errorMessage", "stackTrace"))


def render_test_columns(test, has_details):
    outcome = test.get("outcome", "Unknown")
    state = outcome_class(outcome)
    detail_label = "Details" if has_details else ""
    return f"""
      <span class="test-status"><span class="status-dot {state}"></span>{escape(outcome)}</span>
            <span class="test-summary" title="{escape(test.get('name', 'Unknown test'))}">
                <span class="test-title">{escape(test.get('title', 'Unnamed test'))}</span>
                <span class="test-description">{escape(test.get('description', ''))}</span>
            </span>
      <span class="duration">{escape(format_duration(test.get('durationMs', 0)))}</span>
      <span class="detail-label">{detail_label}</span>
    """


def render_test_row(test):
    outcome = test.get("outcome", "Unknown")
    state = outcome_class(outcome)
    has_details = test_has_details(test)
    suite = short_suite_name(test.get("className", ""))
    search_value = " ".join([
        test.get("name", ""),
        test.get("title", ""),
        test.get("description", ""),
        test.get("domain", ""),
        test.get("feature", ""),
        test.get("why", ""),
        test.get("className", ""),
        " ".join(test.get("categories", [])),
        test.get("executionLabel", ""),
        outcome,
    ]).lower()
    attributes = (
        f'class="test-row {state}{" has-details" if has_details else ""}" '
        f'data-status="{state}" data-feature="{escape(hierarchy_key(test))}" '
        f'data-suite="{escape(suite)}" data-search="{escape(search_value)}"'
    )
    columns = render_test_columns(test, has_details)
    if not has_details:
        return f'<div {attributes}>{columns}</div>'

    body = "".join([
        render_output_block("Test output", test.get("stdout", "")),
        render_output_block("Standard error", test.get("stderr", "")),
        render_output_block("Failure", test.get("errorMessage", "")),
        render_output_block("Stack trace", test.get("stackTrace", "")),
    ])
    detail_open = " open" if state == "failed" else ""
    return f"""
      <details {attributes}{detail_open}>
        <summary>{columns}</summary>
        <div class="test-body">
          <div class="test-context">
                        <span><strong>Test:</strong> {escape(test.get('name', ''))}</span>
                        <span><strong>Hierarchy:</strong> {escape(test.get('domain', ''))} / {escape(test.get('feature', ''))}</span>
                        <span><strong>Source:</strong> {escape(short_suite_name(test.get('className', '')))}</span>
            <span><strong>Execution:</strong> {escape(test.get('executionLabel', ''))}</span>
            <span><strong>Started:</strong> {escape(test.get('startTime', ''))}</span>
          </div>
          {body}
        </div>
      </details>
    """


def hierarchy_key(test):
    return f"{test.get('domain', 'Other Regression Coverage')}::{test.get('feature', 'Unclassified behavior')}"


def render_hierarchy(tests):
        domains = {}
        for test in tests:
                domain = test.get("domain", "Other Regression Coverage")
                feature = test.get("feature", "Unclassified behavior")
                domains.setdefault(domain, {}).setdefault(feature, []).append(test)

        sections = []
        for domain, features in domains.items():
                domain_tests = [test for feature_tests in features.values() for test in feature_tests]
                domain_passed = sum(test.get("outcome") == "Passed" for test in domain_tests)
                feature_sections = []
                for feature, feature_tests in features.items():
                        passed = sum(test.get("outcome") == "Passed" for test in feature_tests)
                        why = feature_tests[0].get("why", "")
                        rows = "".join(render_test_row(test) for test in feature_tests)
                        feature_sections.append(f"""
                            <section class="feature-group" data-feature-group="{escape(hierarchy_key(feature_tests[0]))}">
                                <div class="feature-header">
                                    <div>
                                        <h3>{escape(feature)}</h3>
                                        <p>{escape(why)}</p>
                                    </div>
                                    <span>{passed} / {len(feature_tests)} passed</span>
                                </div>
                                <div class="feature-tests">{rows}</div>
                            </section>
                        """)
                sections.append(f"""
                    <section class="domain-group" data-domain="{escape(domain)}">
                        <div class="domain-header">
                            <h2>{escape(domain)}</h2>
                            <span>{domain_passed} / {len(domain_tests)} passed</span>
                        </div>
                        {''.join(feature_sections)}
                    </section>
                """)
        return "".join(sections)


def render_execution_diagnostics(execution):
    summary = execution["summary"]
    execution_failed = execution.get("exitCode", 0) != 0 or summary["failed"] > 0 or execution.get("parseError")
    state = "failed" if execution_failed else "passed"
    open_attribute = " open" if execution_failed else ""

    parse_error = render_output_block("TRX parse error", execution.get("parseError", ""))
    console_tail = render_output_block("Console output (last 200 lines)", execution.get("consoleTail", ""))
    console_link = ""
    if execution.get("consoleLog"):
        console_link = f'<a href="{escape(execution["consoleLog"])}">Full console log</a>'
    trx_link = ""
    if execution.get("trxPath"):
        trx_link = f'<a href="{escape(execution["trxPath"])}">TRX</a>'
    links = " &middot; ".join(link for link in (trx_link, console_link) if link)

    markup = f"""
        <details class="diagnostic-item {state}"{open_attribute}>
            <summary>
                <span class="status-dot {state}"></span>
                <span class="execution-name">{escape(execution.get('label', 'Regression execution'))}</span>
                <span class="counts">{summary['passed']} passed &middot; {summary['failed']} failed &middot; {summary['skipped'] + summary['inconclusive']} other</span>
                <span class="exit-code">Exit {escape(execution.get('exitCode'))}</span>
            </summary>
            <div class="diagnostic-body">
                <dl>
                    <div><dt>Started</dt><dd>{escape(execution.get('startedUtc'))}</dd></div>
                    <div><dt>Completed</dt><dd>{escape(execution.get('completedUtc'))}</dd></div>
                    <div><dt>Exit code</dt><dd>{escape(execution.get('exitCode'))}</dd></div>
                    <div><dt>Artifacts</dt><dd>{links or 'None'}</dd></div>
                </dl>
                {parse_error}
                <details class="raw-diagnostics">
                    <summary>Command and console</summary>
                    <div class="raw-body">
                        <h5>Command</h5>
                        <pre>{escape(execution.get('command', ''))}</pre>
                        {console_tail}
                    </div>
                </details>
            </div>
        </details>
    """
    return markup


def render_html(manifest):
    executions = manifest.get("executions", [])
    tests = []
    for execution in executions:
        for raw_test in execution.get("tests", []):
            test = dict(raw_test)
            derive_test_metadata(test)
            test["executionLabel"] = execution.get("label", "Regression execution")
            test["executionId"] = execution.get("id", "")
            tests.append(test)

    tests.sort(key=lambda test: (
        test.get("domain", ""),
        test.get("feature", ""),
        OUTCOME_ORDER.get(test.get("outcome", ""), 99),
        test.get("title", ""),
        test.get("name", ""),
    ))
    totals = {
        "total": len(tests),
        "passed": sum(test.get("outcome") == "Passed" for test in tests),
        "failed": sum(outcome_class(test.get("outcome", "")) == "failed" for test in tests),
        "other": sum(outcome_class(test.get("outcome", "")) in {"skipped", "inconclusive"} for test in tests),
        "durationMs": sum(test.get("durationMs", 0) for test in tests),
    }
    overall_failed = totals["failed"] > 0 or any(item.get("exitCode", 0) != 0 or item.get("parseError") for item in executions)
    status = "FAILED" if overall_failed else "PASSED"
    status_class = "failed" if overall_failed else "passed"
    test_html = render_hierarchy(tests)
    diagnostic_html = "".join(render_execution_diagnostics(execution) for execution in executions)
    features = sorted(
        {(hierarchy_key(test), f"{test.get('domain', '')} / {test.get('feature', '')}") for test in tests},
        key=lambda item: item[1],
    )
    feature_options = "".join(
        f'<option value="{escape(key)}">{escape(label)}</option>' for key, label in features
    )

    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Regression results: {escape(manifest.get('masterRunId'))}</title>
  <style>
    :root {{
      color-scheme: light;
            --page: #f3f5f7;
      --surface: #ffffff;
      --line: #d7dde3;
      --text: #17202a;
      --muted: #5f6b76;
      --pass: #147d4a;
      --pass-bg: #e7f5ed;
      --fail: #b42318;
      --fail-bg: #fdecea;
      --warn: #8a5a00;
      --warn-bg: #fff4d6;
      --skip: #53606d;
      --skip-bg: #edf1f4;
            --focus: #1769aa;
      --code: #111820;
      --code-text: #e8edf2;
    }}
    * {{ box-sizing: border-box; }}
    body {{ margin: 0; background: var(--page); color: var(--text); font-family: "Segoe UI", Tahoma, sans-serif; font-size: 14px; line-height: 1.45; }}
        main {{ width: min(1500px, calc(100% - 32px)); margin: 24px auto 48px; }}
    header {{ display: flex; align-items: flex-start; justify-content: space-between; gap: 24px; margin-bottom: 18px; }}
    h1 {{ margin: 0 0 4px; font-size: 24px; letter-spacing: 0; }}
        h2 {{ margin: 24px 0 10px; font-size: 17px; }}
    h5 {{ margin: 14px 0 5px; font-size: 12px; }}
    p {{ margin: 4px 0; }}
    .muted {{ color: var(--muted); }}
    .status {{ border: 1px solid; border-radius: 6px; padding: 7px 12px; font-weight: 700; }}
    .status.passed {{ color: var(--pass); background: var(--pass-bg); border-color: #a8d5ba; }}
    .status.failed {{ color: var(--fail); background: var(--fail-bg); border-color: #efb4ae; }}
        .summary-grid {{ display: grid; grid-template-columns: repeat(4, minmax(120px, 1fr)); gap: 10px; margin-bottom: 14px; }}
    .metric {{ background: var(--surface); border: 1px solid var(--line); border-radius: 6px; padding: 12px; }}
    .metric strong {{ display: block; font-size: 22px; }}
    .metric span {{ color: var(--muted); font-size: 12px; }}
        .controls {{ display: flex; align-items: center; gap: 10px; flex-wrap: wrap; background: var(--surface); border: 1px solid var(--line); border-radius: 6px; padding: 10px; margin-bottom: 10px; }}
        .controls input, .controls select {{ height: 34px; border: 1px solid #b9c2cb; border-radius: 4px; background: #fff; color: var(--text); padding: 0 10px; font: inherit; }}
        .controls input {{ flex: 1 1 320px; min-width: 180px; }}
        .controls select {{ flex: 0 1 220px; min-width: 150px; }}
        .controls input:focus, .controls select:focus, .filter-button:focus {{ outline: 2px solid var(--focus); outline-offset: 1px; }}
        .filter-group {{ display: flex; align-items: center; gap: 4px; }}
        .filter-button {{ height: 34px; border: 1px solid #b9c2cb; border-radius: 4px; background: #fff; color: var(--text); padding: 0 10px; cursor: pointer; font: inherit; }}
        .filter-button.active {{ color: #fff; background: #34495e; border-color: #34495e; }}
        .visible-count {{ margin-left: auto; color: var(--muted); font-size: 12px; white-space: nowrap; }}
        .test-list {{ background: var(--surface); border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }}
        .test-list-header, div.test-row, .test-row > summary {{ display: grid; grid-template-columns: 100px minmax(0, 1fr) 90px 58px; align-items: center; column-gap: 12px; }}
        .test-list-header {{ min-height: 34px; padding: 0 12px; background: #eef2f5; color: var(--muted); font-size: 11px; font-weight: 700; text-transform: uppercase; }}
        .domain-group + .domain-group {{ border-top: 2px solid #cbd4dc; }}
        .domain-header {{ display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 12px 14px; background: #dfe6ec; }}
        .domain-header h2 {{ margin: 0; font-size: 17px; }}
        .domain-header span {{ color: var(--muted); font-size: 12px; white-space: nowrap; }}
        .feature-group + .feature-group {{ border-top: 1px solid #cfd7de; }}
        .feature-header {{ display: flex; align-items: flex-start; justify-content: space-between; gap: 18px; padding: 10px 14px; background: #f4f7f9; border-top: 1px solid #cfd7de; }}
        .feature-header h3 {{ margin: 0 0 2px; font-size: 14px; }}
        .feature-header p {{ margin: 0; color: var(--muted); font-size: 12px; }}
        .feature-header > span {{ color: var(--muted); font-size: 12px; white-space: nowrap; }}
        .test-row {{ min-height: 52px; border-top: 1px solid #e4e8ec; }}
        div.test-row {{ padding: 7px 12px; }}
        .test-row > summary {{ min-height: 52px; padding: 7px 12px; cursor: pointer; list-style: none; }}
        .test-row > summary::-webkit-details-marker {{ display: none; }}
        .test-row > summary:hover, .test-row[open] > summary {{ background: #f7f9fa; }}
        .test-row.failed {{ border-left: 4px solid var(--fail); }}
        .test-row.inconclusive {{ border-left: 4px solid var(--warn); }}
        .test-row[hidden] {{ display: none; }}
        .test-status {{ display: flex; align-items: center; gap: 7px; font-size: 12px; font-weight: 700; }}
        .status-dot {{ width: 9px; height: 9px; border-radius: 50%; flex: 0 0 9px; background: var(--skip); }}
        .status-dot.passed {{ background: var(--pass); }}
        .status-dot.failed {{ background: var(--fail); }}
        .status-dot.inconclusive {{ background: var(--warn); }}
        .status-dot.skipped {{ background: var(--skip); }}
        .test-summary {{ display: flex; flex-direction: column; min-width: 0; gap: 2px; }}
        .test-title {{ min-width: 0; overflow-wrap: anywhere; font-weight: 650; }}
        .test-description {{ min-width: 0; color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }}
        .duration, .detail-label {{ min-width: 0; color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }}
        .duration {{ text-align: right; white-space: nowrap; }}
        .detail-label {{ color: var(--focus); text-align: right; }}
        .test-body {{ grid-column: 1 / -1; min-width: 0; overflow: hidden; border-top: 1px solid var(--line); background: #fafbfc; padding: 10px 14px 14px; }}
        .test-context {{ display: flex; flex-wrap: wrap; gap: 18px; color: var(--muted); font-size: 12px; margin-bottom: 8px; }}
        .test-context span {{ min-width: 0; overflow-wrap: anywhere; }}
        .no-results {{ padding: 24px; text-align: center; color: var(--muted); }}
        .diagnostics {{ margin-top: 22px; background: var(--surface); border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }}
        .diagnostics > summary {{ cursor: pointer; list-style: none; padding: 11px 13px; font-weight: 700; }}
        .diagnostics > summary::-webkit-details-marker, .diagnostic-item > summary::-webkit-details-marker, .raw-diagnostics > summary::-webkit-details-marker {{ display: none; }}
        .diagnostics-list {{ border-top: 1px solid var(--line); padding: 8px; }}
        .diagnostic-item {{ border: 1px solid var(--line); border-radius: 5px; margin: 6px 0; overflow: hidden; }}
        .diagnostic-item > summary {{ cursor: pointer; display: flex; align-items: center; gap: 9px; padding: 9px 11px; list-style: none; }}
    .execution-name {{ font-weight: 700; }}
        .counts {{ flex: 1; color: var(--muted); font-size: 12px; }}
        .exit-code {{ color: var(--muted); font-size: 12px; white-space: nowrap; }}
        .diagnostic-body {{ border-top: 1px solid var(--line); padding: 11px 13px 14px; }}
        .raw-diagnostics {{ margin-top: 12px; border: 1px solid var(--line); border-radius: 4px; }}
        .raw-diagnostics > summary {{ cursor: pointer; padding: 8px 10px; list-style: none; color: var(--muted); }}
        .raw-body {{ border-top: 1px solid var(--line); padding: 10px; }}
    dl {{ display: grid; grid-template-columns: repeat(4, minmax(140px, 1fr)); gap: 8px 18px; margin: 0; }}
    dl div {{ min-width: 0; }}
    dt {{ color: var(--muted); font-size: 11px; text-transform: uppercase; }}
    dd {{ margin: 2px 0 0; overflow-wrap: anywhere; }}
    a {{ color: #075fa9; }}
    pre {{ margin: 0; background: var(--code); color: var(--code-text); border-radius: 5px; padding: 10px 12px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font-family: Consolas, "Courier New", monospace; font-size: 12px; }}
    footer {{ margin-top: 18px; color: var(--muted); font-size: 12px; }}
        @media (max-width: 1000px) {{
            .test-list-header, div.test-row, .test-row > summary {{ grid-template-columns: 92px minmax(0, 1fr) 80px 52px; }}
        }}
        @media (max-width: 720px) {{
      header {{ flex-direction: column; }}
      .summary-grid {{ grid-template-columns: repeat(2, 1fr); }}
      dl {{ grid-template-columns: 1fr; }}
                        .test-list-header, div.test-row, .test-row > summary {{ grid-template-columns: 78px minmax(0, 1fr) 60px; column-gap: 8px; }}
                        .detail-label, .test-list-header > span:last-child {{ display: none; }}
            .domain-header, .feature-header {{ align-items: flex-start; }}
            .visible-count {{ width: 100%; margin-left: 0; }}
    }}
  </style>
</head>
<body>
<main>
  <header>
    <div>
      <h1>Regression Results</h1>
      <p><strong>Master execution:</strong> {escape(manifest.get('masterRunId'))}</p>
    <p class="muted">{len(executions)} executions &middot; Combined test time {escape(format_duration(totals['durationMs']))} &middot; Updated {escape(manifest.get('updatedUtc'))}</p>
    </div>
    <div class="status {status_class}">{status}</div>
  </header>
  <section class="summary-grid">
    <div class="metric"><strong>{totals['total']}</strong><span>Tests</span></div>
    <div class="metric"><strong>{totals['passed']}</strong><span>Passed</span></div>
    <div class="metric"><strong>{totals['failed']}</strong><span>Failed</span></div>
        <div class="metric"><strong>{totals['other']}</strong><span>Skipped / Other</span></div>
  </section>
    <section class="controls" aria-label="Test result filters">
        <input id="test-search" type="search" placeholder="Filter by feature, value, scenario, or test name" aria-label="Filter tests">
        <div class="filter-group" role="group" aria-label="Filter by status">
            <button class="filter-button active" type="button" data-filter="all" aria-pressed="true">All {totals['total']}</button>
            <button class="filter-button" type="button" data-filter="failed" aria-pressed="false">Failed {totals['failed']}</button>
            <button class="filter-button" type="button" data-filter="passed" aria-pressed="false">Passed {totals['passed']}</button>
            <button class="filter-button" type="button" data-filter="other" aria-pressed="false">Other {totals['other']}</button>
        </div>
        <select id="feature-filter" aria-label="Filter by feature">
            <option value="">All features</option>
            {feature_options}
        </select>
        <span id="visible-count" class="visible-count">Showing {totals['total']} tests</span>
    </section>
    <section class="test-list" id="test-list">
        <div class="test-list-header" aria-hidden="true">
            <span>Status</span><span>Scenario and why it matters</span><span>Duration</span><span></span>
        </div>
        {test_html or '<p class="no-results">No tests were recorded.</p>'}
        <p id="no-results" class="no-results" hidden>No tests match the current filters.</p>
    </section>
    <details class="diagnostics">
        <summary>Run diagnostics ({len(executions)} executions)</summary>
        <div class="diagnostics-list">{diagnostic_html or '<p class="muted">No execution diagnostics were recorded.</p>'}</div>
    </details>
    <footer>Generated from MSTest TRX results. Refresh after another execution appends to this master run.</footer>
</main>
<script>
    (() => {{
        const rows = Array.from(document.querySelectorAll('.test-row'));
        const search = document.getElementById('test-search');
        const feature = document.getElementById('feature-filter');
        const featureGroups = Array.from(document.querySelectorAll('.feature-group'));
        const domainGroups = Array.from(document.querySelectorAll('.domain-group'));
        const count = document.getElementById('visible-count');
        const empty = document.getElementById('no-results');
        const buttons = Array.from(document.querySelectorAll('.filter-button'));
        let status = 'all';

        function applyFilters() {{
            const query = search.value.trim().toLowerCase();
            const selectedFeature = feature.value;
            let visible = 0;
            for (const row of rows) {{
                const rowStatus = row.dataset.status;
                const statusMatches = status === 'all' ||
                    rowStatus === status ||
                    (status === 'other' && rowStatus !== 'passed' && rowStatus !== 'failed');
                const textMatches = !query || row.dataset.search.includes(query);
                const featureMatches = !selectedFeature || row.dataset.feature === selectedFeature;
                row.hidden = !(statusMatches && textMatches && featureMatches);
                if (!row.hidden) visible++;
            }}
            for (const group of featureGroups) {{
                group.hidden = !Array.from(group.querySelectorAll('.test-row')).some(row => !row.hidden);
            }}
            for (const group of domainGroups) {{
                group.hidden = !Array.from(group.querySelectorAll('.feature-group')).some(group => !group.hidden);
            }}
            count.textContent = `Showing ${{visible}} of ${{rows.length}} tests`;
            empty.hidden = visible !== 0;
        }}

        for (const button of buttons) {{
            button.addEventListener('click', () => {{
                status = button.dataset.filter;
                for (const item of buttons) {{
                    const active = item === button;
                    item.classList.toggle('active', active);
                    item.setAttribute('aria-pressed', active ? 'true' : 'false');
                }}
                applyFilters();
            }});
        }}
        search.addEventListener('input', applyFilters);
        feature.addEventListener('change', applyFilters);
    }})();
</script>
</body>
</html>
"""


def main():
    parser = argparse.ArgumentParser(description="Append an MSTest TRX execution to a master HTML report.")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--html", required=True)
    parser.add_argument("--master-run-id", required=True)
    parser.add_argument("--execution-id", required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--trx", required=True)
    parser.add_argument("--console-log", required=True)
    parser.add_argument("--exit-code", required=True, type=int)
    parser.add_argument("--command", required=True)
    parser.add_argument("--started-utc", required=True)
    parser.add_argument("--completed-utc", required=True)
    args = parser.parse_args()

    manifest_path = Path(args.manifest).resolve()
    html_path = Path(args.html).resolve()
    trx_path = Path(args.trx).resolve()
    console_path = Path(args.console_log).resolve()
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    html_path.parent.mkdir(parents=True, exist_ok=True)

    execution = {
        "id": args.execution_id,
        "label": args.label,
        "command": args.command,
        "startedUtc": args.started_utc,
        "completedUtc": args.completed_utc,
        "exitCode": args.exit_code,
        "trxPath": relative_path(trx_path, html_path.parent) if trx_path.exists() else "",
        "consoleLog": relative_path(console_path, html_path.parent) if console_path.exists() else "",
        "consoleTail": read_console_tail(console_path),
        "tests": [],
    }

    if trx_path.exists():
        try:
            execution.update(parse_trx(trx_path))
        except (ET.ParseError, OSError, ValueError) as error:
            execution["parseError"] = str(error)
    else:
        execution["parseError"] = f"TRX file was not created: {trx_path}"
    execution["summary"] = summarize(execution["tests"])

    lock_path = manifest_path.with_suffix(manifest_path.suffix + ".lock")
    with manifest_lock(lock_path):
        if manifest_path.exists():
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            if manifest.get("masterRunId") != args.master_run_id:
                raise ValueError(
                    f"Manifest master run '{manifest.get('masterRunId')}' does not match '{args.master_run_id}'."
                )
        else:
            manifest = {
                "schemaVersion": 2,
                "masterRunId": args.master_run_id,
                "createdUtc": args.started_utc,
                "executions": [],
            }

        manifest["schemaVersion"] = 2
        for existing_execution in manifest.get("executions", []):
            for existing_test in existing_execution.get("tests", []):
                derive_test_metadata(existing_test)

        manifest["updatedUtc"] = utc_now()
        manifest["executions"] = [
            item for item in manifest.get("executions", []) if item.get("id") != args.execution_id
        ]
        manifest["executions"].append(execution)
        manifest["executions"].sort(key=lambda item: (item.get("startedUtc", ""), item.get("id", "")))

        write_json_atomic(manifest_path, manifest)
        temporary_html = html_path.with_suffix(html_path.suffix + f".{os.getpid()}.tmp")
        temporary_html.write_text(render_html(manifest), encoding="utf-8")
        os.replace(temporary_html, html_path)

    print(html_path)


if __name__ == "__main__":
    main()
