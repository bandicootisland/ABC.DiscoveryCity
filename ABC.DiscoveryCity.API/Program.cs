using ABC.DiscoveryCity.API.Services;
using Elastic.Clients.Elasticsearch;

//Register Syncfusion license https://help.syncfusion.com/common/essential-studio/licensing/how-to-generate            
//20/4/25, 4 major versions, from 29.x.x
Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense("MzgyMzc3NUAzMjM5MmUzMDJlMzAzYjMzMzMzYkFFWFBEN3orNmlIekpzMmtSMDZXY2RZSnJ6TXZOMDArSjJ3RmFNay9QY0k9");

var builder = WebApplication.CreateBuilder(args);
// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.MaxDepth = 64;
    });

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = int.MaxValue;
    options.MemoryBufferThreshold = int.MaxValue;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100MB
});

builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

builder.Services.AddMemoryCache();

builder.Services.AddHttpClient();

// Elasticsearch
var elasticUrl = builder.Configuration["Elasticsearch:Url"] ?? "http://localhost:9200";
var elasticSettings = new ElasticsearchClientSettings(new Uri(elasticUrl))
    .DefaultIndex("ol_editions");
builder.Services.AddSingleton(new ElasticsearchClient(elasticSettings));



builder.Services.AddSingleton<PdfMetadataService>();
builder.Services.AddSingleton<ABC.PdfProcessing.Syncfusion.PdfMetaDataSyncFusionService>();

// Postgres & Embeddings
builder.Services.AddScoped<ABC.DiscoveryCity.Embeddings.IEmbeddingService, ABC.DiscoveryCity.Embeddings.OllamaEmbeddingService>();

// DbService as scoped — NpgsqlDataSource (connection pool) is cached statically inside DbService
var configConnStartup = builder.Configuration.GetConnectionString("DiscoveryCityDB");
Console.WriteLine($"[API STARTUP] Connection string from config: {configConnStartup ?? "NULL - using default"}");

builder.Services.AddScoped<ABC.DiscoveryCity.PostgreSQL.DbService>(sp =>
{
    var embeddingService = sp.GetRequiredService<ABC.DiscoveryCity.Embeddings.IEmbeddingService>();
    return new ABC.DiscoveryCity.PostgreSQL.DbService(embeddingService, configConnStartup);
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
        });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    //app.UseSwagger();
    //app.UseSwaggerUI();
}

app.UseCors("AllowAll");

app.UseStaticFiles(); // Serve wwwroot (pdf.js viewer, etc.)

app.UseAuthorization();

app.MapControllers();

app.Run();
