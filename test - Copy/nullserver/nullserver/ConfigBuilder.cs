using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace test.nullserver.config
{
    public class ConfigBuilder
    {
        private readonly IConfiguration _configuration;

        public ConfigBuilder(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string Port => _configuration["port"];
        public string ResponseDelay => _configuration["response_delay"];

    }
}