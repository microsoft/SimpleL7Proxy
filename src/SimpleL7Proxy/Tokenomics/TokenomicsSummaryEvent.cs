using System.Net;
using System.Globalization;
using System.Threading;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Tokenomics.Llm;

namespace SimpleL7Proxy.Tokenomics;

public sealed class TokenomicsSummaryEvent : ProxyEvent
{
    private readonly string? _modelBefore;
    private readonly int _priorityBefore;
    private int _emitted;

    public TokenomicsSummaryEvent() : base(24)
    {
    }
    public TokenomicsSummaryEvent( string requestId, int evaluationSequence, string userId, string tenant, 
        string requestedModel,  string modelBefore,  int priorityBefore,  string policyCondition,  
        TokenActionEnum policyAction) : base(24)
    {
        _modelBefore = modelBefore;
        _priorityBefore = priorityBefore;

        Type = EventType.Tokenomics;
        MID = requestId;

        this["SchemaVersion"] = "1";
        this["RecordKind"] = "PolicyDecision";
        this["DecisionId"] = Guid.NewGuid().ToString("N");
        this["EvaluationSequence"] = Format(evaluationSequence);
        this["TimestampUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        this["UserId"] = userId;
        this["Tenant"] = tenant;
        this["RequestedModel"] = requestedModel;
        this["ModelBefore"] = modelBefore;
        this["ModelAfter"] = modelBefore;
        this["ModelChanged"] = "false";
        this["PriorityBefore"] = Format(priorityBefore);
        this["PriorityAfter"] = Format(priorityBefore);
        this["PriorityChanged"] = "false";
        this["PolicyCondition"] = policyCondition;
        this["PolicyAction"] = policyAction.ToString();
        this["Decision"] = "Pending";
        this["BackendCallAllowed"] = "false";
    }

    public void SetModel(string model)
    {
        this["ModelAfter"] = model;
        this["ModelChanged"] = Format(!string.Equals(
            _modelBefore, model, StringComparison.OrdinalIgnoreCase));
    }

    public void SetPriority(int priority)
    {
        this["PriorityAfter"] = Format(priority);
        this["PriorityChanged"] = Format(_priorityBefore != priority);
    }

    public void SetDecision(TokenDecisionEnum decision, bool backendCallAllowed)
    {
        this["Decision"] = decision.ToString();
        this["BackendCallAllowed"] = Format(backendCallAllowed);
    }

    public void SetRetryAfter(int milliseconds)
    {
        this["RetryAfterMs"] = Format(milliseconds);
    }

    public void SetLocalStatus(int statusCode)
    {
        this["LocalStatusCode"] = Format(statusCode);
    }

    public void PrepForFinalStats( HttpStatusCode httpStatusCode, TimeSpan duration, string backendHostname, string effectiveModel,
        int backendAttempts, int lifetimeBackendAttempts, LLMStats? usage)
    {
        ResetForOutcome();

        Status = httpStatusCode;
        Duration = duration;
        Exception = null;

        this["RecordKind"] = "RequestOutcome";
        this["OutcomeId"] = Guid.NewGuid().ToString("N");
        this["TimestampUtc"] = DateTime.UtcNow.ToString("O");
        this["OutcomeSource"] = "Backend";
        this["BackendResponseReceived"] = "true";
        this["BackendHostname"] = backendHostname;
        this["EffectiveModel"] = effectiveModel;
        this["StatusCode"] = Format(httpStatusCode);
        this["Success"] = Format((int)httpStatusCode is >= 200 and < 300);
        this["LatencyMs"] = Format(duration.TotalMilliseconds);
        this["BackendAttempts"] = Format(backendAttempts);
        this["LifetimeBackendAttempts"] = Format(lifetimeBackendAttempts);
        this["UsageAvailable"] = Format(usage != null);
        this["InputTokens"] = Format(usage?.InputTokens ?? 0);
        this["CachedTokens"] = Format(usage?.CachedTokens ?? 0);
        this["OutputTokens"] = Format(usage?.OutputTokens ?? 0);
        this["IsJailbreakDetected"] = Format(usage?.IsJailbreakDetected ?? false);
        this["IsContentFiltered"] = Format(usage?.IsContentFiltered ?? false);

        Interlocked.Exchange(ref _emitted, 0);
    }

    private void ResetForOutcome()
    {
        TryRemove("DecisionId", out _);
        TryRemove("ModelBefore", out _);
        TryRemove("ModelAfter", out _);
        TryRemove("ModelChanged", out _);
        TryRemove("PriorityBefore", out _);
        TryRemove("PriorityAfter", out _);
        TryRemove("PriorityChanged", out _);
        TryRemove("PolicyCondition", out _);
        TryRemove("PolicyAction", out _);
        TryRemove("Decision", out _);
        TryRemove("BackendCallAllowed", out _);
        TryRemove("RetryAfterMs", out _);
        TryRemove("LocalStatusCode", out _);

        Interlocked.Exchange(ref _emitted, 0);
    }

    public void PrepForExceptionStats(
        HttpStatusCode status,  TimeSpan duration,  string outcomeSource,  bool backendResponseReceived,  string effectiveModel,
        int backendAttempts,  int lifetimeBackendAttempts,  string error)
    {
        ResetForOutcome();

        Status = status;
        Duration = duration;
        Exception = null;

        this["RecordKind"] = "RequestOutcome";
        this["OutcomeId"] = Guid.NewGuid().ToString("N");
        this["TimestampUtc"] = DateTime.UtcNow.ToString("O");
        this["OutcomeSource"] = outcomeSource;
        this["BackendResponseReceived"] = Format(backendResponseReceived);
        this["EffectiveModel"] = effectiveModel;
        this["StatusCode"] = Format((int)status);
        this["Success"] = "false";
        this["LatencyMs"] = Format(duration.TotalMilliseconds);
        this["BackendAttempts"] = Format(backendAttempts);
        this["LifetimeBackendAttempts"] = Format(lifetimeBackendAttempts);
        this["UsageAvailable"] = "false";
        this["InputTokens"] = "0";
        this["CachedTokens"] = "0";
        this["OutputTokens"] = "0";
        this["IsJailbreakDetected"] = "false";
        this["IsContentFiltered"] = "false";
        this["Error"] = error;
    }

    public void Emit()
    {
        if (Interlocked.Exchange(ref _emitted, 1) == 0)
        {
            SendEvent();
        }
    }

    private static string Format(bool value) => value ? "true" : "false";

    private static string Format<T>(T value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}