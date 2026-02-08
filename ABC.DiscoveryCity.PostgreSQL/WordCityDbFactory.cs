using Microsoft.Extensions.Configuration;

namespace ABC.DiscoveryCity.PostgreSQL;

/// <summary>
/// Factory to create WordCityDb instances from configuration.
/// </summary>
public static class WordCityDbFactory
{
    /// <summary>
    /// Create a WordCityDb instance from appsettings.json in the current directory.
    /// </summary>
    public static WordCityDb Create(string? configPath = null)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(configPath ?? Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        var configuration = builder.Build();
        return Create(configuration);
    }

    /// <summary>
    /// Create a WordCityDb instance from an IConfiguration.
    /// </summary>
    public static WordCityDb Create(IConfiguration configuration)
    {
        var config = new PostgreSQLConfig
        {
            ConnectionString = configuration.GetConnectionString("WordCity")
                ?? throw new InvalidOperationException("ConnectionStrings:WordCity not found in configuration"),
            VectorDimension = configuration.GetValue("PostgreSQL:VectorDimension", 384),
            CommandTimeout = configuration.GetValue("PostgreSQL:CommandTimeout", 300),
            MaxPoolSize = configuration.GetValue("PostgreSQL:MaxPoolSize", 20)
        };

        return new WordCityDb(config);
    }

    /// <summary>
    /// Create a WordCityDb instance from an explicit connection string.
    /// </summary>
    public static WordCityDb Create(string connectionString, int vectorDimension = 384)
    {
        var config = new PostgreSQLConfig
        {
            ConnectionString = connectionString,
            VectorDimension = vectorDimension
        };

        return new WordCityDb(config);
    }
}
