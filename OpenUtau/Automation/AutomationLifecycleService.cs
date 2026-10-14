using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using OpenUtau.Core;
using OpenUtau.Core.Automation;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Automation {
    public sealed record AutomationProposal(string ProposalId, string Label, JsonElement Preview, DateTimeOffset ExpiresAt);

    public sealed class AvaloniaAutomationDispatcher : IAutomationDispatcher {
        public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            if (Dispatcher.UIThread.CheckAccess()) return action();
            return await Dispatcher.UIThread.InvokeAsync(() => { cancellationToken.ThrowIfCancellationRequested(); return action(); });
        }
    }

    sealed class AuthorizedAutomationDispatcher(IAutomationDispatcher inner) : IAutomationDispatcher {
        readonly AsyncLocal<Action?> guard = new();
        sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
        public IDisposable Authorize(Action? action) {
            var previous = guard.Value; guard.Value = action;
            return new Scope(() => guard.Value = previous);
        }
        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default) {
            var authorization = guard.Value;
            return inner.InvokeAsync(() => { authorization?.Invoke(); return action(); }, cancellationToken);
        }
        public Task<T> InvokeMaintenanceAsync<T>(Func<T> action) => inner.InvokeAsync(action, CancellationToken.None);
    }

    public sealed class AutomationLifecycleService : IAsyncDisposable {
        sealed class ProposalEntry {
            public required AutomationProposal Proposal;
            public required AutomationGrant Grant;
            public required string Operation;
            public required JsonElement Arguments;
            public required string Hash;
            public string Status = "awaiting_approval";
            public object? Result;
        }
        readonly AuthorizedAutomationDispatcher dispatcher;
        readonly DocManager doc;
        readonly AutomationSettings settings;
        readonly bool persistSettings;
        readonly SemaphoreSlim writes = new(1, 1);
        readonly SemaphoreSlim lifecycleGate = new(1, 1);
        readonly AutomationAccessService access = new();
        readonly AutomationFileAccessService files = new();
        readonly AutomationJobManager jobs = new();
        readonly ConcurrentDictionary<string, ProposalEntry> proposals = new();
        readonly ConcurrentDictionary<string, string> grantFiles = new();
        readonly ConcurrentDictionary<string, (string Hash, object Result)> operationResults = new();
        readonly ConcurrentDictionary<string, string> operationOwners = new();
        AutomationHttpHost? host;
        string lastSession;
        volatile bool disposed;
        public EditorAutomationService Editor { get; }
        public AutomationContextService Context { get; }
        public string InstanceId { get; } = Guid.NewGuid().ToString("N");
        public string RuntimeDirectory => AutomationPrivateFiles.RuntimeDirectory;
        public string StartedAt { get; } = DateTimeOffset.UtcNow.ToString("O");
        public string DiscoveryToken { get; } = AutomationJson.Secret();
        public string? Url => host?.Url;
        public bool Enabled => settings.Enabled;
        public string ExternalMode => settings.ExternalMode;
        public event Action<AutomationProposal>? ProposalCreated;
        public event Action<string>? ProjectChanged;

        public AutomationLifecycleService() : this(DocManager.Inst, new AvaloniaAutomationDispatcher(), AutomationSettings.Load(), true) { }
        public AutomationLifecycleService(DocManager doc, IAutomationDispatcher dispatcher, AutomationSettings? settings = null, bool persistSettings = false) {
            this.doc = doc; this.dispatcher = new AuthorizedAutomationDispatcher(dispatcher); this.persistSettings = persistSettings;
            this.settings = settings ?? new AutomationSettings();
            access.ExternalMode = this.settings.ExternalMode;
            foreach (var path in this.settings.AllowedDirectories) { try { files.AllowDirectory(path); } catch (Exception e) when (e is IOException or AutomationException) { } }
            Editor = new EditorAutomationService(doc, this.dispatcher);
            Context = new AutomationContextService(doc, Editor);
            lastSession = Editor.ProjectSessionId;
            SetDefaultRoot();
            Editor.Changed += OnEditorChanged;
        }
        void SetDefaultRoot() {
            var root = string.IsNullOrEmpty(doc.Project.FilePath) ? Path.Combine(RuntimeDirectory, "workspace") : Path.GetDirectoryName(doc.Project.FilePath)!;
            Directory.CreateDirectory(root);
            files.SetDefaultDirectory(root);
        }
        void OnEditorChanged() {
            var session = Editor.ProjectSessionId;
            if (lastSession == session) return;
            lastSession = session;
            access.RevokeNative(); files.Invalidate(); jobs.ProjectChanged(session);
            foreach (var p in proposals.Values.Where(p => p.Status == "awaiting_approval")) p.Status = "cancelled_before_commit";
            foreach (var grantRef in grantFiles.Keys.ToArray()) RevokeNativeGrant(grantRef);
            SetDefaultRoot();
            PlaybackManager.Inst.StopPlayback();
            ProjectChanged?.Invoke(session);
        }
        public async Task StartAsync(CancellationToken cancellationToken = default) {
            await lifecycleGate.WaitAsync(cancellationToken);
            try {
                if (disposed) throw new AutomationException("APP_UNAVAILABLE", "OpenUtau is shutting down.");
                if (!settings.Enabled || host != null) return;
                host = new AutomationHttpHost(this);
                try { await host.StartAsync(cancellationToken); }
                catch { await host.DisposeAsync(); host = null; throw; }
            } finally { lifecycleGate.Release(); }
        }
        public void AllowDirectory(string path) {
            files.AllowDirectory(path);
            settings.AllowedDirectories = settings.AllowedDirectories.Append(AutomationFileAccessService.Canonical(path, true)).Distinct().ToArray();
            PersistSettings();
        }
        public string[] AllowedDirectories => files.Roots;
        public void SetExternalMode(string mode) {
            if (mode is not ("read_only" or "preview" or "apply")) throw new AutomationException("INVALID_ARGUMENT", "Unknown mode.");
            access.ExternalMode = mode;
            settings.ExternalMode = mode;
            PersistSettings();
            access.RevokeExternal();
            foreach (var p in proposals.Values.Where(p => !p.Grant.Native && p.Status == "awaiting_approval")) p.Status = "rejected";
        }
        public async Task<string> CreateNativeGrantAsync(string clientId, string mode, long processGeneration, CancellationToken cancellationToken = default, string[]? partIds = null) {
            if (!Enabled) await SetEnabledAsync(true, cancellationToken);
            await StartAsync(cancellationToken);
            if (!Enabled || Url == null) throw new AutomationException("AUTOMATION_DISABLED", "The automation connection was disabled while starting.");
            var session = await dispatcher.InvokeAsync(() => Editor.ProjectSessionId, cancellationToken);
            var grant = access.Create(clientId, session, mode, processGeneration, true, partIds);
            var path = Path.Combine(RuntimeDirectory, "grants", grant.GrantId + ".json");
            AutomationPrivateFiles.Write(path, new { url = Url, credential = grant.Credential, instanceId = InstanceId,
                grantId = grant.GrantId, projectSessionId = session, clientId, mode = grant.Mode, processGeneration, expiresAt = grant.ExpiresAt });
            grantFiles[path] = grant.Credential;
            return path;
        }
        public void RevokeNativeGrant(string grantRef) {
            if (!grantFiles.TryRemove(grantRef, out var credential)) return;
            access.Revoke(credential);
            jobs.CancelOwner(Path.GetFileNameWithoutExtension(grantRef));
            foreach (var p in proposals.Values.Where(p => p.Grant.Credential == credential && p.Status == "awaiting_approval")) p.Status = "cancelled_before_commit";
            try { File.Delete(grantRef); } catch (IOException) { }
        }
        internal async Task<object> ConnectAsync(JsonElement arguments, CancellationToken token) {
            if (!Enabled) throw new AutomationException("AUTOMATION_DISABLED", "Enable editor automation in OpenUtau's assistant settings.");
            var session = await dispatcher.InvokeAsync(() => Editor.ProjectSessionId, token);
            var expected = AutomationJson.Text(arguments, "projectSessionId");
            if (expected != null && expected != session) throw new AutomationException("PROJECT_CHANGED", "The requested project is no longer open.");
            var grant = access.Create(AutomationJson.Required(arguments, "clientId"), session, access.ExternalMode, 0, false);
            return new { ok = true, credential = grant.Credential, grantId = grant.GrantId, projectSessionId = session, mode = grant.Mode, expiresAt = grant.ExpiresAt };
        }
        internal AutomationGrant Authenticate(string? credential) => access.Authenticate(credential);
        public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) {
            settings.Enabled = enabled; PersistSettings();
            if (enabled) { await StartAsync(cancellationToken); return; }
            access.RevokeAll();
            jobs.CancelAll();
            foreach (var grantRef in grantFiles.Keys.ToArray()) RevokeNativeGrant(grantRef);
            foreach (var p in proposals.Values.Where(p => p.Status == "awaiting_approval")) p.Status = "cancelled_before_commit";
            await lifecycleGate.WaitAsync(cancellationToken);
            try { if (host != null) { await host.DisposeAsync(); host = null; } }
            finally { lifecycleGate.Release(); }
        }
        void PersistSettings() { if (persistSettings) settings.Save(); }
        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken = default) => InvokeAuthorizedAsync(null, operation, arguments, cancellationToken);

        internal async Task<object> InvokeAuthorizedAsync(AutomationGrant? grant, string operation, JsonElement arguments, CancellationToken token, bool approved = false) {
            if (disposed) throw new AutomationException("APP_UNAVAILABLE", "OpenUtau is shutting down.");
            operation = operation.StartsWith("openutau_", StringComparison.Ordinal) ? operation[9..] : operation;
            if (arguments.ValueKind != JsonValueKind.Object) throw new AutomationException("INVALID_ARGUMENT", "arguments must be an object.");
            // HTTP documents are disposed after returning a job ID; workers and authorization scopes own their copy.
            arguments = arguments.Clone();
            using var authorization = dispatcher.Authorize(grant == null ? null : () => access.Demand(grant, operation, Editor.ProjectSessionId, arguments));
            await dispatcher.InvokeAsync(() => { if (grant != null) access.Demand(grant, operation, Editor.ProjectSessionId, arguments); return true; }, token);
            if (operation is "get_operation" or "revert_operation") {
                var target = AutomationJson.Text(arguments, "targetOperationId") ?? AutomationJson.Text(arguments, "operationId");
                if (grant != null && target != null && operationOwners.TryGetValue(target, out var owner) && owner != grant.GrantId)
                    throw new AutomationException("UNAUTHORIZED", "This operation belongs to another connection.");
            }
            if (AutomationAccessService.IsWrite(operation)) {
                var operationId = AutomationJson.Required(arguments, "operationId");
                var owner = grant?.GrantId ?? "native-ui";
                if (operationOwners.Count >= 4096 && !operationOwners.ContainsKey(operationId)) throw new AutomationException("LIMIT_EXCEEDED", "The operation journal is full. Resolve outstanding operations before restarting OpenUtau.");
                if (!operationOwners.TryAdd(operationId, owner) && operationOwners[operationId] != owner)
                    throw new AutomationException("UNAUTHORIZED", "Operation ID belongs to another connection.");
            }
            if (grant?.Mode == "preview" && AutomationAccessService.IsWrite(operation) && !approved) {
                var id = AutomationJson.Required(arguments, "operationId");
                var hash = Hash(operation, arguments);
                var key = grant.GrantId + ":" + id;
                foreach (var expired in proposals.Values.Where(p => p.Status == "awaiting_approval" && p.Proposal.ExpiresAt <= DateTimeOffset.UtcNow)) {
                    lock (expired) {
                        if (expired.Status != "awaiting_approval") continue;
                        expired.Status = "rejected";
                        expired.Arguments = AutomationJson.Element(new { operationId = AutomationJson.Text(expired.Arguments, "operationId") });
                        expired.Proposal = expired.Proposal with { Preview = AutomationJson.Element(new { expired = true }) };
                    }
                }
                if (proposals.TryGetValue(key, out var previous)) {
                    if (previous.Hash != hash) throw new AutomationException("IDEMPOTENCY_CONFLICT", "Operation ID is already used for another request.");
                    return previous.Result ?? Pending(previous);
                }
                var active = proposals.Values.Where(p => p.Status is "awaiting_approval" or "validating").ToArray();
                if (active.Length >= 64 || active.Sum(p => (long)p.Arguments.GetRawText().Length * 2 + p.Proposal.Preview.GetRawText().Length * 2) >= 32L * 1024 * 1024)
                    throw new AutomationException("LIMIT_EXCEEDED", "Too many pending proposals. Review or reject existing cards first.");
                var preview = operation == "apply_edit_batch" ? await Editor.InvokeAsync("preview_edit", arguments, token)
                    : new { operation, label = AutomationJson.Text(arguments, "label") ?? operation, arguments };
                await dispatcher.InvokeAsync(() => { CheckWrite(arguments); return true; }, token);
                var proposal = new AutomationProposal(Guid.NewGuid().ToString("N"), AutomationJson.Text(arguments, "label") ?? operation,
                    AutomationJson.Element(preview), DateTimeOffset.UtcNow.AddMinutes(10));
                var entry = new ProposalEntry { Proposal = proposal, Grant = grant, Arguments = arguments.Clone(), Hash = hash, Operation = operation };
                if (!proposals.TryAdd(key, entry)) throw new AutomationException("EDITOR_BUSY", "The operation is already being reviewed.");
                if (grant.Native) await dispatcher.InvokeAsync(() => { ProposalCreated?.Invoke(proposal); return true; }, token);
                return Pending(entry);
            }
            if (operation == "get_operation") {
                var id = AutomationJson.Required(arguments, "operationId");
                var proposal = proposals.Values.FirstOrDefault(p => AutomationJson.Text(p.Arguments, "operationId") == id && (grant == null || p.Grant.GrantId == grant.GrantId));
                if (proposal != null) return proposal.Result ?? Pending(proposal);
                if (operationResults.TryGetValue((grant?.GrantId ?? "native-ui") + ":" + id, out var cached)) return cached.Result;
            }
            if (operation == "get_selection") return await dispatcher.InvokeAsync(Context.GetSnapshot, token);
            if (operation == "register_input_file" || operation == "resolve_output_target") {
                var session = await dispatcher.InvokeAsync(() => Editor.ProjectSessionId, token);
                var registered = await Task.Run(() => files.Register(session, AutomationJson.Required(arguments, "path"),
                    AutomationJson.Required(arguments, "purpose"), operation == "resolve_output_target", AutomationJson.Bool(arguments, "overwrite")), token);
                await dispatcher.InvokeAsync(() => { if (session != Editor.ProjectSessionId) throw new AutomationException("PROJECT_CHANGED", "Project changed while registering the file."); return true; }, token);
                return registered;
            }
            if (operation == "get_job") return jobs.Get(AutomationJson.Required(arguments, "jobId"), grant?.GrantId, Editor.ProjectSessionId, Editor.EditRevision);
            if (operation == "cancel_job") return jobs.Cancel(AutomationJson.Required(arguments, "jobId"), grant?.GrantId);
            if (operation is "start_render" or "export_audio") {
                var owner = grant?.GrantId ?? "native-ui";
                var id = AutomationJson.Required(arguments, "operationId");
                if (!operationOwners.TryAdd(id, owner) && operationOwners[id] != owner) throw new AutomationException("UNAUTHORIZED", "This operation belongs to another connection.");
                await writes.WaitAsync(token);
                try {
                    var key = owner + ":" + id;
                    var hash = Hash(operation, arguments);
                    if (operationResults.TryGetValue(key, out var previous)) {
                        if (previous.Hash != hash) throw new AutomationException("IDEMPOTENCY_CONFLICT", "Operation ID has another render payload.");
                        return previous.Result;
                    }
                    var result = await StartRenderAsync(arguments, owner, token);
                    operationResults[key] = (hash, result);
                    return result;
                } finally { writes.Release(); }
            }
            if (operation == "get_artifact") {
                var a = jobs.GetArtifact(AutomationJson.Required(arguments, "artifactId"), grant?.GrantId);
                return new { ok = true, artifact = a, stale = a.ProjectSessionId != Editor.ProjectSessionId || a.EditRevision != Editor.EditRevision };
            }
            if (operation is "play_range" or "stop_playback" or "play_artifact") return await dispatcher.InvokeAsync(() => {
                PlaybackManager.Inst.StopPlayback();
                var artifactId = AutomationJson.Text(arguments, "artifactId");
                if (operation == "play_range" && artifactId == null) {
                    if (AutomationJson.Text(arguments, "projectSessionId") != Editor.ProjectSessionId) throw new AutomationException("PROJECT_CHANGED", "Playback project changed.");
                    int start = arguments.TryGetProperty("fromTicks", out var st) ? st.GetInt32() : 0;
                    int end = arguments.TryGetProperty("toTicks", out var en) ? en.GetInt32() : doc.Project.EndTick;
                    if (start < 0 || end <= start) throw new AutomationException("INVALID_ARGUMENT", "Playback range is invalid.");
                    PlaybackManager.Inst.Play(doc.Project, start, end);
                } else if (operation == "play_artifact" || artifactId != null) {
                    var artifact = jobs.GetArtifact(artifactId ?? AutomationJson.Required(arguments, "artifactId"), grant?.GrantId);
                    if (artifact.ProjectSessionId != Editor.ProjectSessionId) throw new AutomationException("PROJECT_CHANGED", "Preview belongs to a previous project.");
                    PlaybackManager.Inst.PlayFile(artifact.Path);
                }
                return (object)new { ok = true, status = operation == "stop_playback" ? "stopped" : "playing" };
            }, token);
            if (operation is "new_project" or "open_project" or "save_project" or "import_midi" or "import_audio") {
                await writes.WaitAsync(token);
                try {
                    var id = AutomationJson.Required(arguments, "operationId");
                    var key = (grant?.GrantId ?? "native-ui") + ":" + id;
                    var hash = Hash(operation, arguments);
                    if (operationResults.TryGetValue(key, out var found)) {
                        if (found.Hash != hash) throw new AutomationException("IDEMPOTENCY_CONFLICT", "Operation ID has a different payload.");
                        return found.Result;
                    }
                    await dispatcher.InvokeAsync(() => { CheckWrite(arguments); return true; }, token);
                    object result;
                    if (operation == "save_project") result = await SaveAsync(arguments, token);
                    else if (operation is "import_midi" or "import_audio") {
                        var input = await dispatcher.InvokeAsync(() => files.Resolve(Editor.ProjectSessionId, AutomationJson.Required(arguments, "fileHandle"), operation == "import_midi" ? "midi" : "audio", false), token);
                        Action verifyInput = () => files.Resolve(input.Session, input.Handle, input.Purpose, false);
                        result = operation == "import_midi" ? await Editor.ImportMidiAsync(input.Path, arguments, token, verifyInput) : await Editor.ImportAudioAsync(input.Path, arguments, token, verifyInput);
                    } else {
                        string? input = operation == "open_project" ? await dispatcher.InvokeAsync(() => files.Resolve(Editor.ProjectSessionId,
                            AutomationJson.Required(arguments, "fileHandle"), "project", false).Path, token) : null;
                        string? projectText = input == null ? null : await File.ReadAllTextAsync(input, token);
                        if (projectText != null) {
                            var raw = await Task.Run(() => Yaml.DefaultDeserializer.Deserialize<UProject>(projectText), token);
                            foreach (var wave in raw.waveParts ?? new List<UWavePart>()) {
                                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(input!)!, wave.relativePath ?? ""));
                                files.ValidateInputPath(path, "audio");
                            }
                        }
                        result = await dispatcher.InvokeAsync(() => {
                            CheckWrite(arguments);
                    if (!doc.ChangesSaved && !AutomationJson.Bool(arguments, "discardChanges")) throw new AutomationException("EDITOR_BUSY", "Save current changes or explicitly choose discardChanges: true.");
                            if (input != null) files.Resolve(Editor.ProjectSessionId, AutomationJson.Required(arguments, "fileHandle"), "project", false);
                            var project = input == null ? Ustx.Create() : Ustx.LoadFromText(input, projectText!);
                            if (input == null && AutomationJson.Text(arguments, "name") is { } name) project.name = name;
                            doc.ExecuteCmd(new LoadProjectNotification(project));
                            return (object)new { ok = true, status = "committed", operationId = id, projectSessionId = Editor.ProjectSessionId, editRevision = Editor.EditRevision };
                        }, token);
                    }
                    operationResults[key] = (hash, result);
                    return result;
                } finally { writes.Release(); }
            }
            return await Editor.InvokeAsync(operation, arguments, token);
        }
        static string Hash(string operation, JsonElement args) {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartArray(); writer.WriteStringValue(operation); Canonical(writer, args); writer.WriteEndArray(); }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        static void Canonical(Utf8JsonWriter writer, JsonElement value) {
            if (value.ValueKind == JsonValueKind.Object) {
                writer.WriteStartObject();
                foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Canonical(writer, p.Value); }
                writer.WriteEndObject();
            } else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
            else value.WriteTo(writer);
        }
        object Pending(ProposalEntry entry) => new { ok = true, operationId = AutomationJson.Text(entry.Arguments, "operationId"), status = entry.Status,
            proposalId = entry.Proposal.ProposalId, label = entry.Proposal.Label, preview = entry.Proposal.Preview, expiresAt = entry.Proposal.ExpiresAt };
        void CheckWrite(JsonElement arguments) {
            Editor.EnsureCurrent(AutomationJson.Required(arguments, "projectSessionId"), arguments.GetProperty("expectedEditRevision").GetInt64());
            if (doc.HasOpenUndoGroup) throw new AutomationException("EDITOR_BUSY", "Finish the current editor gesture and retry.");
        }
        public async Task<object> ApproveProposalAsync(string proposalId, CancellationToken cancellationToken = default) {
            var entry = proposals.Values.FirstOrDefault(p => p.Proposal.ProposalId == proposalId) ?? throw new AutomationException("ENTITY_NOT_FOUND", "Proposal not found.");
            lock (entry) {
                if (entry.Status != "awaiting_approval") throw new AutomationException("REVISION_CONFLICT", "This proposal was already answered or withdrawn.");
                if (entry.Proposal.ExpiresAt <= DateTimeOffset.UtcNow) { entry.Status = "rejected"; throw new AutomationException("SNAPSHOT_EXPIRED", "Proposal expired. Request a new preview."); }
                access.Authenticate(entry.Grant.Credential);
                entry.Status = "validating";
            }
            try {
                var result = await InvokeAuthorizedAsync(entry.Grant, entry.Operation, entry.Arguments, cancellationToken, true);
                lock (entry) { entry.Result = result; entry.Status = "committed"; }
                return result;
            } catch { lock (entry) entry.Status = "failed"; throw; }
        }
        public void RejectProposal(string proposalId) {
            var entry = proposals.Values.FirstOrDefault(p => p.Proposal.ProposalId == proposalId);
            if (entry == null) return;
            lock (entry) if (entry.Status == "awaiting_approval") entry.Status = "rejected";
        }
        async Task<object> SaveAsync(JsonElement arguments, CancellationToken token) {
            var captured = await dispatcher.InvokeAsync(() => {
                CheckWrite(arguments);
                var target = files.Resolve(Editor.ProjectSessionId, AutomationJson.Required(arguments, "outputHandle"), "project", true);
                var project = doc.Project;
                var oldPath = project.FilePath;
                try {
                    project.FilePath = target.Path;
                    project.ustxVersion = Ustx.kUstxVersion;
                    project.BeforeSave();
                    return (Target: target, Text: Yaml.DefaultSerializer.Serialize(project), Session: Editor.ProjectSessionId, Revision: Editor.EditRevision);
                } finally { project.AfterSave(); project.FilePath = oldPath; }
            }, token);
            var temp = captured.Target.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                await File.WriteAllTextAsync(temp, captured.Text, new UTF8Encoding(false), token);
                return await dispatcher.InvokeAsync(() => {
                    Editor.EnsureCurrent(captured.Session, captured.Revision);
                    files.CommitOutput(captured.Target, temp);
                    doc.MarkProjectSaved(captured.Target.Path);
                    return (object)new { ok = true, status = "committed", operationId = AutomationJson.Text(arguments, "operationId"), path = captured.Target.Path,
                        projectSessionId = captured.Session, editRevision = captured.Revision };
                }, token);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        async Task<object> StartRenderAsync(JsonElement args, string owner, CancellationToken token) {
            var basis = await dispatcher.InvokeAsync(() => {
                CheckWrite(args);
                var ids = doc.Project.tracks.Select(Editor.Identities.GetId).ToArray();
                var requested = args.TryGetProperty("trackIds", out var tracks) ? tracks.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();
                var numbers = requested.Length == 0 ? Enumerable.Range(0, ids.Length).ToArray() : requested.Select(t => Array.IndexOf(ids, t)).ToArray();
                if (numbers.Any(t => t < 0)) throw new AutomationException("ENTITY_NOT_FOUND", "Render track is not in this project.");
                var part = AutomationJson.Text(args, "partId") is { } partId ? Editor.FindPart(partId) : null;
                var voices = doc.Project.parts.OfType<UVoicePart>().Where(p => numbers.Contains(p.trackNo) && (part == null || ReferenceEquals(p, part)) && !doc.Project.tracks[p.trackNo].Muted).ToArray();
                foreach (var p in voices.Where(p => p.notes.Count > 0)) if (doc.Project.tracks[p.trackNo].Singer is not { Found: true })
                    throw new AutomationException("MISSING_SINGER", "Select an installed singer for each rendered vocal track.");
                return (Session: Editor.ProjectSessionId, Revision: Editor.EditRevision, Ids: ids, Numbers: numbers,
                    Part: part, DefaultStart: part?.position ?? 0, DefaultEnd: part?.End ?? -1, VoiceIds: voices.Select(Editor.Identities.GetId).ToArray());
            }, token);
            return jobs.Start(basis.Session, basis.Revision, owner, async (id, cancellation) => {
                jobs.Update(id, "waiting_for_phonemes", .05);
                var until = DateTimeOffset.UtcNow.AddSeconds(30);
                while (!await dispatcher.InvokeAsync(() => { Editor.EnsureCurrent(basis.Session, basis.Revision); return Editor.ArePhonemesReady(basis.VoiceIds); }, cancellation)) {
                    if (DateTimeOffset.UtcNow >= until) throw new AutomationException("PHONEMIZER_FAILED", "Phonemes did not complete within 30 seconds.");
                    await Task.Delay(50, cancellation);
                }
                var snapshot = await dispatcher.InvokeAsync(() => {
                    Editor.EnsureCurrent(basis.Session, basis.Revision);
                    try { return AutomationRenderSnapshot.Capture(doc.Project, basis.Numbers, basis.Part); }
                    catch (InvalidOperationException e) { throw new AutomationException(e.Message == "MISSING_SINGER" ? "MISSING_SINGER" : e.Message == "MISSING_ALIAS" ? "MISSING_ALIAS" : "RENDER_FAILED", e.Message); }
                }, cancellation);
                jobs.Update(id, "running", .15);
                await snapshot.PrepareAsync(cancellation);
                var mode = AutomationJson.Text(args, "kind") ?? "preview";
                if (mode is not ("preview" or "mixdown" or "stems")) throw new AutomationException("INVALID_ARGUMENT", "Unknown render mode.");
                var start = args.TryGetProperty("fromTicks", out var st) ? st.GetInt32() : basis.DefaultStart;
                var end = args.TryGetProperty("toTicks", out var en) ? en.GetInt32() : basis.DefaultEnd;
                if (start < 0 || (end != -1 && end <= start)) throw new AutomationException("INVALID_ARGUMENT", "Invalid render range.");
                var ids = basis.Ids;
                var trackNumbers = basis.Numbers;
                var output = AutomationJson.Text(args, "outputHandle");
                var target = output == null ? null : await dispatcher.InvokeAsync(() => files.Resolve(basis.Session, output, "audio", true), cancellation);
                var artifacts = new List<AutomationArtifact>();
                try {
                    foreach (var trackNo in mode == "stems" ? trackNumbers : new[] { -1 }) {
                        var destination = target?.Path ?? Path.Combine(jobs.PreviewDirectory, id + ".wav");
                        AutomationFileHandle? actualTarget = target;
                        if (mode == "stems") {
                            destination = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileNameWithoutExtension(destination) + $"-track-{trackNo + 1}.wav");
                            if (target != null) actualTarget = await dispatcher.InvokeAsync(() => {
                                var registered = AutomationJson.Element(files.Register(basis.Session, destination, "audio", true, target.Overwrite));
                                return files.Resolve(basis.Session, registered.GetProperty("outputHandle").GetString()!, "audio", true);
                            }, cancellation);
                        }
                        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try {
                            await snapshot.RenderAsync(temp, start, end, trackNo, doc.MainScheduler ?? TaskScheduler.Default, cancellation);
                            var verified = AutomationJobManager.VerifyAudio(temp, basis.Session, basis.Revision, start, end, trackNo < 0 ? ids : new[] { ids[trackNo] }, target == null);
                            cancellation.ThrowIfCancellationRequested();
                            if (actualTarget == null) File.Move(temp, destination, true);
                            else await dispatcher.InvokeAsync(() => { files.CommitOutput(actualTarget, temp); return true; }, cancellation);
                            artifacts.Add(verified with { Path = destination });
                        } finally { if (File.Exists(temp)) File.Delete(temp); }
                        jobs.Update(id, "running", .85);
                    }
                    return artifacts.ToArray();
                } catch { foreach (var a in artifacts.Where(a => a.Temporary)) File.Delete(a.Path); throw; }
            });
        }
        public async Task ShowPartAsync(string partId, CancellationToken cancellationToken = default) {
            await dispatcher.InvokeAsync(() => {
                var part = doc.Project.parts.FirstOrDefault(p => Editor.Identities.GetId(p) == partId) ?? throw new AutomationException("ENTITY_NOT_FOUND", "Part no longer exists.");
                doc.ExecuteCmd(new LoadPartNotification(part, doc.Project, part.position));
                return true;
            }, cancellationToken);
        }
        public Task<string> CopyableMcpConfigurationAsync() {
            var root = Environment.GetEnvironmentVariable("OPENUTAU_AI_DIR") ?? Path.Combine(AppContext.BaseDirectory, "AI");
            var node = Environment.GetEnvironmentVariable("OPENUTAU_AI_NODE_PATH") ?? Path.Combine(root, OperatingSystem.IsWindows() ? "node.exe" : "node");
            var entry = Path.Combine(root, "mcp-server.js");
            if (!File.Exists(node) || !File.Exists(entry)) throw new AutomationException("APP_UNAVAILABLE", "The AI runtime package is missing. Build/install the OpenUtau AI package.");
            AutomationPackageVerifier.Verify(root);
            return Task.FromResult(JsonSerializer.Serialize(new { mcpServers = new { openutau = new { command = Path.GetFullPath(node), args = new[] { Path.GetFullPath(entry), "--stdio" } } } }, new JsonSerializerOptions(AutomationJson.Options) { WriteIndented = true }));
        }
        public async ValueTask DisposeAsync() {
            if (disposed) return; disposed = true;
            access.RevokeAll();
            foreach (var path in grantFiles.Keys) RevokeNativeGrant(path);
            Editor.Changed -= OnEditorChanged;
            Context.Dispose(); Editor.Dispose();
            await jobs.DisposeAsync();
            await lifecycleGate.WaitAsync();
            try { if (host != null) { await host.DisposeAsync(); host = null; } }
            finally { lifecycleGate.Release(); }
        }
    }
}
