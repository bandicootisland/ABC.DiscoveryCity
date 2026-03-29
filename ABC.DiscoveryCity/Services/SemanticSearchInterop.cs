using Microsoft.JSInterop;

namespace ABC.DiscoveryCity.Services;

/// <summary>
/// Blazor JS interop for client-side semantic search.
/// Wraps Transformers.js (mxbai-embed-large-v1 ONNX) + PQ codebook quantization
/// running entirely in the browser.
/// </summary>
public class SemanticSearchInterop
{
    private readonly IJSRuntime _js;
    private bool _initialized;
    private bool _initializing;

    public SemanticSearchInterop(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>
    /// Initialize the ONNX model and PQ codebook. First call downloads ~330MB model (cached by browser).
    /// Safe to call multiple times.
    /// </summary>
    public async Task InitAsync(string codebookUrl = "data/sq_codebook.bin")
    {
        if (_initialized) return;
        if (_initializing) return;

        _initializing = true;
        try
        {
            await _js.InvokeVoidAsync("semanticSearch.init", codebookUrl);
            _initialized = true;
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>True when both model and codebook are loaded and ready.</summary>
    public async Task<bool> IsReadyAsync()
    {
        if (_initialized) return true;
        try
        {
            return await _js.InvokeAsync<bool>("semanticSearch.isReady");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Embed query text in the browser and quantize to a SemanticId GUID.
    /// Returns the same GUID that the server-side SentenceQuantizer would produce.
    /// </summary>
    public async Task<Guid> GetSemanticGuidAsync(string queryText)
    {
        if (!_initialized)
            await InitAsync();

        var guidString = await _js.InvokeAsync<string>("semanticSearch.getSemanticGuid", queryText);
        return Guid.Parse(guidString);
    }
}
