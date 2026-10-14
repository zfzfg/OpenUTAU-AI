using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Core.Automation;

namespace OpenUtau.Automation {
    public sealed record AutomationArtifact(string ArtifactId, string Path, string Format, double DurationSeconds,
        string ProjectSessionId, long EditRevision, int StartTicks, int EndTicks, string[] TrackIds, bool Temporary);
    public sealed record AutomationJobState(string JobId, string ProjectSessionId, long EditRevision, string Status,
        double Progress, string? ErrorCode, string? Message, AutomationArtifact[] Artifacts);

    public sealed class AutomationJobManager : IAsyncDisposable {
        sealed class Job {
            public required AutomationJobState State;
            public required string Owner;
            public readonly CancellationTokenSource Cancellation = new();
            public Task? Work;
        }
        readonly Dictionary<string, Job> jobs = new();
        readonly Dictionary<string, AutomationArtifact> artifacts = new();
        readonly Dictionary<string, string> artifactOwners = new();
        readonly object gate = new();
        bool disposed;
        public string PreviewDirectory { get; } = System.IO.Path.Combine(AutomationPrivateFiles.RuntimeDirectory, "previews");
        public AutomationJobManager() { AutomationPrivateFiles.ProtectDirectory(PreviewDirectory); }
        public object Start(string session, long revision, string owner, Func<string, CancellationToken, Task<AutomationArtifact[]>> work) {
            var id = Guid.NewGuid().ToString("N");
            Job job;
            lock (gate) {
                if (disposed) throw new AutomationException("APP_UNAVAILABLE", "OpenUtau is shutting down.");
                var finished = jobs.Where(p => p.Value.Work?.IsCompleted == true).Select(p => p.Key).ToArray();
                foreach (var old in finished.Take(Math.Max(0, jobs.Count - 63))) { jobs[old].Cancellation.Dispose(); jobs.Remove(old); }
                if (jobs.Values.Count(j => j.Work?.IsCompleted != true) >= 4) throw new AutomationException("LIMIT_EXCEEDED", "Four render jobs are already active.");
                job = new Job { Owner = owner, State = new(id, session, revision, "queued", 0, null, null, Array.Empty<AutomationArtifact>()) };
                jobs.Add(id, job);
                job.Work = Task.Run(async () => {
                    AutomationArtifact[]? completedOutput = null;
                    try {
                        var output = await work(id, job.Cancellation.Token);
                        completedOutput = output;
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        lock (gate) {
                            // Cancellation and final publication share one commit point.
                            if (disposed || job.Cancellation.IsCancellationRequested || job.State.Status == "cancelling") throw new OperationCanceledException(job.Cancellation.Token);
                            foreach (var artifact in output) { artifacts[artifact.ArtifactId] = artifact; artifactOwners[artifact.ArtifactId] = job.Owner; }
                            job.State = job.State with { Status = "succeeded", Progress = 1, Artifacts = output };
                            TrimArtifacts();
                        }
                    } catch (OperationCanceledException) {
                        CleanupTemporary(completedOutput);
                        lock (gate) job.State = job.State with { Status = "cancelled", Message = "Rendering was cancelled." };
                    } catch (Exception) when (job.Cancellation.IsCancellationRequested) {
                        CleanupTemporary(completedOutput);
                        lock (gate) job.State = job.State with { Status = "cancelled", Message = "Rendering was cancelled." };
                    } catch (Exception e) {
                        lock (gate) job.State = job.State with { Status = "failed", ErrorCode = e is AutomationException a ? a.Code : "RENDER_FAILED",
                            Message = e is AutomationException ? e.Message : "Rendering failed. Check the singer, samples and renderer in OpenUtau." };
                    }
                });
            }
            return new { ok = true, jobId = id, projectSessionId = session, editRevision = revision, status = "queued" };
        }
        void CleanupTemporary(AutomationArtifact[]? output) {
            if (output == null) return;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PreviewDirectory)) + Path.DirectorySeparatorChar;
            foreach (var artifact in output.Where(a => a.Temporary)) {
                if (!Path.GetFullPath(artifact.Path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                try { File.Delete(artifact.Path); } catch (IOException) { }
            }
        }
        public void Update(string id, string status, double progress) {
            lock (gate) if (jobs.TryGetValue(id, out var job) && job.State.Status != "cancelling") job.State = job.State with { Status = status, Progress = progress };
        }
        public object Get(string id, string? owner, string session, long revision) {
            lock (gate) {
                if (!jobs.TryGetValue(id, out var job)) throw new AutomationException("ENTITY_NOT_FOUND", "Render job not found.");
                if (owner != null && owner != job.Owner) throw new AutomationException("UNAUTHORIZED", "This job belongs to another connection.");
                return new { ok = true, job = job.State, jobId = id, status = job.State.Status, progress = job.State.Progress,
                    error = job.State.ErrorCode == null ? null : new { code = job.State.ErrorCode, message = job.State.Message },
                    projectSessionId = job.State.ProjectSessionId, editRevision = job.State.EditRevision,
                    stale = job.State.ProjectSessionId != session || job.State.EditRevision != revision, artifacts = job.State.Artifacts };
            }
        }
        public object Cancel(string id, string? owner = null) {
            lock (gate) {
                if (!jobs.TryGetValue(id, out var job)) throw new AutomationException("ENTITY_NOT_FOUND", "Render job not found.");
                if (owner != null && owner != job.Owner) throw new AutomationException("UNAUTHORIZED", "This job belongs to another connection.");
                if (job.State.Status is not ("succeeded" or "failed" or "cancelled")) {
                    job.State = job.State with { Status = "cancelling" };
                    job.Cancellation.Cancel();
                }
                return new { ok = true, jobId = id, status = job.State.Status };
            }
        }
        public AutomationArtifact GetArtifact(string id, string? owner = null) {
            lock (gate) {
                if (!artifacts.TryGetValue(id, out var artifact) || !File.Exists(artifact.Path)) throw new AutomationException("ENTITY_NOT_FOUND", "Audio preview has expired.");
                if (owner != null && artifactOwners[id] != owner) throw new AutomationException("UNAUTHORIZED", "This artifact belongs to another connection.");
                return artifact;
            }
        }
        public static AutomationArtifact VerifyAudio(string path, string session, long revision, int start, int end, string[] tracks, bool temporary) {
            if (!File.Exists(path) || new FileInfo(path).Length <= 44) throw new AutomationException("RENDER_FAILED", "Renderer produced no audio.");
            using var wave = new WaveFileReader(path);
            if (wave.TotalTime <= TimeSpan.Zero || wave.WaveFormat.SampleRate <= 0 || wave.Length == 0) throw new AutomationException("RENDER_FAILED", "The WAV is incomplete.");
            return new(Guid.NewGuid().ToString("N"), path, "wav", wave.TotalTime.TotalSeconds, session, revision, start, end, tracks, temporary);
        }
        public void ProjectChanged(string current) { lock (gate) foreach (var job in jobs.Values.Where(j => j.State.ProjectSessionId != current)) job.Cancellation.Cancel(); }
        public void CancelOwner(string owner) { lock (gate) foreach (var job in jobs.Values.Where(j => j.Owner == owner)) job.Cancellation.Cancel(); }
        public void CancelAll() { lock (gate) foreach (var job in jobs.Values) job.Cancellation.Cancel(); }
        void TrimArtifacts() {
            long bytes = artifacts.Values.Where(a => a.Temporary && File.Exists(a.Path)).Sum(a => new FileInfo(a.Path).Length);
            var temporary = artifacts.Values.Where(a => a.Temporary).ToList();
            while (temporary.Count > 16 || bytes > 512L * 1024 * 1024) {
                var artifact = temporary[0];
                if (File.Exists(artifact.Path)) { bytes -= new FileInfo(artifact.Path).Length; File.Delete(artifact.Path); }
                artifacts.Remove(artifact.ArtifactId); artifactOwners.Remove(artifact.ArtifactId); temporary.RemoveAt(0);
            }
        }
        public async ValueTask DisposeAsync() {
            Task[] pending;
            lock (gate) { if (disposed) return; disposed = true; foreach (var j in jobs.Values) j.Cancellation.Cancel(); pending = jobs.Values.Select(j => j.Work!).ToArray(); }
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            lock (gate) foreach (var a in artifacts.Values.Where(a => a.Temporary)) { try { File.Delete(a.Path); } catch (IOException) { } }
        }
    }
}
