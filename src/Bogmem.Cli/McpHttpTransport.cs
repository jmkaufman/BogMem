using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogmem.Slices.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bogmem.Cli;

/// <summary>
/// Configuration for the stateless MCP Streamable HTTP transport.
/// </summary>
public sealed record McpHttpOptions
{
    public const string DefaultListenUrl = "http://127.0.0.1:7079";
    public const string DefaultEndpoint = "/mcp";
    public const int DefaultMaxRequestBodyBytes = 16 * 1024 * 1024;

    public McpHttpOptions(
        string? listenUrl = null,
        string? endpoint = null,
        string? bearerToken = null,
        IEnumerable<string>? allowedOrigins = null,
        int maxRequestBodyBytes = DefaultMaxRequestBodyBytes)
    {
        ListenUri = ValidateListenUri(listenUrl ?? DefaultListenUrl);
        Endpoint = ValidateEndpoint(endpoint ?? DefaultEndpoint);
        BearerToken = string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken.Trim();
        AllowedOrigins = (allowedOrigins ?? [])
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(NormalizeOrigin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        MaxRequestBodyBytes = maxRequestBodyBytes > 0
            ? maxRequestBodyBytes
            : throw new ArgumentOutOfRangeException(
                nameof(maxRequestBodyBytes),
                "The maximum request body size must be positive.");

        if (!ListenUri.IsLoopback && BearerToken is null)
            throw new ArgumentException(
                "A bearer token is required when the MCP HTTP server listens beyond loopback. " +
                "Set --token or BOGMEM_MCP_TOKEN.");
    }

    public Uri ListenUri { get; }
    public string Endpoint { get; }
    public string? BearerToken { get; }
    public IReadOnlySet<string> AllowedOrigins { get; }
    public int MaxRequestBodyBytes { get; }

    private static Uri ValidateListenUri(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(
                "--listen must be an absolute HTTP(S) origin such as http://127.0.0.1:7079.");
        return uri;
    }

    private static string ValidateEndpoint(string raw)
    {
        var endpoint = raw.Trim();
        if (!endpoint.StartsWith('/') ||
            endpoint.Length == 1 ||
            endpoint.EndsWith('/') ||
            endpoint.Contains("//", StringComparison.Ordinal) ||
            endpoint.Any(char.IsWhiteSpace) ||
            endpoint.Contains('?') ||
            endpoint.Contains('#') ||
            string.Equals(endpoint, "/healthz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "--endpoint must be a path such as /mcp and cannot be /healthz.");
        return endpoint;
    }

    private static string NormalizeOrigin(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(
                $"Allowed origin '{raw}' must be an HTTP(S) origin without a path.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}

/// <summary>
/// A runnable MCP Streamable HTTP host. BogMem returns JSON responses and does
/// not open an SSE stream because its MCP server never initiates messages.
/// </summary>
public sealed class McpHttpHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    internal McpHttpHost(WebApplication application) => _application = application;

    public IReadOnlyCollection<string> Addresses => _application.Urls.ToArray();

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _application.StartAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _application.StartAsync(cancellationToken);
        await _application.WaitForShutdownAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _application.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _application.DisposeAsync();
}

/// <summary>
/// Hosts the existing <see cref="McpServer"/> dispatcher over finalized
/// Streamable HTTP (protocol revisions 2025-03-26 through 2025-11-25).
/// </summary>
public static class McpHttpTransport
{
    private static readonly HashSet<string> HttpProtocolVersions =
    [
        "2025-11-25",
        "2025-06-18",
        "2025-03-26",
    ];

    public static McpHttpHost Create(McpServer server, McpHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(McpHttpTransport).Assembly.FullName,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(options.ListenUri.ToString());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
        });

        var app = builder.Build();

        app.MapGet("/healthz", async context =>
        {
            if (!ValidateOrigin(context, options))
                return;
            AddCorsHeaders(context, options);
            if (!ValidateAuthorization(context, options))
                return;

            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new JsonObject
                {
                    ["status"] = "ok",
                    ["service"] = "bogmem-mcp",
                    ["transport"] = "streamable-http",
                    ["endpoint"] = options.Endpoint,
                    ["readOnly"] = server.ReadOnly,
                });
        });

        app.MapMethods(options.Endpoint, ["OPTIONS"], context =>
        {
            if (!ValidateOrigin(context, options))
                return Task.CompletedTask;

            AddCorsHeaders(context, options);
            context.Response.Headers.Allow = "POST, GET, DELETE, OPTIONS";
            context.Response.Headers.AccessControlAllowMethods = "POST, GET, DELETE, OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders =
                "Accept, Authorization, Content-Type, MCP-Protocol-Version, MCP-Session-Id";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        app.MapMethods(options.Endpoint, ["GET", "DELETE"], async context =>
        {
            if (!ValidateOrigin(context, options))
                return;
            AddCorsHeaders(context, options);
            if (!ValidateAuthorization(context, options))
                return;
            if (!await ValidateProtocolVersionAsync(context))
                return;

            context.Response.Headers.Allow = "POST";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        });

        app.MapPost(options.Endpoint, async context =>
        {
            if (!ValidateOrigin(context, options))
                return;
            AddCorsHeaders(context, options);
            if (!ValidateAuthorization(context, options))
                return;

            context.Response.Headers.CacheControl = "no-store";

            if (!HasJsonContentType(context.Request.ContentType))
            {
                await WriteTransportErrorAsync(
                    context,
                    StatusCodes.Status415UnsupportedMediaType,
                    -32600,
                    "Content-Type must be application/json.");
                return;
            }

            if (!Accepts(context, "application/json") ||
                !Accepts(context, "text/event-stream"))
            {
                await WriteTransportErrorAsync(
                    context,
                    StatusCodes.Status406NotAcceptable,
                    -32600,
                    "Accept must include application/json and text/event-stream.");
                return;
            }

            if (!await ValidateProtocolVersionAsync(context))
                return;

            JsonNode? request;
            try
            {
                request = await JsonNode.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted);
            }
            catch (JsonException ex)
            {
                await WriteTransportErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    -32700,
                    "Parse error",
                    ex.Message);
                return;
            }

            if (request is JsonObject responseMessage &&
                !responseMessage.ContainsKey("method") &&
                (responseMessage.ContainsKey("result") || responseMessage.ContainsKey("error")))
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            JsonNode? response;
            try
            {
                response = server.HandleRequest(request);
            }
            catch
            {
                await WriteTransportErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    McpServer.ErrorInternal,
                    "Internal server error");
                return;
            }
            if (response is null)
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            await WriteJsonAsync(context, StatusCodes.Status200OK, response);
        });

        return new McpHttpHost(app);
    }

    private static async Task<bool> ValidateProtocolVersionAsync(HttpContext context)
    {
        var protocolVersion = context.Request.Headers["MCP-Protocol-Version"].ToString();
        if (string.IsNullOrWhiteSpace(protocolVersion) ||
            HttpProtocolVersions.Contains(protocolVersion))
            return true;

        await WriteTransportErrorAsync(
            context,
            StatusCodes.Status400BadRequest,
            -32600,
            $"Unsupported MCP-Protocol-Version: {protocolVersion}");
        return false;
    }

    private static bool ValidateOrigin(HttpContext context, McpHttpOptions options)
    {
        var values = context.Request.Headers.Origin;
        if (values.Count == 0)
            return true;

        if (values.Count != 1 ||
            !TryNormalizeRequestOrigin(values[0], out var origin) ||
            !options.AllowedOrigins.Contains(origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        return true;
    }

    private static bool ValidateAuthorization(HttpContext context, McpHttpOptions options)
    {
        if (options.BearerToken is null)
            return true;

        if (!AuthenticationHeaderValue.TryParse(
                context.Request.Headers.Authorization.ToString(),
                out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(authorization.Parameter) ||
            !FixedTimeEquals(options.BearerToken, authorization.Parameter))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        return true;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    private static bool TryNormalizeRequestOrigin(string? raw, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static bool HasJsonContentType(string? raw)
    {
        return MediaTypeHeaderValue.TryParse(raw, out var contentType) &&
               string.Equals(
                   contentType.MediaType,
                   "application/json",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool Accepts(HttpContext context, string mediaType)
    {
        foreach (var raw in context.Request.Headers.Accept)
        {
            if (raw is null)
                continue;
            foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!MediaTypeWithQualityHeaderValue.TryParse(part, out var accepted) ||
                    accepted.Quality == 0)
                    continue;
                var acceptedMediaType = accepted.MediaType;
                if (acceptedMediaType == "*/*" ||
                    string.Equals(acceptedMediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (acceptedMediaType is not null &&
                    acceptedMediaType.EndsWith("/*", StringComparison.Ordinal) &&
                    mediaType.StartsWith(
                        acceptedMediaType[..^1],
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static void AddCorsHeaders(HttpContext context, McpHttpOptions options)
    {
        if (context.Request.Headers.Origin.Count != 1 ||
            !TryNormalizeRequestOrigin(context.Request.Headers.Origin[0], out var origin) ||
            !options.AllowedOrigins.Contains(origin))
            return;

        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.Vary = "Origin";
    }

    private static Task WriteTransportErrorAsync(
        HttpContext context,
        int statusCode,
        int errorCode,
        string message,
        string? data = null)
    {
        var error = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject
            {
                ["code"] = errorCode,
                ["message"] = message,
            },
        };
        if (data is not null)
            error["error"]!["data"] = data;
        return WriteJsonAsync(context, statusCode, error);
    }

    private static async Task WriteJsonAsync(
        HttpContext context,
        int statusCode,
        JsonNode body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            body.ToJsonString(),
            context.RequestAborted);
    }
}
