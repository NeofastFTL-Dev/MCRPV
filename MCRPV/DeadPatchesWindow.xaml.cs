using System.IO;
using System.Windows;

namespace Mcrpv;

/// <summary>
/// De dode patches als buglijst: per script of datapack gegroepeerd, met per regel de
/// oorzaak (dump/parser-gat, nooit toegevoegd, naamvorm of dubbel). De lijst wordt bij
/// elke index geschreven, dus dit venster leest alleen het bestand.
/// </summary>
public partial class DeadPatchesWindow : Window
{
    public DeadPatchesWindow()
    {
        InitializeComponent();

        string path = IndexCommands.DeadPatchesFile(IndexCommands.DataFolder);

        if (!File.Exists(path))
        {
            SummaryText.Text = Lang.T("dead.nolist");
            BodyText.Text = "";
            return;
        }

        string text = File.ReadAllText(path);
        string[] lines = text.Split('\n');

        SummaryText.Text = lines.Length > 0 ? lines[0].Trim() : Lang.T("dead.fallback");
        BodyText.Text = text;
    }
}
