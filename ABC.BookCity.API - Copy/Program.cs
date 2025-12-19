using ABC.BookCity.API.Services;
using Elastic.Clients.Elasticsearch;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

builder.Services.AddMemoryCache();

builder.Services.AddHttpClient();

// Elasticsearch
var elasticUrl = builder.Configuration["Elasticsearch:Url"] ?? "http://localhost:9200";
var elasticSettings = new ElasticsearchClientSettings(new Uri(elasticUrl))
    .DefaultIndex("ol_editions");
builder.Services.AddSingleton(new ElasticsearchClient(elasticSettings));

builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddSingleton<ITorrentService, TorrentService>();
builder.Services.AddSingleton<PdfMetadataService>();

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

app.UseAuthorization();

app.MapControllers();

app.Run();
