using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

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

        public bool needsToken() {
            // check if all of the required fields are present
            return EntraAudience != null && EntraClientID != null && EntraSecret != null && EntraTenantID != null;
        }
    }

    public class TestConfig
    {
        public string Name { get; set; } = "";
        public string Method { get; set; } = "";
        public string Path { get; set; } = ""; 
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string DataFile { get; set; } = "";
    }

}