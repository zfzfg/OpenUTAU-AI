using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenUtau.App.ViewModels;

namespace OpenUtau.App.Controls {
    public partial class AssistantSidebar : UserControl {
        AssistantSidebarViewModel? subscribed;
        public AssistantSidebar() {
            // Prevent transient inheritance of MainWindowViewModel while the parent's Assistant binding initializes.
            DataContext = null;
            InitializeComponent();
            Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
            DataContextChanged += (_, _) => {
                if (subscribed != null) { subscribed.ConversationChanged -= OnConversationChanged; }
                subscribed = DataContext as AssistantSidebarViewModel;
                if (subscribed != null) { subscribed.ConversationChanged += OnConversationChanged; }
            };
        }

        public bool OwnsFocus => IsKeyboardFocusWithin;

        void OnComposerKeyDown(object? sender, KeyEventArgs args) {
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None) { return; }
            // Avalonia exposes the active preedit on the text presenter. Let the IME commit it first.
            var presenter = Composer.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
            if (!string.IsNullOrEmpty(presenter?.PreeditText)) { return; }
            args.Handled = true;
            if (DataContext is AssistantSidebarViewModel vm) { _ = vm.SendAsync(); }
        }

        void OnConversationChanged() {
            var scroller = Conversation.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroller == null || scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 80) {
                Dispatcher.UIThread.Post(() => {
                    if (subscribed?.Messages.LastOrDefault() is { } last) { Conversation.ScrollIntoView(last); }
                }, DispatcherPriority.Background);
            }
        }

        void OnHide(object? sender, RoutedEventArgs args) { subscribed?.Hide?.Invoke(); }
        async void OnCopyConfiguration(object? sender, RoutedEventArgs args) {
            if (subscribed?.McpConfiguration == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) { return; }
            try { await clipboard.SetTextAsync(await subscribed.McpConfiguration()); } catch (Exception ex) { subscribed.ReportError(ex); }
        }
        async void OnAllowDirectory(object? sender, RoutedEventArgs args) {
            if (subscribed?.AllowDirectory == null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) { return; }
            try {
                var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = ThemeManager.GetString("assistant.allowfolder"), AllowMultiple = false });
                var folder = folders.FirstOrDefault()?.TryGetLocalPath();
                if (folder != null) { subscribed.AllowDirectory(folder); subscribed.RefreshDirectories(); }
            } catch (Exception ex) { subscribed.ReportError(ex); }
        }
    }
}
