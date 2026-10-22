using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NimbusSync.App.Services;
using NimbusSync.App.ViewModels;

namespace NimbusSync.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = AppIcon.WindowIcon;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // PasswordBox can't bind; clear it once the view model has stored the secret.
        if (e.PropertyName == nameof(MainViewModel.ClientSecretInput) && _vm.ClientSecretInput.Length == 0)
            SecretBox.Clear();
    }

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e) => _vm.ClientSecretInput = SecretBox.Password;

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
