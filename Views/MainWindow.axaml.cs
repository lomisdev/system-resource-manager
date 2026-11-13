//Views: MainWindow.axaml.cs
using Avalonia.Controls;
using System_Resource_Manager.ViewModels;

namespace System_Resource_Manager.Views;


public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as MainViewModel)?.Stop();
    }

}