using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace Oxygen.Csharp.Web;

[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class WebRouteAttribute : Attribute
{
    public string Path { get; }
    public string Method { get; }
    public bool RequireAuth { get; }

    public WebRouteAttribute(string path, string method = "POST", bool requireAuth = true)
    {
        Path = string.IsNullOrWhiteSpace(path) ? "/" : path;
        Method = string.IsNullOrWhiteSpace(method) ? "POST" : method.ToUpperInvariant();
        RequireAuth = requireAuth;
    }
}

public static class Http
{
    public static HttpRequest Request(string url) => new(url);
}

public sealed class HttpRequest
{
    private readonly string _url;
    private TimeSpan _timeout = TimeSpan.FromSeconds(10);

    public HttpRequest(string url)
    {
        _url = url;
    }

    public HttpRequest Timeout(int seconds)
    {
        _timeout = TimeSpan.FromSeconds(Math.Max(1, seconds));
        return this;
    }

    public void Get(Action<int, string> callback)
    {
        _ = RunGet(callback);
    }

    private async Task RunGet(Action<int, string> callback)
    {
        try
        {
            using var client = new HttpClient { Timeout = _timeout };
            var resp = await client.GetAsync(_url);
            var text = await resp.Content.ReadAsStringAsync();
            callback((int)resp.StatusCode, text);
        }
        catch
        {
            callback(0, string.Empty);
        }
    }
}
