using Microsoft.JSInterop;

namespace ABC.DiscoveryCity.Services;

public class ThemeService
{
    private readonly IJSRuntime _js;
    private string _currentThemeKey = "default-main";

    public ThemeService(IJSRuntime js) => _js = js;

    public string CurrentThemeKey => _currentThemeKey;

    public event Action? OnThemeChanged;

    public static readonly List<ThemeOption> Themes = new()
    {
        // Light
        new("Default",        "default-main",        "_content/Telerik.UI.for.Blazor/css/kendo-theme-default/all.css"),
        new("Ocean Blue",     "default-ocean-blue",  "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-default/swatches/default-ocean-blue.css"),
        new("Nordic",         "default-nordic",      "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-default/swatches/default-nordic.css"),
        new("Purple",         "default-purple",      "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-default/swatches/default-purple.css"),
        new("Bootstrap",      "bootstrap-main",      "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-bootstrap/swatches/bootstrap-main.css"),
        new("Urban",          "bootstrap-urban",     "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-bootstrap/swatches/bootstrap-urban.css"),
        new("Material",       "material-main",       "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-material/swatches/material-main.css"),
        new("Fluent",         "fluent-main",         "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-fluent/swatches/fluent-main.css"),
        // Dark
        new("Default Dark",   "default-dark",        "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-default/swatches/default-main-dark.css"),
        new("Bootstrap Dark", "bootstrap-dark",      "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-bootstrap/swatches/bootstrap-main-dark.css"),
        new("Material Dark",  "material-dark",       "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-material/swatches/material-main-dark.css"),
        new("Fluent Dark",    "fluent-dark",         "https://blazor.cdn.telerik.com/blazor/12.0.0/kendo-theme-fluent/swatches/fluent-main-dark.css"),
    };

    public async Task InitAsync()
    {
        try
        {
            var saved = await _js.InvokeAsync<string?>("localStorage.getItem", "dc-theme-key");
            if (!string.IsNullOrEmpty(saved) && Themes.Any(t => t.Key == saved))
                _currentThemeKey = saved;
        }
        catch { /* pre-render or SSR — ignore */ }
    }

    public async Task SetThemeAsync(string key)
    {
        var theme = Themes.FirstOrDefault(t => t.Key == key);
        if (theme == null) return;

        _currentThemeKey = key;
        await _js.InvokeVoidAsync("themeChanger.change", theme.Url);
        await _js.InvokeVoidAsync("localStorage.setItem", "dc-theme-key", key);
        await _js.InvokeVoidAsync("localStorage.setItem", "dc-theme-url", theme.Url);
        OnThemeChanged?.Invoke();
    }
}

public record ThemeOption(string Name, string Key, string Url);
