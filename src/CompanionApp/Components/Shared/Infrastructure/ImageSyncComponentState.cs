using System.Globalization;
using System.Text.RegularExpressions;

namespace CompanionApp.Components.Shared;

public sealed class ImageSyncComponentState(string name, string repositoryName)
{
    private static readonly Regex TagPattern = new("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(
        @"^v?(?<version>\d+(?:\.\d+){1,3})(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private IReadOnlyDictionary<string, string> _sourceDigests = new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _targetDigests = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Name { get; } = name;
    public string RepositoryName { get; } = repositoryName;
    public IReadOnlyList<string> AvailableVersions { get; private set; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> SourceDigests => _sourceDigests;
    public IReadOnlyDictionary<string, string> TargetDigests => _targetDigests;
    public string CurrentVersion { get; private set; } = string.Empty;
    public string? SelectedVersion { get; set; }
    public bool SelectedForSynchronization { get; set; }
    public string? SynchronizationStatus { get; set; }
    public string? Error { get; set; }
    public bool SelectionWasReset { get; private set; }

    public bool NeedsSynchronization
    {
        get
        {
            if (SelectedVersion is null)
            {
                return false;
            }

            if (!VersionsMatch(SelectedVersion, CurrentVersion))
            {
                return true;
            }

            return !_sourceDigests.TryGetValue(SelectedVersion, out var selectedDigest)
                || !_targetDigests.TryGetValue(CurrentVersion, out var currentDigest)
                || !string.Equals(selectedDigest, currentDigest, StringComparison.Ordinal);
        }
    }

    public string? GetSynchronizationVersion() =>
        SelectedForSynchronization && NeedsSynchronization ? SelectedVersion : null;

    public void Refresh(
        IReadOnlyDictionary<string, string> sourceDigests,
        IReadOnlyDictionary<string, string> targetDigests,
        string? configuredCurrentVersion)
    {
        var previousSelection = SelectedVersion;
        AvailableVersions = sourceDigests.Keys
            .Where(tag => TagPattern.IsMatch(tag))
            .OrderByDescending(VersionSortKey)
            .ThenByDescending(version => version, StringComparer.Ordinal)
            .ToArray();
        _sourceDigests = sourceDigests;
        _targetDigests = targetDigests;
        Error = null;
        SelectionWasReset = false;

        var latest = AvailableVersions.FirstOrDefault();
        var configured = string.IsNullOrWhiteSpace(configuredCurrentVersion)
            ? null
            : FindVersion(configuredCurrentVersion, AvailableVersions);
        CurrentVersion = configured
            ?? (string.IsNullOrWhiteSpace(configuredCurrentVersion) ? latest : FormatVersion(configuredCurrentVersion));

        var retained = previousSelection is null ? null : FindExactVersion(previousSelection, AvailableVersions);
        if (retained is not null)
        {
            SelectedVersion = retained;
        }
        else
        {
            SelectedVersion = FindVersion(CurrentVersion, AvailableVersions) ?? latest;
            SelectionWasReset = previousSelection is not null;
        }
    }

    public static IReadOnlyDictionary<string, string> ParseCurrentVersions(string? value)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return versions;
        }

        foreach (var entry in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                continue;
            }

            var component = entry[..separator].Trim();
            var version = entry[(separator + 1)..].Trim();
            if (component.Length > 0 && version.Length > 0)
            {
                versions[component] = version;
            }
        }

        return versions;
    }

    public static bool VersionsMatch(string? left, string? right) =>
        string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);

    public static bool IsNewerVersion(string candidate, string current)
    {
        var candidateMatch = VersionPattern.Match(candidate);
        var currentMatch = VersionPattern.Match(current);
        return candidateMatch.Success
            && currentMatch.Success
            && Version.TryParse(candidateMatch.Groups["version"].Value, out var candidateVersion)
            && Version.TryParse(currentMatch.Groups["version"].Value, out var currentVersion)
            && CompareVersions(candidateVersion, currentVersion) > 0;
    }

    private static string? FindExactVersion(string version, IEnumerable<string> versions) =>
        versions.FirstOrDefault(available => string.Equals(available, version, StringComparison.Ordinal));

    private static string? FindVersion(string version, IEnumerable<string> versions) =>
        versions.FirstOrDefault(available => VersionsMatch(available, version));

    private static string FormatVersion(string version) =>
        version.StartsWith('v') || version.StartsWith('V') ? version : $"v{version}";

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var value = version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
        var match = VersionPattern.Match(value);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsed))
        {
            return value;
        }

        return $"{parsed.Major}.{Math.Max(0, parsed.Minor)}.{Math.Max(0, parsed.Build)}.{Math.Max(0, parsed.Revision)}";
    }

    private static Version VersionSortKey(string version)
    {
        var match = VersionPattern.Match(version);
        if (match.Success && Version.TryParse(match.Groups["version"].Value, out var parsed))
        {
            return parsed;
        }

        return new Version(0, 0);
    }

    private static int CompareVersions(Version left, Version right)
    {
        var comparison = left.Major.CompareTo(right.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Math.Max(0, left.Minor).CompareTo(Math.Max(0, right.Minor));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Math.Max(0, left.Build).CompareTo(Math.Max(0, right.Build));
        return comparison != 0
            ? comparison
            : Math.Max(0, left.Revision).CompareTo(Math.Max(0, right.Revision));
    }
}
