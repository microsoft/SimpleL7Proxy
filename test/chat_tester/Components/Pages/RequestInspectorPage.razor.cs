using chat_tester.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;

namespace chat_tester.Components.Pages;

public partial class RequestInspectorPage
{
    // Response bookkeeping
    private sealed class ChatResponseMetrics
    {
        public string Status { get; set; } = "-";
        public string ContentType { get; set; } = "-";
        public TimeSpan? TimeToFirstByte { get; set; }
        public TimeSpan? Duration { get; set; }
        public int Chunks { get; set; }
        public long TotalBytes { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public int? ReasoningTokens { get; set; }
        public int? CachedInputTokens { get; set; }
        public int? TotalTokens { get; set; }
    }

    // Page constants
    private const string DefaultPageTagline = "Send a chat-style request and inspect the returned stream.";
    private const string HistorySource = "Request inspector";
    private const string RequestPanel = PanelDisplayStateCoordinator.RequestPanel;
    private const string ResultPanel = PanelDisplayStateCoordinator.ResultPanel;
    private const string RawPanel = PanelDisplayStateCoordinator.RawPanel;

    // Services
    [Inject]
    private IOptions<ChatTesterOptions> Options { get; set; } = default!;

    [Inject]
    private HttpClient Http { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private RequestDebugSettings DebugSettings { get; set; } = default!;

    [Inject]
    private AutoCollapseSettings AutoCollapseSettings { get; set; } = default!;

    [Inject]
    private ChatHistoryStore ChatHistoryStore { get; set; } = default!;

    [Inject]
    private AuthTokenSettings AuthSettings { get; set; } = default!;

    [Inject]
    private UserSettings UserSettings { get; set; } = default!;

    [Inject]
    private HeaderSettings HeaderSettings { get; set; } = default!;

    // Child components
    private TestFormPanel? FormPanel { get; set; }

    private RequestBodyPanel? BodyPanel { get; set; }

    private ElementReference ChatResponseRef;

    // Route/query state
    [Parameter]
    [SupplyParameterFromQuery(Name = "historyEntryId")]
    public string? HistoryEntryId { get; set; }

    // Initial request defaults
    private string InitialServerBaseUrl { get; set; } = string.Empty;
    private string InitialEndpointPath { get; set; } = string.Empty;
    private string InitialContentType { get; set; } = ChatTesterHttp.JsonContentType;
    private string InitialRequestBody { get; set; } = string.Empty;

    // Page chrome and history selection
    private string PageTagline { get; set; } = DefaultPageTagline;
    private string? SelectedHistoryEntryId { get; set; }
    private string? LoadedHistoryEntryId { get; set; }
    private IReadOnlyList<ChatHistoryEntry> HistoryEntries => ChatHistoryStore.GetSnapshot();

    // Layout state
    private bool AutoCollapse { get; set; } = true;
    private string ActivePanel { get; set; } = RequestPanel;
    private PanelDisplayState RequestPanelState { get; set; } = PanelDisplayState.Expanded;
    private PanelDisplayState ChatResponseState { get; set; } = PanelDisplayState.Minimized;
    private PanelDisplayState RawResponseState { get; set; } = PanelDisplayState.Minimized;

    // Request/response state
    private bool IsRunning { get; set; }
    private bool HasProxyError { get; set; }
    private string StatusMessage { get; set; } = "Ready to send a single request.";
    private string ObservedResponse { get; set; } = "No response yet.";
    private string DisplayResponse { get; set; } = "Waiting for the request ...";
    private string ActiveResponseTab { get; set; } = "response-body";
    private string RequestHeadersText { get; set; } = "No request has been sent yet.";
    private string ResponseHeadersText { get; set; } = "No response has been received yet.";
    private string RequestBodyDisplay { get; set; } = "No request body yet.";
    private ResponseDisplayFormat ResponseFormat { get; set; } = ResponseDisplayFormat.Text;
    private ChatResponseMetrics ResponseMetrics { get; set; } = new();

    // Derived UI state
    private bool HasResponse => !string.IsNullOrWhiteSpace(ObservedResponse)
        && !ObservedResponse.Equals("No response yet.", StringComparison.OrdinalIgnoreCase)
        && !ObservedResponse.Equals("Awaiting response...", StringComparison.OrdinalIgnoreCase)
        && !ObservedResponse.Equals("The server returned an empty response body.", StringComparison.OrdinalIgnoreCase);

    private int RequestTextAreaRows => HasResponse ? 7 : 12;

    // Lifecycle
    protected override void OnInitialized()
    {
        var options = Options.Value;
        InitialServerBaseUrl = options.ServerBaseUrl;
        InitialEndpointPath = options.ChatEndpointPath;
        InitialRequestBody = options.ChatRequestBody;
        AuthSettings.ApplyDefaultsIfMissing(options.AuthorizationHeaderName, options.AuthorizationHeaderPrefix);
        UserSettings.ApplyDefaultsIfMissing(options.UserHeaderName, options.PriorityKeyHeader, options.UserNames);
        HeaderSettings.ApplyDefaultsIfMissing(options.DefaultHeaders);
        AutoCollapse = AutoCollapseSettings.Enabled;
        if (!AutoCollapse)
        {
            EnsureAllPanesVisible();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (firstRender)
            {
                await JS.InvokeVoidAsync("chatScroll.register", ChatResponseRef);
            }

            await JS.InvokeVoidAsync("responseSearch.register", ChatResponseRef);
            await JS.InvokeVoidAsync("responseSearch.refresh");
            await JS.InvokeVoidAsync("chatScroll.scrollIfStuck", ChatResponseRef);
        }
        catch (JSException)
        {
            // Ignore interop errors during prerender or disconnect.
        }

        await LoadRequestedHistoryEntryAsync();
    }

    // History loading
    private async Task LoadRequestedHistoryEntryAsync()
    {
        if (string.IsNullOrWhiteSpace(HistoryEntryId)
            || string.Equals(LoadedHistoryEntryId, HistoryEntryId, StringComparison.Ordinal))
        {
            return;
        }

        LoadedHistoryEntryId = HistoryEntryId;
        var entry = HistoryEntries.FirstOrDefault(entry => string.Equals(entry.Id, HistoryEntryId, StringComparison.Ordinal)
            && string.Equals(entry.Source, HistorySource, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        await SelectHistoryEntry(entry);
        StateHasChanged();
    }

    // Panel visibility and CSS helpers
    private bool IsRequestPanelMinimized => RequestPanelState == PanelDisplayState.Minimized;

    private bool ShowRequestPanel => !IsRequestPanelMinimized;

    private bool IsChatResponseMinimized => ChatResponseState == PanelDisplayState.Minimized;

    private bool ShowChatResponsePanel => !IsChatResponseMinimized;

    private bool IsChatResponseFullscreen => ChatResponseState == PanelDisplayState.Fullscreen;

    private bool IsRawResponseMinimized => RawResponseState == PanelDisplayState.Minimized;

    private bool ShowRawResponsePanel => !IsRawResponseMinimized;

    private bool IsResponseColumnHidden => IsChatResponseMinimized && IsRawResponseMinimized;

    private bool ShouldHideRequestPanel => IsRequestPanelMinimized;

    private bool ShouldHideChatResponse => IsChatResponseMinimized;

    private bool ShouldHideRawResponse => IsRawResponseMinimized;

    private bool ShouldHideResponseColumn => IsResponseColumnHidden;

    private string WorkspaceStateCss
    {
        get
        {
            var classes = new List<string>();
            if (!AutoCollapse)
            {
                classes.Add("manual-layout");
            }

            if (IsRequestPanelMinimized)
            {
                classes.Add("request-minimized");
            }

            if (IsResponseColumnHidden)
            {
                classes.Add("response-hidden");
            }

            return string.Join(' ', classes);
        }
    }

    private string ChatResponseCardStateCss => ChatResponseState switch
    {
        PanelDisplayState.Minimized => "panel-minimized",
        PanelDisplayState.Fullscreen => "fs-overlay",
        _ => string.Empty
    };

    // Panel state transitions
    private Task SelectPanelAsync(string panel)
    {
        if (!AutoCollapse)
        {
            ActivePanel = panel;
            EnsureAllPanesVisible();
            return Task.CompletedTask;
        }

        ApplyPanelState(PanelDisplayStateCoordinator.SelectPanel(panel));
        return Task.CompletedTask;
    }

    private Task SetRequestPanelStateAsync(PanelDisplayState state)
    {
        RequestPanelState = state;
        NormalizePanelStates();
        return Task.CompletedTask;
    }

    private Task SetChatResponseStateAsync(PanelDisplayState state)
    {
        ChatResponseState = state;
        NormalizePanelStates();
        return Task.CompletedTask;
    }

    private Task SetRawResponseStateAsync(PanelDisplayState state)
    {
        RawResponseState = state;
        NormalizePanelStates();
        return Task.CompletedTask;
    }

    private Task SetRequestPanelVisibleAsync(ChangeEventArgs args)
    {
        RequestPanelState = GetChecked(args) ? PanelDisplayState.Expanded : PanelDisplayState.Minimized;
        if (RequestPanelState != PanelDisplayState.Minimized)
        {
            ActivePanel = RequestPanel;
        }

        NormalizePanelStates();
        return Task.CompletedTask;
    }

    private Task SetChatResponsePanelVisibleAsync(ChangeEventArgs args)
    {
        ChatResponseState = GetChecked(args) ? PanelDisplayState.Expanded : PanelDisplayState.Minimized;
        if (ChatResponseState != PanelDisplayState.Minimized)
        {
            ActivePanel = ResultPanel;
        }

        NormalizePanelStates();
        return Task.CompletedTask;
    }

    private Task SetRawResponsePanelVisibleAsync(ChangeEventArgs args)
    {
        RawResponseState = GetChecked(args) ? PanelDisplayState.Expanded : PanelDisplayState.Minimized;
        if (RawResponseState != PanelDisplayState.Minimized)
        {
            ActivePanel = RawPanel;
        }

        NormalizePanelStates();
        return Task.CompletedTask;
    }

    private Task SetAutoCollapseAsync(bool autoCollapse)
    {
        AutoCollapse = autoCollapse;
        AutoCollapseSettings.Enabled = autoCollapse;

        if (!AutoCollapse)
        {
            EnsureAllPanesVisible();
        }
        else
        {
            ApplyPanelState(PanelDisplayStateCoordinator.SelectPanel(ActivePanel));
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    private Task SetActiveResponseTabAsync(string tab)
    {
        ActiveResponseTab = tab;
        return Task.CompletedTask;
    }

    private void ShowResultPanelAfterResponse()
    {
        if (!AutoCollapse)
        {
            ActivePanel = ResultPanel;
            EnsureAllPanesVisible();
            return;
        }

        ApplyPanelState(PanelDisplayStateCoordinator.SelectPanel(ResultPanel));
    }

    private void ShowRequestAndResultPanels()
    {
        if (!AutoCollapse)
        {
            ActivePanel = ResultPanel;
            EnsureAllPanesVisible();
            return;
        }

        ApplyPanelState(new PanelDisplayStateGroup(
            ResultPanel,
            PanelDisplayState.Expanded,
            PanelDisplayState.Expanded,
            PanelDisplayState.Minimized));
    }

    private void NormalizePanelStates()
    {
        ApplyPanelState(PanelDisplayStateCoordinator.Normalize(
            RequestPanelState,
            ChatResponseState,
            RawResponseState,
            ActivePanel));
    }

    private void EnsureAllPanesVisible()
    {
        ActivePanel = string.IsNullOrWhiteSpace(ActivePanel) ? RequestPanel : ActivePanel;
        RequestPanelState = PanelDisplayState.Expanded;
        ChatResponseState = PanelDisplayState.Expanded;
        RawResponseState = PanelDisplayState.Expanded;
    }

    private void ApplyPanelState(PanelDisplayStateGroup panelState)
    {
        ActivePanel = panelState.ActivePanel;
        RequestPanelState = panelState.Request;
        ChatResponseState = panelState.Result;
        RawResponseState = panelState.Raw;
    }

    private void SetResponseFormat(ResponseDisplayFormat format)
    {
        ResponseFormat = format;
    }

    private static bool GetChecked(ChangeEventArgs args) => args.Value switch
    {
        bool value => value,
        string text when bool.TryParse(text, out var value) => value,
        _ => false
    };

    // Request execution
    private async Task SendSingleChatCallAsync()
    {
        var serverBaseUrl = FormPanel?.ServerBaseUrl ?? string.Empty;
        var endpointPath = BodyPanel?.EndpointPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serverBaseUrl) || string.IsNullOrWhiteSpace(endpointPath))
        {
            StatusMessage = "Set both the server URL and endpoint path before sending the request.";
            return;
        }

        var requestBody = BodyPanel?.RequestBody ?? string.Empty;
        var contentType = BodyPanel?.ContentType ?? ChatTesterHttp.JsonContentType;
        IsRunning = true;
        SelectedHistoryEntryId = null;
        PageTagline = DefaultPageTagline;
        StatusMessage = "Sending the chat request and waiting for the response...";
        ObservedResponse = "Awaiting response...";
        DisplayResponse = "Generating response stream...";
        ResponseMetrics = new ChatResponseMetrics { Status = "Sending" };
        HasProxyError = false;
        RequestBodyDisplay = requestBody;
        ShowRequestAndResultPanels();
        StateHasChanged();

        using var metricsTimer = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var metricsTimerTask = RunMetricsTimerAsync(stopwatch, metricsTimer.Token);

        try
        {
            var client = Http;
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatTesterHttp.BuildUri(serverBaseUrl, endpointPath));
            request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, contentType);
            request.Headers.Add(ChatTesterHttp.AcceptHeaderName, ChatTesterHttp.EventStreamContentType);
            ChatTesterHttp.ApplyDebugHeader(request, DebugSettings);

            RequestHeadersText = ChatTesterHttp.SummarizeRequestHeaders(request);

            if (FormPanel is not null)
            {
                await FormPanel.ApplyAuthorizationAsync(request);
                FormPanel.ApplyUser(request, 0);
                FormPanel.ApplyHeaders(request, 0);
            }

            RequestHeadersText = ChatTesterHttp.SummarizeRequestHeaders(request);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            ResponseMetrics.TimeToFirstByte = stopwatch.Elapsed;
            ResponseMetrics.Status = $"{(int)response.StatusCode} {response.ReasonPhrase ?? string.Empty}".Trim();
            ResponseMetrics.ContentType = response.Content.Headers.ContentType?.ToString() ?? "-";
            ApplyDetectedResponseFormat(ResponseMetrics.ContentType);
            ResponseHeadersText = ChatTesterHttp.SummarizeResponseHeaders(response);
            StatusMessage = $"Streaming response: {(int)response.StatusCode} {response.ReasonPhrase ?? ""}".Trim();
            StateHasChanged();
            await StreamResponseAsync(response, stopwatch);
            ResponseMetrics.Duration = stopwatch.Elapsed;
            StatusMessage = $"Completed with {(int)response.StatusCode} {response.ReasonPhrase ?? ""}".Trim();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                ShowResultPanelAfterResponse();
            }
        }
        catch (Exception ex)
        {
            ObservedResponse = ex.Message;
            ResponseHeadersText = "No response headers available.";
            DisplayResponse = $"I could not receive a stream from the server.\nReason: {ex.Message}";
            StatusMessage = "The request failed before a full response was received.";
            ResponseMetrics.Status = "Failed";
            ShowRequestAndResultPanels();
        }
        finally
        {
            metricsTimer.Cancel();
            try
            {
                await metricsTimerTask;
            }
            catch (OperationCanceledException)
            {
            }

            ResponseMetrics.Duration = stopwatch.Elapsed;
            await SaveHistoryEntryAsync(serverBaseUrl, endpointPath, contentType);
            IsRunning = false;
            StateHasChanged();
        }
    }

    // History persistence and restore
    private async Task SaveHistoryEntryAsync(string serverBaseUrl, string endpointPath, string contentType)
    {
        var entry = await ChatHistoryStore.AddRequest(new RequestHistoryEntry
        {
            Source = HistorySource,
            Method = "POST",
            ServerBaseUrl = serverBaseUrl,
            EndpointPath = endpointPath,
            ContentType = contentType,
            RequestHeadersText = RequestHeadersText,
            ResponseHeadersText = ResponseHeadersText,
            RequestBody = RequestBodyDisplay,
            ResponseBody = ObservedResponse,
            DisplayResponse = DisplayResponse,
            StatusMessage = StatusMessage,
            ResponseFormat = ResponseFormat.ToString(),
            ActiveResponseTab = ActiveResponseTab,
            HasProxyError = HasProxyError,
            Metrics = new ChatHistoryMetrics
            {
                Status = ResponseMetrics.Status,
                ContentType = ResponseMetrics.ContentType,
                TimeToFirstByte = ResponseMetrics.TimeToFirstByte,
                Duration = ResponseMetrics.Duration,
                Chunks = ResponseMetrics.Chunks,
                TotalBytes = ResponseMetrics.TotalBytes,
                InputTokens = ResponseMetrics.InputTokens,
                OutputTokens = ResponseMetrics.OutputTokens,
                ReasoningTokens = ResponseMetrics.ReasoningTokens,
                CachedInputTokens = ResponseMetrics.CachedInputTokens,
                TotalTokens = ResponseMetrics.TotalTokens
            }
        });

        SelectedHistoryEntryId = entry.Id;
        PageTagline = $"Showing response from {FormatHistoryTimestamp(entry.CreatedAt)}.";
    }

    private async Task SelectHistoryEntry(ChatHistoryEntry entry)
    {
        SelectedHistoryEntryId = entry.Id;
        PageTagline = $"Showing response from {FormatHistoryTimestamp(entry.CreatedAt)}.";
        FormPanel?.LoadServerBaseUrl(entry.ServerBaseUrl);
        if (BodyPanel is not null)
        {
            await BodyPanel.LoadRequestAsync(entry);
        }

        RequestHeadersText = entry.RequestHeadersText;
        ResponseHeadersText = entry.ResponseHeadersText;
        RequestBodyDisplay = entry.RequestBody;
        ObservedResponse = entry.ResponseBody;
        DisplayResponse = entry.DisplayResponse;
        StatusMessage = entry.StatusMessage;
        ActiveResponseTab = string.IsNullOrWhiteSpace(entry.ActiveResponseTab) ? "response-body" : entry.ActiveResponseTab;
        if (ActiveResponseTab == "proxy-error")
        {
            ActiveResponseTab = "backend-log";
        }

        HasProxyError = entry.HasProxyError;
        ResponseMetrics = new ChatResponseMetrics
        {
            Status = entry.Metrics.Status,
            ContentType = entry.Metrics.ContentType,
            TimeToFirstByte = entry.Metrics.TimeToFirstByte,
            Duration = entry.Metrics.Duration,
            Chunks = entry.Metrics.Chunks,
            TotalBytes = entry.Metrics.TotalBytes,
            InputTokens = entry.Metrics.InputTokens,
            OutputTokens = entry.Metrics.OutputTokens,
            ReasoningTokens = entry.Metrics.ReasoningTokens,
            CachedInputTokens = entry.Metrics.CachedInputTokens,
            TotalTokens = entry.Metrics.TotalTokens
        };

        if (Enum.TryParse<ResponseDisplayFormat>(entry.ResponseFormat, out var responseFormat))
        {
            ResponseFormat = responseFormat;
        }
        else
        {
            ResponseFormat = ResponseDisplayFormat.Text;
        }

        ShowResultPanelAfterResponse();
    }

    private async Task DeleteHistoryEntryAsync(ChatHistoryEntry entry)
    {
        var wasSelected = string.Equals(SelectedHistoryEntryId, entry.Id, StringComparison.Ordinal);
        if (await ChatHistoryStore.DeleteAsync(entry.Id))
        {
            if (wasSelected)
            {
                SelectedHistoryEntryId = null;
                PageTagline = DefaultPageTagline;
            }
        }
    }

    // Response streaming and metrics
    private async Task RunMetricsTimerAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ResponseMetrics.Duration = stopwatch.Elapsed;
            await InvokeAsync(StateHasChanged);
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task StreamResponseAsync(HttpResponseMessage response, Stopwatch stopwatch)
    {
        var rawBuilder = new StringBuilder();
        var assistantBuilder = new StringBuilder();
        var sawAssistantContent = false;
        DisplayResponse = string.Empty;
        ObservedResponse = string.Empty;
        await InvokeAsync(StateHasChanged);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync() is { } line)
        {
            ResponseMetrics.Chunks++;
            ResponseMetrics.TotalBytes += Encoding.UTF8.GetByteCount(line) + 1;
            rawBuilder.AppendLine(line);
            ObservedResponse = rawBuilder.ToString();
            UpdateProxyErrorState();

            var payload = ChatStreamParser.ExtractDataPayload(line);
            if (payload is not null)
            {
                if (ChatStreamParser.TryExtractTokenUsage(payload, out var tokenUsage))
                {
                    ApplyTokenUsage(tokenUsage);
                }

                var piece = ChatStreamParser.ExtractDeltaText(payload);
                if (!string.IsNullOrEmpty(piece))
                {
                    assistantBuilder.Append(piece);
                    sawAssistantContent = true;
                    DisplayResponse = assistantBuilder.ToString();
                }
            }

            ResponseMetrics.Duration = stopwatch.Elapsed;
            await InvokeAsync(StateHasChanged);
        }

        if (rawBuilder.Length == 0)
        {
            ObservedResponse = "The server returned an empty response body.";
            DisplayResponse = "I did not receive any content from the server.";
        }
        else if (!sawAssistantContent)
        {
            DisplayResponse = BuildDisplayResponse(rawBuilder.ToString());
        }

        ApplyTokenUsage(ChatStreamParser.ExtractTokenUsage(rawBuilder.ToString()));

        UpdateProxyErrorState();

        await InvokeAsync(StateHasChanged);
    }

    private void ApplyTokenUsage(ChatTokenUsage usage)
    {
        if (!usage.HasAny)
        {
            return;
        }

        ResponseMetrics.InputTokens = usage.InputTokens ?? ResponseMetrics.InputTokens;
        ResponseMetrics.OutputTokens = usage.OutputTokens ?? ResponseMetrics.OutputTokens;
        ResponseMetrics.ReasoningTokens = usage.ReasoningTokens ?? ResponseMetrics.ReasoningTokens;
        ResponseMetrics.CachedInputTokens = usage.CachedInputTokens ?? ResponseMetrics.CachedInputTokens;
        ResponseMetrics.TotalTokens = usage.TotalTokens ?? ResponseMetrics.TotalTokens;
    }

    private void UpdateProxyErrorState()
    {
        if (!ObservedResponse.Contains("No active hosts were able", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!HasProxyError)
        {
            HasProxyError = true;
            ActiveResponseTab = "backend-log";
        }
    }

    private string BuildDisplayResponse(string observed)
    {
        if (string.IsNullOrWhiteSpace(observed))
        {
            return "I did not receive any content from the server.";
        }

        var interpretedContent = ChatStreamParser.ExtractAllContent(observed);
        if (!string.IsNullOrWhiteSpace(interpretedContent))
        {
            return interpretedContent;
        }

        interpretedContent = ChatStreamParser.ExtractDisplayContent(observed);
        if (!string.IsNullOrWhiteSpace(interpretedContent))
        {
            return interpretedContent;
        }

        // Not an event stream (or no stream content found): show the entire response body.
        return observed;
    }

    // Response display formatting
    private void ApplyDetectedResponseFormat(string contentType)
    {
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            ResponseFormat = ResponseDisplayFormat.Html;
        }
        else if (contentType.Contains("markdown", StringComparison.OrdinalIgnoreCase))
        {
            ResponseFormat = ResponseDisplayFormat.Markdown;
        }
        else
        {
            ResponseFormat = ResponseDisplayFormat.Text;
        }
    }

    private static string FormatHistoryTimestamp(DateTimeOffset value) =>
        value.LocalDateTime.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);

    private static string MarkdownToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var inList = false;
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (inList)
                {
                    builder.AppendLine("</ul>");
                    inList = false;
                }
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!inList)
                {
                    builder.AppendLine("<ul>");
                    inList = true;
                }
                builder.Append("<li>").Append(InlineMarkdown(line[2..])).AppendLine("</li>");
                continue;
            }

            if (inList)
            {
                builder.AppendLine("</ul>");
                inList = false;
            }

            if (TryParseMarkdownHeading(line, out var headingLevel, out var headingText))
            {
                builder.Append("<h").Append(headingLevel).Append(">").Append(InlineMarkdown(headingText)).Append("</h").Append(headingLevel).AppendLine(">");
            }
            else
            {
                builder.Append("<p>").Append(InlineMarkdown(line)).AppendLine("</p>");
            }
        }

        if (inList)
        {
            builder.AppendLine("</ul>");
        }

        return builder.ToString();
    }

    private static string InlineMarkdown(string value)
    {
        var encoded = WebUtility.HtmlEncode(value);
        while (TryReplaceDelimited(ref encoded, "**", "<strong>", "</strong>")) { }
        while (TryReplaceDelimited(ref encoded, "`", "<code>", "</code>")) { }
        while (TryReplaceDelimited(ref encoded, "*", "<em>", "</em>")) { }
        return encoded;
    }

    private static bool TryParseMarkdownHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        while (level < line.Length && level < 6 && line[level] == '#')
        {
            level++;
        }

        if (level == 0 || level >= line.Length || line[level] != ' ')
        {
            level = 0;
            return false;
        }

        text = line[(level + 1)..];
        return true;
    }

    private static bool TryReplaceDelimited(ref string value, string delimiter, string openTag, string closeTag)
    {
        var start = value.IndexOf(delimiter, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var end = value.IndexOf(delimiter, start + delimiter.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        value = value[..start] + openTag + value[(start + delimiter.Length)..end] + closeTag + value[(end + delimiter.Length)..];
        return true;
    }
}
