using System.Windows;
using System.Windows.Input;

namespace Mcrpv;

/// <summary>
/// Laat de gebruiker zelf de Minecraft-instance kiezen waarin JEI zijn bookmarks
/// bewaart. Toont per instance de laatste wijzigingstijd, zodat je kunt zien welke
/// het meest recent is.
/// </summary>
public partial class InstancePickerWindow : Window
{
    public InstancePickerWindow()
    {
        InitializeComponent();
        RefreshList();
    }

    /// <summary>De gekozen instance (null als er niets gekozen is).</summary>
    public MinecraftBookmarkFile? Selected => InstanceList.SelectedItem as MinecraftBookmarkFile;

    private void RefreshList()
    {
        List<MinecraftBookmarkFile> found;

        try
        {
            found = MinecraftBookmarks.FindAll();
        }
        catch (Exception ex)
        {
            InstanceList.ItemsSource = null;
            UseButton.IsEnabled = false;
            StatusText.Text = Lang.T("inst.failed", ex.Message);
            return;
        }

        InstanceList.ItemsSource = found;
        UseButton.IsEnabled = found.Count > 0;

        if (found.Count > 0)
            InstanceList.SelectedIndex = 0;

        StatusText.Text = found.Count == 0
            ? Lang.T("inst.none")
            : Lang.T("inst.found", found.Count);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        DialogResult = true;
    }

    private void InstanceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null)
            DialogResult = true;
    }
}
