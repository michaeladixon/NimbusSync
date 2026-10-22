using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using NimbusSync.App.ViewModels;

namespace NimbusSync.App.Services;

/// <summary>Notification-area icon: status at a glance, quick actions, and conflict toasts.</summary>
public sealed class TrayIconController : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _resumeItem;
    private readonly MenuItem _syncItem;
    private StatusKind? _shownKind;

    public TrayIconController(MainViewModel vm, Action showWindow, Action exit)
    {
        _vm = vm;

        var open = new MenuItem { Header = "Open NimbusSync", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => showWindow();
        _syncItem = new MenuItem { Header = "Sync all now", Command = vm.SyncAllCommand };
        _pauseItem = new MenuItem { Header = "Pause syncing", Command = vm.PauseAllCommand };
        _resumeItem = new MenuItem { Header = "Resume syncing", Command = vm.ResumeAllCommand };
        var quit = new MenuItem { Header = "Quit NimbusSync" };
        quit.Click += (_, _) => exit();

        _icon = new TaskbarIcon
        {
            ContextMenu = new ContextMenu { Items = { open, new Separator(), _syncItem, _pauseItem, _resumeItem, new Separator(), quit } },
            NoLeftClickDelay = true
        };
        _icon.TrayLeftMouseUp += (_, _) => showWindow();
        _icon.ContextMenu.Opened += (_, _) => UpdateMenu();

        vm.OverallStatusChanged += (_, _) => UpdateIcon();
        vm.ConflictRaised += (_, c) =>
            _icon.ShowBalloonTip("Sync conflict", $"{c.FileName} changed on this PC and in Drive. Open NimbusSync to choose a version.", BalloonIcon.Warning);

        UpdateIcon();
    }

    private void UpdateIcon()
    {
        if (_shownKind != _vm.OverallKind)
        {
            _icon.Icon = AppIcon.TrayIcon(_vm.OverallKind);
            _shownKind = _vm.OverallKind;
        }
        _icon.ToolTipText = $"NimbusSync · {_vm.OverallText}";
    }

    private void UpdateMenu()
    {
        var any = _vm.Profiles.Count > 0;
        _syncItem.IsEnabled = _vm.AnyRunning;
        _pauseItem.Visibility = _vm.AnyRunning ? Visibility.Visible : Visibility.Collapsed;
        _resumeItem.Visibility = _vm.AnyRunning ? Visibility.Collapsed : Visibility.Visible;
        _resumeItem.IsEnabled = any && _vm.IsSignedIn;
    }

    public void Dispose() => _icon.Dispose();
}
