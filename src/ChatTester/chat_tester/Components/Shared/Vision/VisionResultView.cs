using System.Globalization;

namespace chat_tester.Components.Shared;

public sealed class VisionResultView
{
    public string SummaryText { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public bool HasCaptionResult { get; set; }
    public bool HasDenseCaptionsResult { get; set; }
    public bool HasTagsResult { get; set; }
    public bool HasObjectsResult { get; set; }
    public bool HasPeopleResult { get; set; }
    public bool HasReadResult { get; set; }
    public bool HasMetadataResult { get; set; }
    public VisionCaptionItem? Caption { get; set; }
    public List<VisionCaptionItem> DenseCaptions { get; } = new();
    public List<VisionTagItem> Tags { get; } = new();
    public List<VisionDetectionItem> Objects { get; } = new();
    public List<VisionDetectionItem> People { get; } = new();
    public List<string> ReadLines { get; } = new();
    public VisionImageMetadata Metadata { get; set; } = new();

    public bool HasAnyVisionResult =>
        HasCaptionResult
        || HasDenseCaptionsResult
        || HasTagsResult
        || HasObjectsResult
        || HasPeopleResult
        || HasReadResult
        || HasMetadataResult
        || Metadata.HasDimensions;
}

public sealed class VisionCaptionItem
{
    public string Text { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public VisionBoundingBox Box { get; set; } = new();
}

public sealed class VisionTagItem
{
    public string Name { get; set; } = string.Empty;
    public double? Confidence { get; set; }
}

public sealed class VisionDetectionItem
{
    public string Label { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public VisionBoundingBox Box { get; set; } = new();
}

public sealed class VisionBoundingBox
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool HasValue => X.HasValue || Y.HasValue || Width.HasValue || Height.HasValue;
    public string DisplayText => HasValue
        ? $"x {FormatOptional(X)}, y {FormatOptional(Y)}, w {FormatOptional(Width)}, h {FormatOptional(Height)}"
        : "-";

    private static string FormatOptional(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}

public sealed class VisionImageMetadata
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool HasDimensions => Width.HasValue || Height.HasValue;
}