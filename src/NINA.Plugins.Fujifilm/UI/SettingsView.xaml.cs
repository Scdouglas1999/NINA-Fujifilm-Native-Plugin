using System.Windows.Controls;

namespace NINA.Plugins.Fujifilm.UI;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // Settings bind straight into the shared FujiSettings instance, so a change takes effect
        // immediately but is only written to disk by an explicit Save. Persist on unload as well,
        // otherwise a setting changed without pressing Save silently reverts on the next launch.
        Unloaded += (_, _) => (DataContext as SettingsViewModel)?.SaveSettings();
    }
}
