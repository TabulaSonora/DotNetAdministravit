using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace TabulaSonora.Web.Services;

/// <summary>What is held in browser storage under one key.</summary>
/// <param name="Key">Storage key.</param>
/// <param name="Name">File name as the user picked it.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="Sha256">SHA-256 as lower-case hex, computed when it was stored.</param>
/// <param name="SavedAt">When it was stored, ISO-8601.</param>
public sealed record StoredAsset(string Key, string Name, long Size, string Sha256, string SavedAt);

/// <summary>
/// The user's own files, kept in IndexedDB so they are supplied once rather than every visit.
/// </summary>
/// <remarks>
/// Nothing Roland's is served by this application; the DLL is the user's, it stays on their machine,
/// and it is never uploaded anywhere. This class is the whole of its lifetime in the browser.
/// </remarks>
public sealed class AssetStore(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>Storage key for <c>SCCore.dll</c>.</summary>
    public const string RomKey = "sccore";

    /// <summary>Storage key for a user-supplied <c>presets.json</c>.</summary>
    public const string PresetsKey = "presets";

    /// <summary>Upper bound on a stream read out of storage; the DLL is 27 MB.</summary>
    private const long MaxAssetBytes = 64L * 1024 * 1024;

    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/asset-store.js");

    /// <summary>Asks the browser not to evict this origin's storage.</summary>
    /// <returns>Whether storage is now persistent.</returns>
    /// <remarks>
    /// Without the grant a 27 MB record is best-effort and can be cleared under disk pressure, which
    /// would silently send the user back to the file picker with no explanation.
    /// </remarks>
    public async ValueTask<bool> RequestPersistenceAsync() =>
        await (await ModuleAsync()).InvokeAsync<bool>("requestPersistence");

    /// <summary>Reads what is stored under a key, without reading its bytes.</summary>
    /// <param name="key">Storage key.</param>
    /// <returns>The record, or <see langword="null"/> if nothing is stored.</returns>
    public async ValueTask<StoredAsset?> DescribeAsync(string key) =>
        await (await ModuleAsync()).InvokeAsync<StoredAsset?>("describe", key);

    /// <summary>Stores the file currently selected in a file input.</summary>
    /// <param name="key">Storage key.</param>
    /// <param name="input">The <c>&lt;input type="file"&gt;</c> element.</param>
    /// <returns>The stored record, or <see langword="null"/> if nothing was selected.</returns>
    /// <remarks>
    /// Takes the element rather than a <c>IBrowserFile</c> on purpose: the bytes go from the picked
    /// file straight into IndexedDB without crossing into .NET at all, which for the DLL is 27 MB of
    /// interop that never has to happen.
    /// </remarks>
    public async ValueTask<StoredAsset?> PutFromInputAsync(string key, ElementReference input) =>
        await (await ModuleAsync()).InvokeAsync<StoredAsset?>("putFromInput", key, input);

    /// <summary>Deletes what is stored under a key.</summary>
    /// <param name="key">Storage key.</param>
    public async ValueTask RemoveAsync(string key) =>
        await (await ModuleAsync()).InvokeVoidAsync("remove", key);

    /// <summary>Reads a stored asset's bytes.</summary>
    /// <param name="key">Storage key.</param>
    /// <returns>The bytes, or <see langword="null"/> if nothing is stored.</returns>
    public async ValueTask<byte[]?> ReadAsync(string key)
    {
        var module = await ModuleAsync();
        var reference = await module.InvokeAsync<IJSStreamReference>("readStream", key);

        await using (reference)
        {
            // Nothing stored under that key comes back as an empty array rather than null, because
            // Blazor dereferences the result on its way to a stream reference.
            if (reference.Length == 0)
            {
                return null;
            }

            await using var stream = await reference.OpenReadStreamAsync(MaxAssetBytes);
            using var buffer = new MemoryStream((int)Math.Min(reference.Length, MaxAssetBytes));
            await stream.CopyToAsync(buffer);

            // The browser side keeps its own reference alive for retries; once the bytes are here
            // that is a second 27 MB nobody needs.
            await module.InvokeVoidAsync("releaseCache", key);
            return buffer.ToArray();
        }
    }

    /// <summary>Reads a stored asset as UTF-8 text.</summary>
    /// <param name="key">Storage key.</param>
    /// <returns>The text, or <see langword="null"/> if nothing is stored.</returns>
    public async ValueTask<string?> ReadTextAsync(string key) =>
        await (await ModuleAsync()).InvokeAsync<string?>("readText", key);

    /// <summary>Releases the JavaScript module.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
            _module = null;
        }
    }
}
