using System.Globalization;

namespace chat_tester.Components.Shared;

public static class VisionResultFormatter
{
    public static string Optional(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    public static string Optional(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    public static string Confidence(double? value) => value is null ? "-" : $"{Math.Clamp(value.Value, 0, 1):P1}";

    public static string ConfidencePercent(double? value) => value is null
        ? "0%"
        : $"{Math.Clamp(value.Value, 0, 1) * 100:0.#}%";
}