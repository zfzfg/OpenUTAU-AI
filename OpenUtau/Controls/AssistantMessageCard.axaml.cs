using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpenUtau.App.ViewModels;

namespace OpenUtau.App.Controls {
    public partial class AssistantMessageCard : UserControl {
        public AssistantMessageCard() { InitializeComponent(); }
        AssistantSidebarViewModel? Sidebar => this.FindAncestorOfType<AssistantSidebar>()?.DataContext as AssistantSidebarViewModel;
        AssistantMessageViewModel? Card => DataContext as AssistantMessageViewModel;
        async Task Run(Func<AssistantSidebarViewModel, AssistantMessageViewModel, Task> action) {
            if (Sidebar is { } sidebar && Card is { } card) {
                try { await action(sidebar, card); } catch (Exception ex) { card.Status = ex.Message; }
            }
        }
        async void OnOption(object? sender, RoutedEventArgs args) => await Run((sidebar, card) => sidebar.RespondAsync(card, (sender as Button)?.Tag as string));
        async void OnAnswer(object? sender, RoutedEventArgs args) => await Run((sidebar, card) => sidebar.RespondAsync(card));
        async void OnApply(object? sender, RoutedEventArgs args) => await Run((sidebar, card) => sidebar.ApplyAsync(card));
        void OnReject(object? sender, RoutedEventArgs args) { if (Card is { } card) { Sidebar?.Reject(card); } }
        async void OnShow(object? sender, RoutedEventArgs args) => await Run(async (sidebar, card) => { if (card.Result is { } result && sidebar.ShowInEditor != null) { await sidebar.ShowInEditor(result); } });
        async void OnPlay(object? sender, RoutedEventArgs args) => await Run(async (sidebar, card) => { if (card.Result is { } result && sidebar.PlayPreview != null) { await sidebar.PlayPreview(result); } });
        void OnStop(object? sender, RoutedEventArgs args) => Sidebar?.StopPreview?.Invoke();
        void OnPrevious(object? sender, RoutedEventArgs args) { if (Card is { } card) { card.ReviewPage--; } }
        void OnNext(object? sender, RoutedEventArgs args) { if (Card is { } card) { card.ReviewPage++; } }
        async void OnUndo(object? sender, RoutedEventArgs args) => await Run((sidebar, card) => sidebar.RevertAsync(card));
    }
}
