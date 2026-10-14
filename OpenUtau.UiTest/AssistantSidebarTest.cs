using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using OpenUtau.App.Assistant;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Automation;
using OpenUtau.Core;
using OpenUtau.Core.Automation;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using OpenUtau.Colors;
using Xunit;

namespace OpenUtau.UiTest {
    public class AssistantSidebarTest {
        static Task WithWindow(string name, Action<MainWindow, MainWindowViewModel, AssistantSidebar> test) {
            return WithWindow(name, (window, vm, sidebar) => { test(window, vm, sidebar); return Task.CompletedTask; });
        }
        static Task WithWindow(string name, Func<MainWindow, MainWindowViewModel, AssistantSidebar, Task> test) {
            return HeadlessUi.RunAsync(async () => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
                vm.AssistantVisible = true;
                try {
                    window.Show(); window.InitProject(); window.UpdateAssistantLayout(); HeadlessUi.Flush();
                    var sidebar = window.FindControl<AssistantSidebar>("AssistantPanel")!;
                    await test(window, vm, sidebar);
                    HeadlessUi.Flush();
                    Assert.True(HeadlessUi.Errors.Snapshot().Count == 0, string.Join("\n", HeadlessUi.Errors.Snapshot()));
                } finally {
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, name);
                    // End the test with a clean document, avoiding a user-save dialog.
                    DocManager.Inst.Project.Saved = true;
                    DocManager.Inst.Recovered = false;
                    DocManager.Inst.ExecuteCmd(new LoadProjectNotification(DocManager.Inst.Project));
                    window.Close();
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (window.IsVisible && DateTime.UtcNow < deadline) { HeadlessUi.Flush(); await Task.Delay(5); }
                    Assert.False(window.IsVisible, "Accepted shutdown did not finish.");
                }
            });
        }

        [Fact]
        public Task SidebarResizesAndCollapsesWithoutLosingPreferredWidth() {
            return WithWindow(nameof(SidebarResizesAndCollapsesWithoutLosingPreferredWidth), (window, vm, sidebar) => {
                vm.Assistant.Preferences.Width = 420;
                window.UpdateAssistantLayout(); HeadlessUi.Flush();
                Assert.True(vm.AssistantEffectiveVisible);
                Assert.InRange(sidebar.Bounds.Width, 419, 421);
                Assert.True(window.FindControl<GridSplitter>("AssistantSplitter")!.Focusable);
                window.Width = 700; HeadlessUi.Flush();
                Assert.False(vm.AssistantEffectiveVisible);
                Assert.Equal(0, window.FindControl<Grid>("MainGrid")!.ColumnDefinitions[2].ActualWidth);
                Assert.Equal(420, vm.Assistant.Preferences.Width);
                window.Width = 1280; HeadlessUi.Flush();
                Assert.True(vm.AssistantEffectiveVisible);
                Assert.InRange(sidebar.Bounds.Width, 419, 421);
                vm.AssistantVisible = false; window.UpdateAssistantLayout(); HeadlessUi.Flush();
                Assert.Equal(0, window.FindControl<Grid>("MainGrid")!.ColumnDefinitions[1].ActualWidth);
                Assert.Equal(0, window.FindControl<Grid>("MainGrid")!.ColumnDefinitions[2].ActualWidth);
                vm.AssistantVisible = true; window.UpdateAssistantLayout(); HeadlessUi.Flush();
                Assert.True(HeadlessUi.CountRenderedColors(window) > 10);
            });
        }

        [Fact]
        public Task ComposerKeepsPointerFocusAndDoesNotRouteEditorShortcuts() {
            return WithWindow(nameof(ComposerKeepsPointerFocusAndDoesNotRouteEditorShortcuts), (window, vm, sidebar) => {
                vm.NewProject();
                vm.Page = 1;
                var project = DocManager.Inst.Project;
                var part = new UVoicePart { name = "Selected vocal", position = 0, duration = 1920, trackNo = 0 };
                DocManager.Inst.StartUndoGroup("fixture"); DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part)); DocManager.Inst.EndUndoGroup();
                vm.TracksViewModel.SelectPart(part);
                HeadlessUi.Flush();
                var composer = sidebar.FindControl<TextBox>("Composer")!;
                var point = composer.TranslatePoint(new Point(16, 16), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); HeadlessUi.Flush();
                Assert.Same(composer, window.FocusManager!.GetFocusedElement());
                vm.Assistant.Draft = "vocal draft";
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
                window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
                window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
                window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
                HeadlessUi.Flush();
                Assert.Contains(part, project.parts);
                Assert.Contains(part, vm.TracksViewModel.SelectedParts);
                Assert.False(PlaybackManager.Inst.PlayingMaster);
                Assert.Equal(1, vm.Page);
                Assert.Same(composer, window.FocusManager.GetFocusedElement());
            });
        }

        [Fact]
        public Task ComposerEnterRespectsShiftAndImePreedit() {
            return WithWindow(nameof(ComposerEnterRespectsShiftAndImePreedit), (window, vm, sidebar) => {
                var composer = sidebar.FindControl<TextBox>("Composer")!;
                composer.Focus(); vm.Assistant.Draft = "first line"; composer.CaretIndex = composer.Text!.Length;
                window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null); HeadlessUi.Flush();
                Assert.Contains('\n', vm.Assistant.Draft);
                var presenter = composer.GetVisualDescendants().OfType<TextPresenter>().First();
                presenter.PreeditText = "歌";
                var previousMessages = vm.Assistant.Messages.Count;
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); HeadlessUi.Flush();
                Assert.Equal(previousMessages, vm.Assistant.Messages.Count);
                Assert.Contains("first line", vm.Assistant.Draft);
                presenter.PreeditText = null;
            });
        }

        [Fact]
        public Task NativeHistoryIsVirtualizedAndCardsUseActualResults() {
            return WithWindow(nameof(NativeHistoryIsVirtualizedAndCardsUseActualResults), (window, vm, sidebar) => {
                vm.Assistant.Preferences.HistoryLimit = 2500;
                vm.Assistant.Messages.Clear();
                for (var i = 0; i < 2000; i++) { vm.Assistant.Messages.Add(new AssistantMessageViewModel { Title = "Codex", Text = $"Phrase {i}: keep this lyric and timing context." }); }
                vm.Assistant.EnqueueEvent("toolStarted", JsonSerializer.SerializeToElement(new { toolCallId = "tool-1", title = "Change refrain lyrics", status = "running" }));
                vm.Assistant.EnqueueEvent("toolCompleted", JsonSerializer.SerializeToElement(new { toolCallId = "tool-1", status = "succeeded", result = new { summary = "12 lyrics changed", editRevision = 4 } }));
                vm.Assistant.FlushEvents();
                var tool = Assert.Single(vm.Assistant.Messages.Where(m => m.Id == "tool-1"));
                Assert.Equal("succeeded", tool.Status);
                Assert.Equal("12 lyrics changed", tool.Text);
                vm.Assistant.AddProposal("review-1", "Refrain: vocal timing", JsonSerializer.SerializeToElement(new { summary = "8 notes · beat 17–25\nShorten the final syllable and move the breath before the refrain." }));
                HeadlessUi.Flush();
                sidebar.FindControl<ListBox>("Conversation")!.ScrollIntoView(vm.Assistant.Messages.Last()); HeadlessUi.Flush();
                Assert.InRange(sidebar.GetVisualDescendants().OfType<AssistantMessageCard>().Count(), 1, 80);
                Assert.Contains(sidebar.GetVisualDescendants().OfType<Button>(), button => button.Focusable);
                Assert.True(HeadlessUi.CountRenderedColors(window) > 10);
            });
        }

        [Fact]
        public Task QuestionsAreWithdrawnOnTurnCompletion() {
            return WithWindow(nameof(QuestionsAreWithdrawnOnTurnCompletion), (_, vm, _) => {
                vm.Assistant.Messages.Clear();
                vm.Assistant.EnqueueEvent("questionRequested", JsonSerializer.SerializeToElement(new { requestId = "q-1", title = "Vocal language", question = "Which language should this song use?", options = new[] { new { optionId = "de", name = "German" } } }));
                vm.Assistant.FlushEvents();
                var question = Assert.Single(vm.Assistant.Messages);
                Assert.True(question.Pending);
                vm.Assistant.EnqueueEvent("turnCompleted", JsonSerializer.SerializeToElement(new { stopReason = "cancelled" }));
                vm.Assistant.FlushEvents();
                Assert.False(question.Pending);
                Assert.Equal("ready", vm.Assistant.State);
            });
        }

        [Fact]
        public Task NativeHostUsesTheEditorMcpAndResolvesPermissionAndCancel() {
            return WithWindow(nameof(NativeHostUsesTheEditorMcpAndResolvesPermissionAndCancel), async (window, vm, _) => {
                var source = new DirectoryInfo(AppContext.BaseDirectory);
                while (source != null && !File.Exists(Path.Combine(source.FullName, "OPENUTAU_KI_UMSETZUNGSPLAN.md"))) { source = source.Parent; }
                Assert.NotNull(source);
                var workspace = Path.Combine(source.FullName, "tools", "openutau-ai");
                var host = Path.Combine(workspace, "dist", "agent-host.js");
                Assert.True(File.Exists(host), "Run npm ci and npm run build in tools/openutau-ai before native host integration tests.");
                var nodeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
                var node = Environment.GetEnvironmentVariable("OPENUTAU_AI_NODE_PATH") ?? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(directory => Path.Combine(directory, nodeName)).FirstOrDefault(File.Exists);
                Assert.NotNull(node);
                vm.Assistant.Preferences.Provider = "codex";
                vm.Assistant.Preferences.NodePath = node;
                vm.Assistant.Preferences.HostPath = host;
                vm.Assistant.Preferences.CliPath = node;
                vm.Assistant.Preferences.AdapterPath = Path.Combine(workspace, "test", "fixtures", "fake-acp-agent.mjs");
                vm.Assistant.Preferences.Mode = "read_only";
                vm.Assistant.Preferences.ContextScope = "project";
                vm.Assistant.Messages.Clear();
                await vm.Assistant.ConnectAsync();
                Assert.True(vm.Assistant.IsConnected, vm.Assistant.Status);
                Assert.Equal(2, vm.Assistant.Models.Count);
                Assert.Equal(new[] { "low", "high" }, vm.Assistant.Efforts.Select(e => e.Id));
                vm.Assistant.Draft = "tool";
                await vm.Assistant.SendAsync(); vm.Assistant.FlushEvents();
                var tool = Assert.Single(vm.Assistant.Messages, m => m.Kind == "tool");
                Assert.NotNull(tool.Result);
                Assert.True(tool.Result.Value.TryGetProperty("projectSessionId", out var sessionId), tool.Result.Value.GetRawText());
                Assert.Equal("completed", tool.Status);

                // Cancelling the editor's existing save dialog must preserve the real provider session.
                var project = DocManager.Inst.Project;
                DocManager.Inst.StartUndoGroup("Save-cancel fixture");
                if (project.tracks.Count == 0) { DocManager.Inst.ExecuteCmd(new AddTrackCommand(project, new UTrack())); }
                DocManager.Inst.ExecuteCmd(new AddPartCommand(project, new UVoicePart { name = "Close-cancel phrase", trackNo = 0, duration = 1920 }));
                DocManager.Inst.EndUndoGroup();
                window.Close(); HeadlessUi.Flush();
                var saveDialog = Assert.Single(window.OwnedWindows.OfType<MessageBox>());
                var cancel = saveDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == saveDialog.FindResource("button.cancel") as string);
                cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20); HeadlessUi.Flush();
                Assert.True(window.IsVisible);
                Assert.True(vm.Assistant.IsConnected);
                vm.Assistant.Draft = "hello";
                await vm.Assistant.SendAsync(); vm.Assistant.FlushEvents();
                Assert.Equal("ready", vm.Assistant.State);

                vm.Assistant.Draft = "permission";
                var prompt = vm.Assistant.SendAsync();
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (!vm.Assistant.Messages.Any(m => m.Kind == "permission" && m.Pending) && DateTime.UtcNow < deadline) { await Task.Delay(10); vm.Assistant.FlushEvents(); }
                var permission = Assert.Single(vm.Assistant.Messages, m => m.Kind == "permission" && m.Pending);
                await vm.Assistant.RespondAsync(permission, "reject_once");
                await prompt.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.False(permission.Pending);

                vm.Assistant.Draft = "wait";
                var pending = vm.Assistant.SendAsync();
                await Task.Delay(100); vm.Assistant.FlushEvents();
                await vm.Assistant.StopAsync();
                await pending.WaitAsync(TimeSpan.FromSeconds(8)); vm.Assistant.FlushEvents();
                Assert.False(vm.Assistant.IsBusy);
                await vm.Assistant.DisconnectAsync();
                Assert.False(vm.Assistant.IsConnected);
            });
        }

        [Fact]
        public Task LiveLanguageAndThemesKeepStreamingAndMusicalReviewReadable() {
            return WithWindow(nameof(LiveLanguageAndThemesKeepStreamingAndMusicalReviewReadable), (window, vm, sidebar) => {
                var oldTheme = Preferences.Default.ThemeName;
                var oldLanguage = Preferences.Default.Language;
                var customPath = Path.Combine(AssistantRuntimePaths.UserDirectory, "qa-theme.yaml");
                Directory.CreateDirectory(AssistantRuntimePaths.UserDirectory);
                File.WriteAllText(customPath, Yaml.DefaultSerializer.Serialize(new CustomTheme.ThemeYaml { Name = "Assistant QA", AccentColor1 = "#338899" }));
                CustomTheme.Themes["Assistant QA"] = customPath;
                try {
                    vm.Assistant.Messages.Clear();
                    OpenUtau.App.App.SetLanguage("en-US"); Preferences.Default.ThemeName = "Light"; OpenUtau.App.App.SetTheme(); HeadlessUi.Flush();
                    vm.Assistant.EnqueueEvent("messageDelta", JsonSerializer.SerializeToElement(new { text = "The refrain keeps its melody. " }));
                    vm.Assistant.FlushEvents();
                    Assert.Contains(sidebar.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "AI assistant");
                    Assert.Equal("Read only", vm.Assistant.Modes[0].Name);
                    HeadlessUi.SaveScreenshot(window, "AssistantEnglishLight");

                    OpenUtau.App.App.SetLanguage("de-DE"); Preferences.Default.ThemeName = "Dark"; OpenUtau.App.App.SetTheme(); HeadlessUi.Flush();
                    vm.Assistant.EnqueueEvent("messageDelta", JsonSerializer.SerializeToElement(new { text = "Lyrics und Timing werden als Vorschlag geprüft." }));
                    vm.Assistant.FlushEvents();
                    Assert.Contains(sidebar.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "KI-Assistent");
                    Assert.Equal("Nur lesen", vm.Assistant.Modes[0].Name);
                    Assert.Contains("melody", vm.Assistant.Messages.Last().Text);
                    var changes = Enumerable.Range(0, 25).Select(i => new {
                        noteId = "note-" + i, partId = "part-1", trackName = "Lead vocal", partName = "Refrain",
                        before = new { lyric = "la", tone = 60, positionTicks = i * 480, durationTicks = 480, tuningCents = 0 },
                        after = new { lyric = "Liebe", tone = 62, positionTicks = i * 480, durationTicks = 360, tuningCents = 0 }
                    }).ToArray();
                    vm.Assistant.AddProposal("live-review", "Refrain · Lyrics und Notendauer", JsonSerializer.SerializeToElement(new { noteChanges = changes }));
                    var review = vm.Assistant.Messages.Last();
                    Assert.Equal(12, review.ReviewLines.Count);
                    Assert.Contains("la → Liebe", review.ReviewLines[0]);
                    review.ReviewPage = 2;
                    Assert.Single(review.ReviewLines);
                    review.ReviewPage = 0;
                    sidebar.FindControl<ListBox>("Conversation")!.ScrollIntoView(review); HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, "AssistantGermanDarkReview");
                    Preferences.Default.ThemeName = "Assistant QA"; OpenUtau.App.App.SetTheme(); HeadlessUi.Flush();
                    Assert.True(HeadlessUi.CountRenderedColors(window) > 10);
                    HeadlessUi.SaveScreenshot(window, "AssistantCustomThemeReview");
                } finally {
                    Preferences.Default.ThemeName = oldTheme;
                    OpenUtau.App.App.SetTheme();
                    OpenUtau.App.App.SetLanguage(string.IsNullOrEmpty(oldLanguage) ? "de-DE" : oldLanguage);
                    CustomTheme.Themes.Remove("Assistant QA");
                    File.Delete(customPath);
                }
            });
        }

        [Fact]
        public Task AutomationBatchRefreshesTheLiveTracksAndPianoRoll() {
            return WithWindow(nameof(AutomationBatchRefreshesTheLiveTracksAndPianoRoll), async (window, vm, _) => {
                vm.NewProject();
                vm.Page = 1;
                using var editor = new EditorAutomationService(DocManager.Inst, new AvaloniaAutomationDispatcher());
                var trackId = editor.Identities.GetId(DocManager.Inst.Project.tracks[0]);
                var request = JsonSerializer.SerializeToElement(new {
                    projectSessionId = editor.ProjectSessionId, expectedEditRevision = editor.EditRevision, operationId = Guid.NewGuid().ToString("N"),
                    operations = new object[] {
                        new { type = "add_part", trackId, clientRef = "verse", name = "Native live verse", positionTicks = 0, durationTicks = 1920 },
                        new { type = "add_notes", partId = "@verse", notes = new[] { new { positionTicks = 0, durationTicks = 480, tone = 60, lyric = "la" } } },
                    }
                });
                await editor.InvokeAsync("apply_edit_batch", request); HeadlessUi.Flush();
                var part = Assert.Single(vm.TracksViewModel.Parts.OfType<UVoicePart>());
                Assert.Single(part.notes);
                await window.EnsurePianoRollAsync();
                vm.ShowPianoRoll = true;
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, DocManager.Inst.Project, 0)); HeadlessUi.Flush();
                var notes = vm.TracksViewModel.PianoRollOpenPart as UVoicePart;
                Assert.Same(part, notes);
                vm.Undo(); HeadlessUi.Flush();
                Assert.Empty(vm.TracksViewModel.Parts);
                vm.Redo(); HeadlessUi.Flush();
                Assert.Contains(part, vm.TracksViewModel.Parts);
                Assert.Single(part.notes);
            });
        }

        [Fact]
        public async Task DisplayHistoryUsesAtomicRotationAndCanBeDeleted() {
            var directory = Path.Combine(Path.GetTempPath(), "OpenUtau-history-test-" + Guid.NewGuid().ToString("N"));
            var store = new AssistantHistoryStore(directory);
            try {
                var value = new AssistantHistory(1, "codex", "native-verified", "project-old", "draft text", new[] { new AssistantHistoryMessage("user", "You", "Original lyric request", DateTimeOffset.UtcNow, "operation-1", "job-1", "artifact-1") }, "verified-model", "high");
                await store.SaveAsync(value, 100, TestContext.Current.CancellationToken);
                await store.SaveAsync(value with { Draft = "new draft" }, 100, TestContext.Current.CancellationToken);
                Assert.Equal("new draft", store.Read()!.Draft);
                Assert.Equal("verified-model", store.Read()!.ModelId);
                Assert.Equal("high", store.Read()!.EffortId);
                Assert.Equal("operation-1", store.Read()!.Messages[0].OperationId);
                Assert.Equal("job-1", store.Read()!.Messages[0].JobId);
                Assert.Equal("artifact-1", store.Read()!.Messages[0].ArtifactId);
                Assert.True(File.Exists(Path.Combine(directory, "history.json.previous")));
                var largeText = new string('歌', 65536);
                var largeHistory = value with { Messages = Enumerable.Range(0, 1000).Select(i => new AssistantHistoryMessage("assistant", "message" + i, largeText, DateTimeOffset.UtcNow)).ToArray() };
                await store.SaveAsync(largeHistory, 1000, TestContext.Current.CancellationToken);
                Assert.InRange(new FileInfo(Path.Combine(directory, "history.json")).Length, 1, 8 * 1024 * 1024);
                var retained = store.Read();
                Assert.NotNull(retained);
                Assert.Equal("message999", retained.Messages.Last().Title);
                Assert.InRange(retained.Messages.Count, 1, 999);
                Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
                await store.ClearAsync();
                Assert.Null(store.Read());
            } finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
        }
    }
}
