using Microsoft.JSInterop;

namespace EventTicketing.Scanner;

/// <summary>
/// What .NET in the browser can't do itself, from wwwroot/js/door.js: the camera with jsQR,
/// ES256 signatures with WebCrypto (.NET's ECDsa isn't available in WebAssembly), and IndexedDB.
/// </summary>
public sealed class DoorBrowser(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/door.js");

    public async Task<bool> VerifyAsync(string code, IReadOnlyList<TicketKey> keys) =>
        await (await ModuleAsync()).InvokeAsync<bool>("verify", code, keys);

    public async Task<T?> LoadAsync<T>(string key) =>
        await (await ModuleAsync()).InvokeAsync<T?>("load", key);

    public async Task SaveAsync<T>(string key, T value) =>
        await (await ModuleAsync()).InvokeVoidAsync("save", key, value);

    public async Task ForgetAsync() =>
        await (await ModuleAsync()).InvokeVoidAsync("forget");

    public async Task<bool> OnlineAsync() =>
        await (await ModuleAsync()).InvokeAsync<bool>("online");

    public async Task StartCameraAsync<T>(Microsoft.AspNetCore.Components.ElementReference video, DotNetObjectReference<T> page)
        where T : class =>
        await (await ModuleAsync()).InvokeVoidAsync("startCamera", video, page);

    public async Task StopCameraAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("stopCamera");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("stopCamera");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone.
            }
        }
    }
}
