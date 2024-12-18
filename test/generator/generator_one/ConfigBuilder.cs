using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace test.generator.config
{
    public class ConfigBuilder
    {
        private readonly IConfiguration _configuration;

        public ConfigBuilder(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string TestEndpoint => _configuration?["test_endpoint"] ?? "Undefined";
        public string DurationSeconds => _configuration?["duration_seconds"] ?? "Undefined";
        public int Concurrency => int.Parse(_configuration["concurrency"]);
        public string InterrunDelay => _configuration?["interrun_delay"] ?? "0";

        public string EntraAudience => _configuration?["Entra_audience"] ?? "Undefined";
        public string EntraClientID => _configuration?["Entra_clientID"] ?? "Undefined";
        public string EntraSecret => _configuration?["Entra_secret"] ?? "Undefined";
        public string EntraTenantID => _configuration?["Entra_tenantID"] ?? "Undefined";


        public IEnumerable<TestConfig> Tests
        {
            get
            {
                var testsSection = _configuration.GetSection("tests");
                var tests = new List<TestConfig>();
                testsSection.Bind(tests);
                return tests;
            }
        }

        public bool needsToken()
        {
            // Check if any of the required fields are missing or have the value "Undefined"
            return !string.IsNullOrEmpty(EntraAudience) && EntraAudience != "Undefined" &&
                   !string.IsNullOrEmpty(EntraClientID) && EntraClientID != "Undefined" &&
                   !string.IsNullOrEmpty(EntraSecret) && EntraSecret != "Undefined" &&
                   !string.IsNullOrEmpty(EntraTenantID) && EntraTenantID != "Undefined";
        }

        
        // format:   nnn[ms|s|m|h]
        // examples: 100ms, 1s, 1m, 1h
        public TimeSpan? parseTimeout(string timeout)
        {
            if (string.IsNullOrEmpty(timeout))
            {
                return null;
            }
            // Get the timeout, if defined, parse it to a datetimeoffset
            //var timeout = _configuration?["timeout"];
            var match = Regex.Match(timeout, @"^(?<value>\d+)(?<unit>[a-z]+)$");
            if (match.Success)
            {
                var value = int.Parse(match.Groups["value"].Value);
                var unit = match.Groups["unit"].Value;

                return unit switch
                {
                    "ms" => TimeSpan.FromMilliseconds(value),
                    "s" => TimeSpan.FromSeconds(value),
                    "m" => TimeSpan.FromMinutes(value),
                    "h" => TimeSpan.FromHours(value),
                    _ => throw new ArgumentException($"Invalid time unit in timeout: {unit}")
                };
            }
            else
            {
                throw new ArgumentException($"Invalid timeout format: {timeout}");
            }

            return null;
        }

    }

    public class TestConfig
    {
        public string Name { get; set; } = "";
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string DataFile { get; set; } = "";
        public string Timeout { get; set; } = "";
    }

}