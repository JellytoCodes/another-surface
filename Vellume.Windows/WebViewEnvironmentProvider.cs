using System.Collections.Concurrent;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Vellume.Windows;

internal static class WebViewEnvironmentProvider
{
    private static readonly ConcurrentDictionary<
        string,
        Lazy<Task<CoreWebView2Environment>>> Environments =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<CoreWebView2Environment> GetAsync(
        string userDataDirectory)
    {
        var normalizedDirectory = Path.GetFullPath(userDataDirectory);
        var lazyEnvironment = Environments.GetOrAdd(
            normalizedDirectory,
            directory => new Lazy<Task<CoreWebView2Environment>>(
                () => CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: directory),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazyEnvironment.Value;
        }
        catch
        {
            Environments.TryRemove(
                new KeyValuePair<
                    string,
                    Lazy<Task<CoreWebView2Environment>>>(
                    normalizedDirectory,
                    lazyEnvironment));
            throw;
        }
    }
}
