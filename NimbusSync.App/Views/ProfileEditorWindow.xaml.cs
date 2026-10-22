using System.Windows;
using Microsoft.Win32;
using NimbusSync.App.Services;
using NimbusSync.App.ViewModels;

namespace NimbusSync.App.Views;

public partial class ProfileEditorWindow : Window
{
    private readonly ProfileEditorViewModel _vm;

    public ProfileEditorWindow(ProfileEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = AppIcon.WindowIcon;
        vm.RequestClose += (_, result) => DialogResult = result;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder to sync" };
        if (!string.IsNullOrWhiteSpace(_vm.LocalRootPath)) dialog.InitialDirectory = _vm.LocalRootPath;
        if (dialog.ShowDialog(this) != true) return;

        _vm.LocalRootPath = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(_vm.Name)) _vm.Name = dialog.SafeFolderName;
    }
}
