using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Rawr.App.ViewModels;

namespace Rawr.App.Dialogs;

/// <summary>
/// Borderless preview window meant for a secondary display. Mirrors PreviewImage
/// from the main view-model fit-to-screen, with focus-peaking and clipping overlays.
/// Closing here flips MainViewModel.ShowSecondMonitor off so the View-menu
/// checkbox stays in sync.
/// </summary>
public partial class SecondMonitorWindow : Window
{
    public ICommand CloseCommand { get; }

    public SecondMonitorWindow(MainViewModel vm)
    {
        DataContext = vm;
        CloseCommand = new RelayCommand(Close);
        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);
    }
}
