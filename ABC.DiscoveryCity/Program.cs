using ABC.DiscoveryCity.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace ABC.DiscoveryCity
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");

            // Configure HttpClient to point to the API (reads from wwwroot/appsettings.json)
            var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5022";
            builder.Services.AddScoped(sp => new HttpClient
            {
                BaseAddress = new Uri(apiBase.TrimEnd('/') + "/")
            });
            
            builder.Services.AddTelerikBlazor();
            
            
            builder.Services.AddScoped<TorrentService>();
            builder.Services.AddScoped<SearchService>();
            
            await builder.Build().RunAsync();
        }
    }
}
