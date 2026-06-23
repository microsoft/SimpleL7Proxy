using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace chat_tester.Components.Shared;

/// <summary>
/// Base class for per-model request components. Each model component renders a Simple or
/// Advanced pane based on the cascaded <see cref="SimpleMode"/> value supplied by the parent
/// container, and knows how to construct its own request via <see cref="BuildRequest"/>.
/// Derived components implement <see cref="BuildPayload"/> for their specific request shape.
/// </summary>
public abstract class ModelComponentBase : ComponentBase
{
    /// <summary>Default user message used when none has been entered.</summary>
    protected const string DefaultMessage = "tell me about the history of jokes";

    /// <summary>Content-Type options offered in simple mode.</summary>
    protected static readonly string[] ContentTypeOptions =
    {
        ChatTesterHttp.JsonContentType,
        "application/json; charset=utf-8",
        "application/x-ndjson"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private string? _lastTemplateKey;
    private bool _lastSimpleMode;
    private bool _seededVerbatim;

    /// <summary>Simple vs advanced pane selection, cascaded from the parent container.</summary>
    [CascadingParameter(Name = "SimpleMode")]
    public bool SimpleMode { get; set; }

    /// <summary>The model template this component renders and builds a request for.</summary>
    [Parameter]
    public ModelTemplate Template { get; set; } = default!;

    /// <summary>Row count for the advanced request-body editor.</summary>
    [Parameter]
    public int Rows { get; set; } = 12;

    /// <summary>User message, two-way bound to the parent so it carries across model switches.</summary>
    [Parameter]
    public string Message { get; set; } = DefaultMessage;

    /// <summary>Callback raised when <see cref="Message"/> changes.</summary>
    [Parameter]
    public EventCallback<string> MessageChanged { get; set; }

    /// <summary>max_tokens value, two-way bound to the parent (used by schemas that support it).</summary>
    [Parameter]
    public int MaxTokens { get; set; } = 1024;

    /// <summary>Callback raised when <see cref="MaxTokens"/> changes.</summary>
    [Parameter]
    public EventCallback<int> MaxTokensChanged { get; set; }

    /// <summary>Content-Type, two-way bound to the parent so it carries across model switches.</summary>
    [Parameter]
    public string ContentType { get; set; } = ChatTesterHttp.JsonContentType;

    /// <summary>Callback raised when <see cref="ContentType"/> changes.</summary>
    [Parameter]
    public EventCallback<string> ContentTypeChanged { get; set; }

    /// <summary>Optional verbatim request body used to seed the first model on initial load.</summary>
    [Parameter]
    public string? InitialBody { get; set; }

    /// <summary>Optional endpoint path used together with <see cref="InitialBody"/>.</summary>
    [Parameter]
    public string? InitialEndpointPath { get; set; }

    /// <summary>The current request body (built in simple mode, editable in advanced mode).</summary>
    public string RequestBody { get; set; } = string.Empty;

    /// <summary>The current endpoint path (resolved in simple mode, editable in advanced mode).</summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>Whether the selected schema sends a streaming request.</summary>
    protected bool IsStreaming => ModelCatalog.SchemaIsStreaming(Template.Schema);

    /// <summary>The message to send, falling back to a default when empty.</summary>
    protected string EffectiveMessage =>
        string.IsNullOrWhiteSpace(Message) ? DefaultMessage : Message;

    /// <summary>Builds the schema-specific request payload object for the given message.</summary>
    protected abstract object BuildPayload(string message, int maxTokens);

    /// <summary>Constructs the request (endpoint, body, content type) for the current state.</summary>
    public ModelRequest BuildRequest() => new(EndpointPath, RequestBody, ContentType);

    protected override void OnInitialized()
    {
        if (!string.IsNullOrWhiteSpace(InitialBody))
        {
            RequestBody = InitialBody!;
            EndpointPath = !string.IsNullOrWhiteSpace(InitialEndpointPath)
                ? InitialEndpointPath!
                : ModelCatalog.ResolveEndpointPath(Template);
            _lastTemplateKey = Template.Key;
            _seededVerbatim = true;
        }
    }

    protected override void OnParametersSet()
    {
        var rebuilt = false;

        if (_lastTemplateKey != Template.Key)
        {
            _lastTemplateKey = Template.Key;
            RebuildRequest();
            rebuilt = true;
        }

        // When the user switches into simple mode, refresh the body so the pane fields win,
        // unless we just seeded a verbatim body from the parent on initial load.
        if (SimpleMode && !_lastSimpleMode && !rebuilt && !_seededVerbatim)
        {
            RebuildRequest();
        }

        _lastSimpleMode = SimpleMode;
        _seededVerbatim = false;
    }

    /// <summary>Rebuilds the request body and endpoint path from the current simple-mode fields.</summary>
    protected void RebuildRequest()
    {
        RequestBody = Serialize(BuildPayload(EffectiveMessage, MaxTokens));
        EndpointPath = ModelCatalog.ResolveEndpointPath(Template);
    }

    /// <summary>Serializes a payload object to indented JSON.</summary>
    protected static string Serialize(object payload) =>
        JsonSerializer.Serialize(payload, SerializerOptions);

    /// <summary>Parses an optional double from an input value, returning null when empty/invalid.</summary>
    protected static double? ParseNullableDouble(object? value) =>
        double.TryParse(value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    /// <summary>Parses an optional int from an input value, returning null when empty/invalid.</summary>
    protected static int? ParseNullableInt(object? value) =>
        int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    protected async Task OnMessageInput(ChangeEventArgs args)
    {
        Message = args.Value?.ToString() ?? string.Empty;
        await MessageChanged.InvokeAsync(Message);
        RebuildRequest();
    }

    protected async Task OnMaxTokensInput(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var value) && value > 0)
        {
            MaxTokens = value;
            await MaxTokensChanged.InvokeAsync(MaxTokens);
            RebuildRequest();
        }
    }

    protected async Task OnContentTypeSelected(ChangeEventArgs args)
    {
        ContentType = args.Value?.ToString() ?? ChatTesterHttp.JsonContentType;
        await ContentTypeChanged.InvokeAsync(ContentType);
    }

    protected void OnEndpointPathChanged(string value) => EndpointPath = value;

    protected void OnRequestBodyChanged(string value) => RequestBody = value;

    protected async Task OnContentTypeChangedAsync(string value)
    {
        ContentType = value;
        await ContentTypeChanged.InvokeAsync(value);
    }
}
