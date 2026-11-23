using System;
using System.Windows.Markup;

namespace Mcrpv;

/// <summary>
/// XAML: <c>Text="{loc:Tr btn.save}"</c>. De tekst wordt bij het laden opgehaald, dus na
/// een taalwissel moet het venster opnieuw opgebouwd worden (dat doet het hoofdvenster).
/// Staat bewust in een eigen bestand: <see cref="Lang"/> blijft zo vrij van WPF, zodat de
/// teksten ook in de testprojecten (en op Linux) te gebruiken zijn.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Lang.T(Key);
}
