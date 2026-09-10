using Azure;
using Azure.Core.Diagnostics;
using Azure.Data.AppConfiguration;
using Azure.Identity;
using System.Diagnostics;
using System.Diagnostics.Tracing;

const string defaultEndpoint = "https://nvm2-tc26-appcfg.azconfig.io";
const string defaultLabel = "ACA-L7-2";

var endpoint = GetOptionValue(args, "--endpoint") ?? defaultEndpoint;
var label = GetOptionValue(args, "--label") ?? defaultLabel;
var allLabels = args.Contains("--all-labels", StringComparer.OrdinalIgnoreCase);
var showValues = args.Contains("--show-values", StringComparer.OrdinalIgnoreCase);

if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
	|| endpointUri.Scheme != Uri.UriSchemeHttps)
{
	Console.Error.WriteLine($"Invalid App Configuration endpoint: {endpoint}");
	return 1;
}

var clientOptions = new ConfigurationClientOptions
{
	Retry =
	{
		MaxRetries = 0,
		NetworkTimeout = TimeSpan.FromSeconds(10),
	},
};

using var identityListener = AzureEventSourceListener.CreateConsoleLogger(EventLevel.Informational);
var credential = new DefaultAzureCredential();
var client = new ConfigurationClient(endpointUri, credential, clientOptions);
var selector = new SettingSelector
{
	KeyFilter = "*",
	LabelFilter = allLabels ? null : label,
};

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var stopwatch = Stopwatch.StartNew();
var count = 0;
long? firstItemMilliseconds = null;

Console.WriteLine($"Endpoint: {endpointUri}");
Console.WriteLine($"Label:    {(allLabels ? "<all labels>" : label)}");
Console.WriteLine("Credential: DefaultAzureCredential");
Console.WriteLine("Downloading configuration...");

try
{
	await foreach (var setting in client.GetConfigurationSettingsAsync(selector, timeout.Token))
	{
		firstItemMilliseconds ??= stopwatch.ElapsedMilliseconds;
		count++;

		var displayedValue = showValues ? setting.Value ?? string.Empty : "<masked>";
		Console.WriteLine($"{setting.Key} = {displayedValue}");
	}

	stopwatch.Stop();
	Console.WriteLine();
	Console.WriteLine($"Downloaded {count} settings.");
	Console.WriteLine($"Time to first item: {firstItemMilliseconds?.ToString() ?? "no items"} ms");
	Console.WriteLine($"Total time:         {stopwatch.ElapsedMilliseconds} ms");
	return 0;
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
	Console.Error.WriteLine($"Timed out after {stopwatch.Elapsed.TotalSeconds:0.0} seconds after receiving {count} settings.");
	return 2;
}
catch (AuthenticationFailedException exception)
{
	Console.Error.WriteLine($"Authentication failed after {stopwatch.Elapsed.TotalSeconds:0.0} seconds: {exception.Message}");
	return 3;
}
catch (RequestFailedException exception)
{
	Console.Error.WriteLine($"App Configuration failed after {stopwatch.Elapsed.TotalSeconds:0.0} seconds ({exception.Status}): {exception.Message}");
	return 4;
}

static string? GetOptionValue(string[] arguments, string option)
{
	var optionIndex = Array.FindIndex(arguments, argument =>
		string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
	return optionIndex >= 0 && optionIndex + 1 < arguments.Length
		? arguments[optionIndex + 1]
		: null;
}
