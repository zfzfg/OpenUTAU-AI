using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Automation;
using OpenUtau.Core;
using OpenUtau.Core.Automation;
using ReactiveUI.Builder;
using Xunit;

namespace OpenUtau.Test {
    [Collection("Editor automation")]
    public sealed class AutomationTransportTest : IDisposable {
        readonly Thread previousUiThread = OpenUtau.Core.Util.ThreadGuard.UiThreadForTest;
        public void Dispose() => OpenUtau.Core.Util.ThreadGuard.SetUiThread(previousUiThread);
        static readonly Lazy<bool> reactive = new(() => { RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp(); return true; });
        static readonly string testRuntime = Path.Combine(Path.GetTempPath(), "OpenUtau API tests " + Guid.NewGuid().ToString("N"), "automation");
        sealed class TestDispatcher(DocManager doc) : IAutomationDispatcher {
            readonly object gate = new();
            public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default) {
                lock (gate) { cancellationToken.ThrowIfCancellationRequested(); doc.SetMainThreadForTest(Thread.CurrentThread); return Task.FromResult(action()); }
            }
        }
        static DocManager NewDoc() {
            _ = reactive.Value;
            Environment.SetEnvironmentVariable("OPENUTAU_AUTOMATION_DIR", testRuntime);
            var doc = (DocManager)Activator.CreateInstance(typeof(DocManager), nonPublic: true)!;
            doc.SetMainThreadForTest(Thread.CurrentThread);
            return doc;
        }
        static JsonElement Json(object value) => AutomationJson.Element(value);
        static async Task<JsonElement> Send(HttpClient client, string path, object payload, string? credential = null, string? origin = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(payload, AutomationJson.Options)) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (credential != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            if (origin != null) request.Headers.Add("Origin", origin);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
        }
        static object Invoke(string operation, object args, string clientId = "test", string version = "1") => new { apiVersion = version, requestId = Guid.NewGuid().ToString("N"), clientId, operation, arguments = args };
        static string Code(JsonElement result) => result.GetProperty("error").GetProperty("code").GetString()!;
        static object Batch(AutomationLifecycleService service, string name, string? id = null) => new {
            projectSessionId = service.Editor.ProjectSessionId, expectedEditRevision = service.Editor.EditRevision,
            operationId = id ?? Guid.NewGuid().ToString("N"), label = "Test project title", operations = new[] { new { type = "set_project_info", name } }
        };

        [Fact]
        public async Task RealLoopbackApiAuthenticatesValidatesAndCommitsWithNoAssistant() {
            var doc = NewDoc();
            await using var service = new AutomationLifecycleService(doc, new TestDispatcher(doc));
            await service.StartAsync(TestContext.Current.CancellationToken);
            using var client = new HttpClient { BaseAddress = new Uri(service.Url!) };
            Assert.Equal("UNAUTHORIZED", Code(await Send(client, "automation/v1/connect", new { clientId = "test" }, "invalid")));
            Assert.Equal("UNAUTHORIZED", Code(await Send(client, "automation/v1/connect", new { clientId = "test" }, service.DiscoveryToken, "http://example.com")));
            var connection = await Send(client, "automation/v1/connect", new { clientId = "test" }, service.DiscoveryToken);
            string credential = connection.GetProperty("credential").GetString()!;
            Assert.Equal("PROTOCOL_MISMATCH", Code(await Send(client, "automation/v1/invoke", Invoke("get_project", new { }, version: "2"), credential)));
            Assert.Equal("UNAUTHORIZED", Code(await Send(client, "automation/v1/invoke", Invoke("get_project", new { }, clientId: "forged"), credential)));
            Assert.Equal("INVALID_ARGUMENT", Code(await Send(client, "automation/v1/invoke", Invoke("get_project", new { arbitrary = true }), credential)));
            var project = await Send(client, "automation/v1/invoke", Invoke("get_project", new { }), credential);
            Assert.Equal(service.Editor.ProjectSessionId, project.GetProperty("projectSessionId").GetString());
            var payload = Batch(service, "Via regular API");
            var result = await Send(client, "automation/v1/invoke", Invoke("apply_edit_batch", payload), credential);
            Assert.Equal("committed", result.GetProperty("status").GetString()); Assert.Equal("Via regular API", doc.Project.name);
            long revision = service.Editor.EditRevision;
            var replay = await Send(client, "automation/v1/invoke", Invoke("apply_edit_batch", payload), credential);
            Assert.Equal(revision, replay.GetProperty("editRevision").GetInt64()); Assert.Equal(revision, service.Editor.EditRevision);
            var descriptor = Path.Combine(service.RuntimeDirectory, service.InstanceId + ".json");
            Assert.True(File.Exists(descriptor));
            await service.DisposeAsync(); Assert.False(File.Exists(descriptor));
        }

        [Fact]
        public async Task PreviewIsExactlyOnceAndRevocationPreventsApproval() {
            var doc = NewDoc();
            await using var service = new AutomationLifecycleService(doc, new TestDispatcher(doc));
            var grantRef = await service.CreateNativeGrantAsync("test", "preview", 1, TestContext.Current.CancellationToken);
            using var grant = JsonDocument.Parse(File.ReadAllText(grantRef));
            using var client = new HttpClient { BaseAddress = new Uri(service.Url!) };
            var credential = grant.RootElement.GetProperty("credential").GetString()!;
            var pending = await Send(client, "automation/v1/invoke", Invoke("apply_edit_batch", Batch(service, "Approved")), credential);
            Assert.Equal("awaiting_approval", pending.GetProperty("status").GetString()); Assert.Equal("New Project", doc.Project.name);
            var proposal = pending.GetProperty("proposalId").GetString()!;
            await service.ApproveProposalAsync(proposal, TestContext.Current.CancellationToken);
            Assert.Equal("Approved", doc.Project.name);
            await Assert.ThrowsAsync<AutomationException>(() => service.ApproveProposalAsync(proposal, TestContext.Current.CancellationToken));
            var second = await Send(client, "automation/v1/invoke", Invoke("apply_edit_batch", Batch(service, "Must not commit")), credential);
            service.RevokeNativeGrant(grantRef);
            await Assert.ThrowsAsync<AutomationException>(() => service.ApproveProposalAsync(second.GetProperty("proposalId").GetString()!, TestContext.Current.CancellationToken));
            Assert.Equal("Approved", doc.Project.name); Assert.False(File.Exists(grantRef));
        }

        [Fact]
        public async Task ForeignConnectionCannotRevertAndOldProjectWritesFail() {
            var doc = NewDoc();
            await using var service = new AutomationLifecycleService(doc, new TestDispatcher(doc));
            await service.StartAsync(TestContext.Current.CancellationToken);
            using var client = new HttpClient { BaseAddress = new Uri(service.Url!) };
            var a = await Send(client, "automation/v1/connect", new { clientId = "a" }, service.DiscoveryToken);
            var b = await Send(client, "automation/v1/connect", new { clientId = "b" }, service.DiscoveryToken);
            string ca = a.GetProperty("credential").GetString()!, cb = b.GetProperty("credential").GetString()!;
            string id = Guid.NewGuid().ToString("N");
            await Send(client, "automation/v1/invoke", Invoke("apply_edit_batch", Batch(service, "Owner a", id), "a"), ca);
            var foreign = await Send(client, "automation/v1/invoke", Invoke("revert_operation", new { projectSessionId = service.Editor.ProjectSessionId,
                expectedEditRevision = service.Editor.EditRevision, operationId = Guid.NewGuid().ToString("N"), targetOperationId = id }, "b"), cb);
            Assert.Equal("UNAUTHORIZED", Code(foreign)); Assert.Equal("Owner a", doc.Project.name);
            var old = Batch(service, "Stale");
            await service.InvokeAsync("new_project", Json(new { projectSessionId = service.Editor.ProjectSessionId, expectedEditRevision = service.Editor.EditRevision,
                operationId = Guid.NewGuid().ToString("N"), discardChanges = true }), TestContext.Current.CancellationToken);
            Assert.Equal("PROJECT_CHANGED", Code(await Send(client, "automation/v1/invoke", Invoke("apply_edit_batch", old, "a"), ca)));
            var recorded = await Send(client, "automation/v1/invoke", Invoke("get_operation", new { operationId = id }, "a"), ca);
            Assert.Equal("committed", recorded.GetProperty("status").GetString());
        }

        [Fact]
        public void GrantsEnforceReadOnlyScopeAndGenerationCredentials() {
            var access = new AutomationAccessService();
            var read = access.Create("reader", "session", "read_only", 4, true);
            Assert.Equal("UNAUTHORIZED", Assert.Throws<AutomationException>(() => access.Demand(read, "apply_edit_batch", "session", Json(new { }))).Code);
            var scoped = access.Create("editor", "session", "apply", 4, true, new[] { "part1" });
            access.Demand(scoped, "apply_edit_batch", "session", Json(new { operations = new[] { new { type = "set_lyrics", partId = "part1" } } }));
            Assert.Equal("UNAUTHORIZED", Assert.Throws<AutomationException>(() => access.Demand(scoped, "apply_edit_batch", "session", Json(new { operations = new[] { new { type = "add_track", clientRef = "part1" } } }))).Code);
            access.Revoke(scoped.Credential);
            Assert.Equal("UNAUTHORIZED", Assert.Throws<AutomationException>(() => access.Demand(scoped, "apply_edit_batch", "session", Json(new { }))).Code);
        }

        [Fact]
        public void FileHandlesEnforceRootsFingerprintOverwriteAndSession() {
            var root = Path.Combine(Path.GetTempPath(), "OpenUtau Automation " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                var access = new AutomationFileAccessService(); access.AllowDirectory(root);
                string path = Path.Combine(root, "song.ustx"); File.WriteAllText(path, "name: A");
                var handle = Json(access.Register("session", path, "project", false)).GetProperty("fileHandle").GetString()!;
                Assert.Equal(path, access.Resolve("session", handle, "project", false).Path);
                Assert.Equal("FILE_ACCESS_DENIED", Assert.Throws<AutomationException>(() => access.Resolve("other", handle, "project", false)).Code);
                File.WriteAllText(path, "name: Changed");
                Assert.Equal("REVISION_CONFLICT", Assert.Throws<AutomationException>(() => access.Resolve("session", handle, "project", false)).Code);
                Assert.Equal("FILE_ACCESS_DENIED", Assert.Throws<AutomationException>(() => access.Register("session", path, "project", true)).Code);
                var output = Json(access.Register("session", path, "project", true, true)).GetProperty("outputHandle").GetString()!;
                var validated = access.Resolve("session", output, "project", true);
                var temp = Path.Combine(root, "output.tmp"); File.WriteAllText(temp, "complete"); access.CommitOutput(validated, temp);
                Assert.Equal("complete", File.ReadAllText(path)); Assert.False(File.Exists(temp));
                Assert.Equal("FILE_ACCESS_DENIED", Assert.Throws<AutomationException>(() => access.Register("session", Path.Combine(root, "..", "escape.ustx"), "project", true)).Code);
            } finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task SaveUsesValidatedHandleAndProducesReloadableSong() {
            var doc = NewDoc();
            await using var service = new AutomationLifecycleService(doc, new TestDispatcher(doc));
            string root = Path.Combine(Path.GetTempPath(), "OpenUtau Save " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); service.AllowDirectory(root);
            try {
                await service.InvokeAsync("apply_edit_batch", Json(Batch(service, "Atomic song")), TestContext.Current.CancellationToken);
                var target = Json(await service.InvokeAsync("resolve_output_target", Json(new { path = Path.Combine(root, "song.ustx"), purpose = "project" }), TestContext.Current.CancellationToken));
                var saved = Json(await service.InvokeAsync("save_project", Json(new { projectSessionId = service.Editor.ProjectSessionId, expectedEditRevision = service.Editor.EditRevision,
                    operationId = Guid.NewGuid().ToString("N"), outputHandle = target.GetProperty("outputHandle").GetString() }), TestContext.Current.CancellationToken));
                Assert.Equal("committed", saved.GetProperty("status").GetString());
                Assert.Equal("Atomic song", OpenUtau.Core.Format.Ustx.Load(Path.Combine(root, "song.ustx")).name);
                Assert.True(doc.ChangesSaved); Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            } finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task StandardMcpSdkCreatesAndSavesSongThroughActualEditorApi() {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "tools", "openutau-ai", "test", "editor-roundtrip.mjs"))) directory = directory.Parent;
            Assert.NotNull(directory);
            var workspace = Path.Combine(directory.FullName, "tools", "openutau-ai");
            Assert.True(File.Exists(Path.Combine(workspace, "dist", "mcp-server.js")), "Run npm ci && npm run build in tools/openutau-ai before .NET tests.");
            var doc = NewDoc();
            await using var service = new AutomationLifecycleService(doc, new TestDispatcher(doc));
            await service.StartAsync(TestContext.Current.CancellationToken);
            var root = Path.Combine(Path.GetTempPath(), "OpenUtau MCP ü " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); service.AllowDirectory(root);
            try {
                var executable = Path.Combine(workspace, "artifacts", "win-x64", "AI", "node.exe");
                if (!OperatingSystem.IsWindows() || !File.Exists(executable)) executable = "node";
                var launch = new ProcessStartInfo(executable) { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                launch.ArgumentList.Add(Path.Combine(workspace, "test", "editor-roundtrip.mjs"));
                launch.ArgumentList.Add(service.InstanceId); launch.ArgumentList.Add(Path.Combine(root, "song.ustx"));
                using var process = Process.Start(launch)!;
                var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { if (!process.HasExited) process.Kill(true); throw; }
                Assert.True(process.ExitCode == 0, await stderr);
                var evidence = JsonDocument.Parse(await stdout).RootElement;
                Assert.True(evidence.GetProperty("ok").GetBoolean()); Assert.Equal(27, evidence.GetProperty("tools").GetInt32());
                var saved = OpenUtau.Core.Format.Ustx.Load(Path.Combine(root, "song.ustx"));
                Assert.Equal(2, saved.parts.Count); Assert.Equal(3, saved.parts.OfType<OpenUtau.Core.Ustx.UVoicePart>().Sum(p => p.notes.Count));
            } finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task OpenProjectDoesNotAuthorizeEmbeddedAudioOutsideReleasedRoots() {
            var doc = NewDoc();
            await using var service = new AutomationLifecycleService(doc, new TestDispatcher(doc));
            var root = Path.Combine(Path.GetTempPath(), "OpenUtau project trust " + Guid.NewGuid().ToString("N"));
            var outside = Path.Combine(Path.GetTempPath(), "OpenUtau outside " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Directory.CreateDirectory(outside); service.AllowDirectory(root);
            var session = service.Editor.ProjectSessionId;
            try {
                var external = Path.Combine(outside, "external.wav"); File.WriteAllText(external, "Do not open");
                var project = OpenUtau.Core.Format.Ustx.Create(); project.FilePath = Path.Combine(root, "song.ustx");
                project.parts.Add(new OpenUtau.Core.Ustx.UWavePart { FilePath = external, trackNo = 0 });
                project.BeforeSave(); File.WriteAllText(project.FilePath, OpenUtau.Core.Yaml.DefaultSerializer.Serialize(project)); project.AfterSave();
                var registered = Json(await service.InvokeAsync("register_input_file", Json(new { path = project.FilePath, purpose = "project" }), TestContext.Current.CancellationToken));
                var error = await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("open_project", Json(new {
                    projectSessionId = session, expectedEditRevision = service.Editor.EditRevision, operationId = Guid.NewGuid().ToString("N"), fileHandle = registered.GetProperty("fileHandle").GetString()
                }), TestContext.Current.CancellationToken));
                Assert.Equal("FILE_ACCESS_DENIED", error.Code); Assert.Equal(session, service.Editor.ProjectSessionId); Assert.Empty(doc.Project.parts);
            } finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
        }

        [Fact]
        public async Task HttpRenderOwnsItsJsonAfterTheRequestReturnsAndVerifiesAudio() {
            var doc = NewDoc();
            await using var service = new AutomationLifecycleService(doc, new TestDispatcher(doc));
            await service.StartAsync(TestContext.Current.CancellationToken);
            using var client = new HttpClient { BaseAddress = new Uri(service.Url!) };
            var connected = await Send(client, "automation/v1/connect", new { clientId = "test" }, service.DiscoveryToken);
            var credential = connected.GetProperty("credential").GetString()!;
            var root = Path.Combine(Path.GetTempPath(), "OpenUtau HTTP audio " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); service.AllowDirectory(root);
            try {
                var wavePath = Path.Combine(root, "instrumental.wav");
                using (var writer = new WaveFileWriter(wavePath, new WaveFormat(44100, 16, 2))) {
                    for (int i = 0; i < 4410; i++) { float sample = (float)Math.Sin(2 * Math.PI * 440 * i / 44100) * .2f; writer.WriteSample(sample); writer.WriteSample(sample); }
                }
                var input = await Send(client, "automation/v1/invoke", Invoke("register_input_file", new { path = wavePath, purpose = "audio" }), credential);
                var imported = await Send(client, "automation/v1/invoke", Invoke("import_audio", new {
                    projectSessionId = service.Editor.ProjectSessionId, expectedEditRevision = service.Editor.EditRevision,
                    operationId = Guid.NewGuid().ToString("N"), fileHandle = input.GetProperty("fileHandle").GetString()
                }), credential);
                Assert.Equal("committed", imported.GetProperty("status").GetString());
                var started = await Send(client, "automation/v1/invoke", Invoke("start_render", new {
                    projectSessionId = service.Editor.ProjectSessionId, expectedEditRevision = service.Editor.EditRevision,
                    operationId = Guid.NewGuid().ToString("N"), kind = "preview"
                }), credential);
                var jobId = started.GetProperty("jobId").GetString()!;
                JsonElement completed;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                do {
                    completed = await Send(client, "automation/v1/invoke", Invoke("get_job", new { jobId }), credential);
                    if (completed.GetProperty("status").GetString() is "succeeded" or "failed" or "cancelled") break;
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                } while (DateTime.UtcNow < deadline);
                Assert.Equal("succeeded", completed.GetProperty("status").GetString());
                var artifact = completed.GetProperty("artifacts")[0]; Assert.True(artifact.GetProperty("durationSeconds").GetDouble() > 0);
                using var audio = new WaveFileReader(artifact.GetProperty("path").GetString()!); Assert.True(audio.Length > 0);
            } finally { Directory.Delete(root, true); }
        }
    }
}
