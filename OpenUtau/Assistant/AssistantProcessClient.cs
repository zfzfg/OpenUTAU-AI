using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUtau.App.Assistant {
    public interface IAssistantProcessClient : IAsyncDisposable {
        event Action<string, JsonElement>? Notification;
        Task StartAsync(string node, string host, long generation, CancellationToken cancellationToken = default);
        Task<JsonElement> RequestAsync(string method, object? arguments, CancellationToken cancellationToken = default);
        Task NotifyAsync(string method, object? arguments, CancellationToken cancellationToken = default);
    }

    /// <summary>Private, bounded NDJSON RPC. Provider stdout never goes into the editor console.</summary>
    public sealed class AssistantProcessClient : IAssistantProcessClient {
        const int MaxFrameBytes = 2 * 1024 * 1024;
        readonly SemaphoreSlim writeGate = new SemaphoreSlim(1);
        readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        Process? process;
        Task? readTask;
        Task? errorTask;
        long nextId;
        long generation;
        bool disposing;
        public event Action<string, JsonElement>? Notification;

        public async Task StartAsync(string node, string host, long generation, CancellationToken cancellationToken = default) {
            if (process != null) { throw new InvalidOperationException("An owned host is already running; dispose it before reconnecting."); }
            this.generation = generation;
            var start = new ProcessStartInfo {
                FileName = node, WorkingDirectory = Path.GetDirectoryName(host)!, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add(host);
            process = Process.Start(start) ?? throw new IOException("Agent host did not start.");
            readTask = ReadAsync(process.StandardOutput.BaseStream, lifetime.Token);
            errorTask = DrainStderrAsync(process.StandardError.BaseStream, lifetime.Token);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await RequestAsync("initialize", new { apiVersion = "1", appVersion = typeof(AssistantProcessClient).Assembly.GetName().Version?.ToString(), processGeneration = generation }, handshake.Token);
            if (!result.TryGetProperty("apiVersion", out var version) || version.GetString() != "1") {
                await DisposeAsync();
                throw new InvalidDataException("Agent host protocol version is incompatible.");
            }
        }

        public async Task<JsonElement> RequestAsync(string method, object? arguments, CancellationToken cancellationToken = default) {
            var id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            try {
                await WriteAsync(new { jsonrpc = "2.0", id, method, @params = arguments ?? new { } }, cancellationToken);
                return await completion.Task;
            } finally { pending.TryRemove(id, out _); }
        }

        public Task NotifyAsync(string method, object? arguments, CancellationToken cancellationToken = default) =>
            WriteAsync(new { jsonrpc = "2.0", method, @params = arguments ?? new { } }, cancellationToken);

        async Task WriteAsync(object frame, CancellationToken cancellationToken) {
            var payload = JsonSerializer.Serialize(frame, AssistantRuntimePaths.Json);
            if (Encoding.UTF8.GetByteCount(payload) > MaxFrameBytes) { throw new InvalidDataException("IPC frame exceeds 2 MiB."); }
            await writeGate.WaitAsync(cancellationToken);
            try {
                if (process == null || process.HasExited) { throw new IOException("Agent host is disconnected."); }
                await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
            } finally { writeGate.Release(); }
        }

        async Task ReadAsync(Stream stream, CancellationToken cancellationToken) {
            Exception? failure = null;
            try {
                var buffer = new byte[8192];
                using var frame = new MemoryStream();
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0) {
                    for (var i = 0; i < count; i++) {
                        if (buffer[i] == '\n') {
                            if (frame.Length > 0) {
                                using var doc = JsonDocument.Parse(frame.ToArray());
                                Receive(doc.RootElement);
                                frame.SetLength(0);
                            }
                        } else {
                            if (frame.Length >= MaxFrameBytes) { throw new InvalidDataException("Oversized agent host frame."); }
                            frame.WriteByte(buffer[i]);
                        }
                    }
                }
                if (!disposing) { failure = new EndOfStreamException("Agent host closed its output."); }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { failure = ex; }
            finally {
                var error = failure ?? new IOException("Agent host disconnected.");
                foreach (var completion in pending.Values) { completion.TrySetException(error); }
                if (!disposing) {
                    Notification?.Invoke("processExited", JsonSerializer.SerializeToElement(new { processGeneration = generation, code = "HOST_EOF", message = error.Message }, AssistantRuntimePaths.Json));
                }
            }
        }

        void Receive(JsonElement frame) {
            if (!frame.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0") { throw new InvalidDataException("Malformed host RPC frame."); }
            if (frame.TryGetProperty("id", out var id) && id.TryGetInt64(out var number)) {
                if (!pending.TryGetValue(number, out var completion)) { return; }
                if (frame.TryGetProperty("error", out var error)) {
                    var message = error.TryGetProperty("message", out var value) ? value.GetString() : "Agent request failed.";
                    completion.TrySetException(new AssistantRpcException(message ?? "Agent request failed.", error.Clone()));
                } else if (frame.TryGetProperty("result", out var result)) { completion.TrySetResult(result.Clone()); }
                else { completion.TrySetException(new InvalidDataException("RPC response has no result or error.")); }
            } else if (frame.TryGetProperty("method", out var method) && frame.TryGetProperty("params", out var arguments)) {
                Notification?.Invoke(method.GetString() ?? "", arguments.Clone());
            } else { throw new InvalidDataException("Unexpected host RPC request."); }
        }

        static async Task DrainStderrAsync(Stream stream, CancellationToken token) {
            // Do not persist CLI diagnostics: they can contain transcripts or credentials.
            var buffer = new byte[4096];
            try { while (await stream.ReadAsync(buffer, token) > 0) { } }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync() {
            if (disposing) { return; }
            disposing = true;
            if (process != null) {
                try {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    if (!process.HasExited) {
                        await RequestAsync("shutdown", new { }, timeout.Token);
                        process.StandardInput.Close();
                        await process.WaitForExitAsync(timeout.Token);
                    }
                } catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is InvalidOperationException || ex is AssistantRpcException) {
                    if (!process.HasExited) { process.Kill(true); }
                }
                // Do not report ownership released before the OS confirms exit.
                if (!process.HasExited) { process.Kill(true); }
                await process.WaitForExitAsync();
            }
            lifetime.Cancel();
            if (readTask != null) { await readTask; }
            if (errorTask != null) { await errorTask; }
            process?.Dispose();
            process = null;
            lifetime.Dispose();
            writeGate.Dispose();
        }
    }

    public sealed class AssistantRpcException : Exception {
        public JsonElement Error { get; }
        public AssistantRpcException(string message, JsonElement error) : base(message) { Error = error; }
    }
}
