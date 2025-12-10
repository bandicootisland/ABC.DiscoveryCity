using ABC.BookCity.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace ABC.BookCity
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");

            // Configure HttpClient to point to the API
            builder.Services.AddScoped(sp => new HttpClient 
            { 
                BaseAddress = new Uri("http://localhost:5022/") // API base URL
            });
            
            builder.Services.AddTelerikBlazor();
            
            
            builder.Services.AddScoped<TorrentService>();
            
            await builder.Build().RunAsync();
        }
    }
}
