using System.Windows;
using System.Windows.Controls;

namespace EnshroudedPlanner;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        var s = SettingsStore.Current;
        ChkUpdatesOnStart.IsChecked = s.CheckForUpdatesOnStart;

        CmbTheme.SelectedIndex = s.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Current;

        s.CheckForUpdatesOnStart = ChkUpdatesOnStart.IsChecked == true;

        s.Theme = CmbTheme.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        };

        SettingsStore.Save();
        DialogResult = true;
        Close();
    }
}
