using System.Windows;
using NimbusSync.App.ViewModels;

namespace NimbusSync.App.Views;

public sealed class WpfUiServices(Func<Window?> owner) : IUiServices
{
    public bool ShowProfileEditor(ProfileEditorViewModel editor)
    {
        var window = new ProfileEditorWindow(editor);
        if (VisibleOwner() is { } o) window.Owner = o;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }

    public bool Confirm(string title, string message)
        => Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void ShowError(string title, string message)
        => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        => VisibleOwner() is { } o
            ? MessageBox.Show(o, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);

    private Window? VisibleOwner() => owner() is { IsVisible: true } w ? w : null;
}
