using System.Windows;

namespace Mcrpv;

/// <summary>
/// Toont welke laag, welk bestand en welke regel het recept van dit item bepaalden,
/// plus alle bewerkingen die het item raakten (PLAN.md §7).
/// </summary>
public partial class ProvenanceWindow : Window
{
    public ProvenanceWindow(string item, IReadOnlyDictionary<string, Recipe> recipes)
    {
        InitializeComponent();

        var report = ProvenanceReport.Explain(IndexCommands.DataFolder, item, recipes);

        ItemText.Text = report.Item;
        LayerText.Text = Lang.T("prov.layer", report.Layer);
        DetailsText.Text = report.Details;
        SourceText.Text = report.Source.Length > 0
            ? Lang.T("prov.source", report.Source) +
              (report.FromFold ? Lang.T("prov.fromfold") : "") +
              Lang.T("prov.ops", report.Ops)
            : Lang.T("prov.nosource") + Lang.T("prov.ops", report.Ops);

        if (report.Lines.Count == 0)
        {
            report.Lines.Add(new ProvenanceLine
            {
                Text = report.Layer == "geen recept"
                    ? Lang.T("prov.nolines")
                    : Lang.T("prov.fromlayer", report.Layer)
            });
        }

        Lines.ItemsSource = report.Lines;
    }
}
