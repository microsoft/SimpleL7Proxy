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

        public string TestEndpoint => _configuration["test_endpoint"];
        public string DurationSeconds => _configuration["duration_seconds"];
        public int Concurrency => int.Parse(_configuration["concurrency"]);
        public string InterrunDelay => _configuration["interrun_delay"];

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
    }

    public class TestConfig
    {
        public string Name { get; set; }
        public string Method { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public string DataFile { get; set; }
    }
}