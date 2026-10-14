using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Core.Pipeline;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Render {
    /// <summary>Captures musical inputs on the UI thread; render workers own the copied project.</summary>
    public sealed class AutomationRenderSnapshot {
        public UProject Project { get; }
        readonly List<(UVoicePart Part, PhraseSource Source)> sources = new();
        AutomationRenderSnapshot(UProject project) { Project = project; }
        public static AutomationRenderSnapshot Capture(UProject live, int[]? trackNumbers = null, UPart? onlyPart = null) {
            var copy = live.CloneAsTemplate();
            copy.name = live.name;
            copy.comment = live.comment;
            copy.FilePath = live.FilePath;
            copy.tempos = live.tempos.Select(t => new UTempo(t.position, t.bpm)).ToList();
            copy.timeSignatures = live.timeSignatures.Select(t => new UTimeSignature(t.barPosition, t.beatPerBar, t.beatUnit)).ToList();
            copy.tracks = live.tracks.Select(t => new UTrack(t.TrackName) {
                TrackNo = t.TrackNo, TrackColor = t.TrackColor, Singer = t.Singer, Phonemizer = t.Phonemizer,
                Mute = t.Mute, Solo = t.Solo, Muted = t.Muted, Volume = t.Volume, Pan = t.Pan, MixFx = t.MixFx?.Clone(),
                TrackExpressions = t.TrackExpressions.Select(e => e.Clone()).ToList(), ExpressionGraph = t.ExpressionGraph,
                VoiceColorExp = t.VoiceColorExp?.Clone(), VoiceColor2Exp = t.VoiceColor2Exp?.Clone(), VoiceColorNames = t.VoiceColorNames.ToArray(),
                RendererSettings = new URenderSettings { renderer = t.RendererSettings.renderer, resampler = t.RendererSettings.resampler,
                    wavtool = t.RendererSettings.wavtool, Renderer = t.RendererSettings.Renderer,
                    Resampler = t.RendererSettings.Resampler, Wavtool = t.RendererSettings.Wavtool }
            }).ToList();
            if (trackNumbers != null) for (int i = 0; i < copy.tracks.Count; i++) if (!trackNumbers.Contains(i)) copy.tracks[i].Muted = true;
            copy.timeAxis.BuildSegments(copy);
            var result = new AutomationRenderSnapshot(copy);
            foreach (var part in live.parts) {
                if ((onlyPart != null && !ReferenceEquals(part, onlyPart)) || copy.tracks[part.trackNo].Muted) continue;
                if (part is UVoicePart voice) {
                    var cloned = (UVoicePart)voice.Clone();
                    copy.parts.Add(cloned);
                    var track = copy.tracks[voice.trackNo];
                    if (voice.notes.Count == 0 || track.Muted) continue;
                    if (track.Singer == null || !track.Singer.Found) throw new InvalidOperationException("MISSING_SINGER");
                    if (track.RendererSettings.Renderer == null) throw new InvalidOperationException("Renderer is unavailable.");
                    if (!voice.PhonemesUpToDate) throw new InvalidOperationException("Phonemes are pending.");
                    if (voice.phonemes.Any(p => p.Error)) throw new InvalidOperationException("MISSING_ALIAS");
                    var source = PhraseSource.FromPart(copy, track, voice, 1);
                    if (source == null) throw new InvalidOperationException("No renderable phonemes.");
                    result.sources.Add((cloned, source));
                } else if (part is UWavePart wave) {
                    // UWavePart.Clone loads against the live singleton and loses placement; copy explicitly.
                    var cloned = new UWavePart { FilePath = wave.FilePath, relativePath = wave.relativePath, name = wave.name,
                        comment = wave.comment, trackNo = wave.trackNo, position = wave.position, skip = wave.skip, trim = wave.trim,
                        fadein = wave.fadein, fadeout = wave.fadeout, fileDurationMs = wave.fileDurationMs };
                    copy.parts.Add(cloned);
                }
            }
            return result;
        }
        public async Task PrepareAsync(CancellationToken cancellationToken) {
            foreach (var pair in sources) {
                cancellationToken.ThrowIfCancellationRequested();
                var phrases = await Task.Run(pair.Source.BuildPhrases, cancellationToken);
                pair.Part.ApplyPhraseSourceResult(pair.Source, phrases);
            }
            foreach (var wave in Project.parts.OfType<UWavePart>()) {
                cancellationToken.ThrowIfCancellationRequested();
                wave.Load(Project);
                if (wave.Peaks != null) await wave.Peaks.WaitAsync(cancellationToken);
                if (wave.Samples == null) throw new InvalidOperationException("Missing audio input.");
            }
        }
        public Task RenderAsync(string path, int startTicks, int endTicks, int trackNo, TaskScheduler scheduler, CancellationToken cancellationToken) => Task.Run(() => {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationTokenSource cancellation = null;
            try {
                var engine = new RenderEngine(Project, startTicks, endTicks, trackNo);
                var mix = engine.RenderMixdown(scheduler, ref cancellation, true, true, new MixPlanner(), cancellationToken).Item1;
                cancellationToken.ThrowIfCancellationRequested();
                var startSample = Math.Max(0, (int)(Project.timeAxis.TickPosToMsPos(startTicks) * 44.1)) * 2;
                var endSample = endTicks < 0 ? int.MaxValue : checked((int)(Project.timeAxis.TickPosToMsPos(endTicks) * 44.1) * 2);
                WaveFileWriter.CreateWaveFile16(path, new RangeExportAdapter(mix, startSample, endSample, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            } finally { cancellation?.Dispose(); }
        }, cancellationToken);

        sealed class RangeExportAdapter : ISampleProvider {
            readonly ISignalSource source;
            readonly int end;
            readonly CancellationToken token;
            int position;
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            public RangeExportAdapter(ISignalSource source, int start, int end, CancellationToken token) {
                this.source = source; this.end = end; this.token = token; position = start;
            }
            public int Read(float[] buffer, int offset, int count) {
                token.ThrowIfCancellationRequested();
                count = Math.Min(count, Math.Max(0, end - position));
                if (count == 0) return 0;
                Array.Clear(buffer, offset, count);
                if (!source.IsReady(position, count)) throw new InvalidOperationException("Renderer did not complete all sources.");
                var next = source.Mix(position, buffer, offset, count);
                var read = Math.Clamp(next - position, 0, count);
                position += read;
                return read;
            }
        }
    }
}
