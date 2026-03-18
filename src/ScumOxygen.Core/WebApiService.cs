using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ScumOxygen.Core;

public sealed class WebApiRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string BodyText { get; init; } = string.Empty;
    public string RemoteIp { get; init; } = string.Empty;
}

public sealed class WebApiService
{
    private sealed record RouteEntry(Func<WebApiRequest, string> Handler, bool RequireAuth);

    private readonly ConcurrentDictionary<string, RouteEntry> _routes = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private Thread? _thread;
    private string _webRoot = string.Empty;
    private string? _apiKey;
    private HashSet<string> _allowedIps = new(StringComparer.OrdinalIgnoreCase);
    private bool _enableCors = true;
    private volatile bool _running;
    private int _listenPort;

    public void Start(string prefix)
    {
        var normalizedPrefix = prefix
            .Replace("http://+:", "http://0.0.0.0:", StringComparison.OrdinalIgnoreCase)
            .Replace("http://*:", "http://0.0.0.0:", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri(normalizedPrefix);
        var host = uri.Host;
        var port = uri.Port;

        if (_running)
        {
            if (_listenPort == port)
                return;

            throw new InvalidOperationException($"Web API already running on port {_listenPort}, cannot switch to {port} without restart.");
        }

        IPAddress bindAddress;
        if (string.Equals(host, "+", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            bindAddress = IPAddress.Any;
        }
        else if (string.Equals(host, "::", StringComparison.OrdinalIgnoreCase))
        {
            bindAddress = IPAddress.IPv6Any;
        }
        else if (!IPAddress.TryParse(host, out bindAddress!))
        {
            bindAddress = IPAddress.Any;
        }

        _listener = new TcpListener(bindAddress, port);
        _listener.Start();
        _listenPort = port;
        _running = true;
        _thread = new Thread(ListenLoop) { IsBackground = true };
        _thread.Start();
    }

    public void StartPluginServer(int port, string token)
    {
        if (port <= 0)
            throw new ArgumentOutOfRangeException(nameof(port));

        Start($"http://+:{port}/");
        if (string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(token))
        {
            ConfigureSecurity(token, _allowedIps);
        }
    }

    public void SetWebRoot(string path)
    {
        _webRoot = path;
    }

    public void ConfigureSecurity(string? apiKey, IEnumerable<string>? allowedIps)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _allowedIps = new HashSet<string>(allowedIps ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public void ConfigureCors(bool enable)
    {
        _enableCors = enable;
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listenPort = 0;
    }

    public void Register(string method, string path, Func<WebApiRequest, string> handler)
    {
        Register(method, path, handler, requireAuth: false);
    }

    public void Register(string method, string path, Func<WebApiRequest, string> handler, bool requireAuth)
    {
        var key = $"{method.ToUpperInvariant()} {path}";
        _routes[key] = new RouteEntry(handler, requireAuth);
    }

    public void Unregister(string method, string path)
    {
        var key = $"{method.ToUpperInvariant()} {path}";
        _routes.TryRemove(key, out _);
    }

    private void ListenLoop()
    {
        while (_running && _listener != null)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch
            {
                if (!_running) break;
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var req = ReadRequest(stream, client);
                if (req == null) return;

                if (req.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    WriteResponse(stream, 204, "text/plain", Array.Empty<byte>());
                    return;
                }

                var routeKey = $"{req.Method.ToUpperInvariant()} {req.Path}";
                if (_routes.TryGetValue(routeKey, out var route))
                {
                    var isApi = req.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
                    if ((isApi || route.RequireAuth) && !IsAuthorized(req))
                    {
                        WriteResponse(stream, 401, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"unauthorized\"}"));
                        return;
                    }

                    var body = route.Handler(req) ?? "{}";
                    WriteResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body));
                    return;
                }

                if (req.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !IsAuthorized(req))
                {
                    WriteResponse(stream, 401, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"unauthorized\"}"));
                    return;
                }

                if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_webRoot))
                {
                    var path = req.Path == "/" ? "/index.html" : req.Path;
                    var fullPath = SafeCombine(_webRoot, path.TrimStart('/'));
                    if (fullPath != null && File.Exists(fullPath))
                    {
                        WriteResponse(stream, 200, GetContentType(fullPath), File.ReadAllBytes(fullPath));
                        return;
                    }
                }

                WriteResponse(stream, 404, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"not_found\"}"));
            }
            catch
            {
                try
                {
                    WriteResponse(stream, 500, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"internal_error\"}"));
                }
                catch
                {
                }
            }
        }
    }

    private static WebApiRequest? ReadRequest(NetworkStream stream, TcpClient client)
    {
        var buffer = new byte[8192];
        using var raw = new MemoryStream();
        var headerEnd = -1;

        while (headerEnd < 0 && raw.Length < 1024 * 1024)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return null;
            raw.Write(buffer, 0, read);
            headerEnd = FindHeaderEnd(raw.GetBuffer(), (int)raw.Length);
        }

        if (headerEnd < 0) return null;

        var data = raw.ToArray();
        var headerText = Encoding.ASCII.GetString(data, 0, headerEnd);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return null;

        var method = requestLine[0].Trim().ToUpperInvariant();
        var target = requestLine[1].Trim();
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = target;
        var qIndex = target.IndexOf('?');
        if (qIndex >= 0)
        {
            path = target.Substring(0, qIndex);
            var queryString = target.Substring(qIndex + 1);
            foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0]);
                var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                query[key] = value;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            headers[key] = value;
        }

        var bodyOffset = headerEnd + 4;
        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLengthText))
        {
            int.TryParse(contentLengthText, out contentLength);
        }

        var bodyBytes = new byte[Math.Max(0, contentLength)];
        var alreadyBuffered = Math.Max(0, data.Length - bodyOffset);
        if (alreadyBuffered > 0)
        {
            Array.Copy(data, bodyOffset, bodyBytes, 0, Math.Min(alreadyBuffered, bodyBytes.Length));
        }

        var remaining = contentLength - alreadyBuffered;
        var position = Math.Max(0, alreadyBuffered);
        while (remaining > 0)
        {
            var read = stream.Read(bodyBytes, position, remaining);
            if (read <= 0) break;
            position += read;
            remaining -= read;
        }

        var bodyText = contentLength > 0 ? Encoding.UTF8.GetString(bodyBytes, 0, Math.Max(0, position)) : string.Empty;
        var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? string.Empty;

        return new WebApiRequest
        {
            Method = method,
            Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
            Query = query,
            Headers = headers,
            BodyText = bodyText,
            RemoteIp = NormalizeIp(remoteIp)
        };
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i <= length - 4; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static string GetContentType(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            _ => "application/octet-stream"
        };
    }

    private void WriteResponse(NetworkStream stream, int statusCode, string contentType, byte[] body)
    {
        var reason = statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            401 => "Unauthorized",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK"
        };

        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
        header.Append($"Content-Type: {contentType}\r\n");
        header.Append($"Content-Length: {body.Length}\r\n");
        header.Append("Connection: close\r\n");
        if (_enableCors)
        {
            header.Append("Access-Control-Allow-Origin: *\r\n");
            header.Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n");
            header.Append("Access-Control-Allow-Headers: Content-Type, X-API-KEY, Authorization\r\n");
        }
        header.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (body.Length > 0)
        {
            stream.Write(body, 0, body.Length);
        }
        stream.Flush();
    }

    private bool IsAuthorized(WebApiRequest req)
    {
        if (_allowedIps.Count > 0 && !IsIpAllowed(req.RemoteIp))
            return false;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return IsLoopback(req.RemoteIp);
        }

        req.Headers.TryGetValue("X-API-KEY", out var provided);
        if (string.IsNullOrWhiteSpace(provided) && req.Headers.TryGetValue("Authorization", out var auth))
        {
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                provided = auth.Substring("Bearer ".Length).Trim();
        }

        return string.Equals(provided, _apiKey, StringComparison.Ordinal);
    }

    private static string? SafeCombine(string root, string relativePath)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(Path.Combine(rootFull, relativePath));
            if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return null;
            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    private bool IsIpAllowed(string ip)
    {
        if (_allowedIps.Count == 0) return true;
        foreach (var allowed in _allowedIps)
        {
            if (string.IsNullOrWhiteSpace(allowed)) continue;
            if (allowed == "*") return true;
            if (allowed.EndsWith("*", StringComparison.Ordinal))
            {
                var prefix = allowed.TrimEnd('*');
                if (ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }
            if (string.Equals(allowed, ip, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsLoopback(string ip)
    {
        return ip == "127.0.0.1" || ip == "::1";
    }

    private static string NormalizeIp(string ip)
    {
        if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
            return ip.Substring("::ffff:".Length);
        return ip;
    }
}
