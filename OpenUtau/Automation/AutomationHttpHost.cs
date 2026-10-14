using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenUtau.Core.Automation;

namespace OpenUtau.Automation {
    public sealed class AutomationHttpHost : IAsyncDisposable {
        public const int MaximumRequestBytes = 2 * 1024 * 1024;
        readonly AutomationLifecycleService service;
        readonly string descriptor;
        WebApplication? app;
        AutomationContractValidator? validator;
        public string? Url { get; private set; }
        public AutomationHttpHost(AutomationLifecycleService service) {
            this.service = service;
            descriptor = Path.Combine(service.RuntimeDirectory, service.InstanceId + ".json");
        }
        public async Task StartAsync(CancellationToken token = default) {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions {
                Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production"
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => {
                options.Listen(IPAddress.Loopback, 0);
                options.Limits.MaxRequestBodySize = MaximumRequestBytes;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            });
            var toolsPath = FindToolsFile();
            if (toolsPath != null) validator = new AutomationContractValidator(File.ReadAllText(toolsPath));
            app = builder.Build();
            app.Use(async (context, next) => {
                try {
                    if (context.Request.Headers.ContainsKey("Origin") || context.Request.Host.Host != "127.0.0.1"
                        || context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
                        throw new AutomationException("UNAUTHORIZED", "Only local non-browser clients are accepted.");
                    if (context.Request.ContentLength > MaximumRequestBytes) throw new AutomationException("LIMIT_EXCEEDED", "Request exceeds 2 MiB. Use smaller batches.");
                    await next(context);
                } catch (AutomationException e) {
                    context.Response.StatusCode = e.Code == "UNAUTHORIZED" ? 401 : 200;
                    var error = new System.Collections.Generic.Dictionary<string, object> { ["code"] = e.Code, ["message"] = e.Message };
                    if (e.Details != null) error["details"] = e.Details;
                    await context.Response.WriteAsJsonAsync(new { ok = false, error }, AutomationJson.Options);
                } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (BadHttpRequestException e) {
                    context.Response.StatusCode = e.StatusCode;
                    await context.Response.WriteAsJsonAsync(new { ok = false, error = new { code = "LIMIT_EXCEEDED", message = "Request body is too large or invalid." } }, AutomationJson.Options);
                } catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or System.Collections.Generic.KeyNotFoundException) {
                    await context.Response.WriteAsJsonAsync(new { ok = false, error = new { code = "INVALID_ARGUMENT", message = "Request does not match the automation contract." } }, AutomationJson.Options);
                } catch (Exception) {
                    await context.Response.WriteAsJsonAsync(new { ok = false, error = new { code = "APP_UNAVAILABLE", message = "OpenUtau could not finish this request. Query the operation status before repeating a write." } }, AutomationJson.Options);
                }
            });
            app.MapGet("/automation/v1/health", () => Results.Json(new { ok = true, ready = true, instanceId = service.InstanceId,
                startedAt = service.StartedAt, apiVersion = "1", appVersion = typeof(AutomationHttpHost).Assembly.GetName().Version?.ToString() }));
            app.MapPost("/automation/v1/connect", async (HttpContext context) => {
                if (!AutomationJson.EqualSecret(Bearer(context), service.DiscoveryToken)) throw new AutomationException("UNAUTHORIZED", "Invalid discovery credential.");
                using var body = await ReadBody(context);
                return Results.Json(await service.ConnectAsync(body.RootElement, context.RequestAborted), AutomationJson.Options);
            });
            app.MapPost("/automation/v1/invoke", async (HttpContext context) => {
                var grant = service.Authenticate(Bearer(context));
                using var body = await ReadBody(context);
                var request = body.RootElement;
                if (AutomationJson.Required(request, "apiVersion") != "1") throw new AutomationException("PROTOCOL_MISMATCH", "Automation API version 1 is required.");
                _ = AutomationJson.Required(request, "requestId");
                if (AutomationJson.Required(request, "clientId") != grant.ClientId) throw new AutomationException("UNAUTHORIZED", "clientId does not match the connection grant.");
                var operation = AutomationJson.Required(request, "operation");
                var arguments = request.GetProperty("arguments");
                if (AutomationJson.Text(arguments, "instanceId") is { } instance && instance != service.InstanceId)
                    throw new AutomationException("APP_UNAVAILABLE", "This connection belongs to a different OpenUtau instance.");
                if (validator == null) throw new AutomationException("APP_INITIALIZING", "Install the AI contract package before connecting an MCP client.");
                validator.ValidateToolArguments(operation, arguments);
                return Results.Json(Envelope(await service.InvokeAuthorizedAsync(grant, operation, arguments, context.RequestAborted)), AutomationJson.Options);
            });
            app.MapGet("/automation/v1/operations/{id}", async (HttpContext context, string id) => {
                var grant = service.Authenticate(Bearer(context));
                return Results.Json(Envelope(await service.InvokeAuthorizedAsync(grant, "get_operation", AutomationJson.Element(new { operationId = id }), context.RequestAborted)), AutomationJson.Options);
            });
            app.MapGet("/automation/v1/jobs/{id}", async (HttpContext context, string id) => {
                var grant = service.Authenticate(Bearer(context));
                return Results.Json(Envelope(await service.InvokeAuthorizedAsync(grant, "get_job", AutomationJson.Element(new { jobId = id }), context.RequestAborted)), AutomationJson.Options);
            });
            app.MapPost("/automation/v1/jobs/{id}/cancel", async (HttpContext context, string id) => {
                var grant = service.Authenticate(Bearer(context));
                return Results.Json(await service.InvokeAuthorizedAsync(grant, "cancel_job", AutomationJson.Element(new { jobId = id }), context.RequestAborted), AutomationJson.Options);
            });
            await app.StartAsync(token);
            Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            AutomationPrivateFiles.Write(descriptor, new { instanceId = service.InstanceId, pid = Environment.ProcessId, startedAt = service.StartedAt,
                apiVersion = "1", url = Url, appVersion = typeof(AutomationHttpHost).Assembly.GetName().Version?.ToString(), token = service.DiscoveryToken });
        }
        static string? FindToolsFile() {
            var root = Environment.GetEnvironmentVariable("OPENUTAU_AI_DIR") ?? Path.Combine(AppContext.BaseDirectory, "AI");
            foreach (var relative in new[] { "contracts/automation/v1/tools.json", "contracts/tools.json", "tools.json" }) {
                var path = Path.Combine(root, relative);
                if (File.Exists(path)) return path;
            }
            // Development builds may run directly from bin; no personal checkout path is compiled in.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && directory != null; ++i, directory = directory.Parent) {
                var path = Path.Combine(directory.FullName, "contracts", "automation", "v1", "tools.json");
                if (File.Exists(path)) return path;
            }
            return null;
        }
        static object Envelope(object result) {
            var data = AutomationJson.Element(result);
            if (data.TryGetProperty("ok", out _)) return result;
            var fields = data.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
            fields["ok"] = true;
            return fields;
        }
        static string? Bearer(HttpContext context) {
            var header = context.Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;
        }
        static async Task<JsonDocument> ReadBody(HttpContext context) {
            if (context.Request.ContentType?.Split(';')[0].Trim() != "application/json") throw new AutomationException("INVALID_ARGUMENT", "Content-Type must be application/json.");
            return await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 48 }, context.RequestAborted);
        }
        public async ValueTask DisposeAsync() {
            if (app != null) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
                await app.DisposeAsync(); app = null;
            }
            if (File.Exists(descriptor)) File.Delete(descriptor);
        }
    }
}
