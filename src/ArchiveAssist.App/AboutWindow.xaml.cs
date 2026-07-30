using System.Windows;

namespace ArchiveAssist.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = typeof(AboutWindow).Assembly.GetName().Version;
        VersionText.Text = version is null ? "Version 1.0" : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }
}
