using System;

namespace Mcrpv
{
    /// <summary>
    /// Het rekenwerk achter de bracket-zoom staat hier bij elkaar, zodat de UI en
    /// <c>--selftest</c> gegarandeerd dezelfde regels controleren. Een klik op een
    /// ingrediënt gebruikte eerder <c>Math.Clamp(fromZoom * 1.4, fromZoom, 1.5)</c>;
    /// stond de bracket al boven 150%, dan was de ondergrens groter dan de bovengrens en
    /// sloeg de app vast met "UI-fout: '4' cannot be greater than 1.5." (zie crash.log).
    /// </summary>
    public static class BracketZoom
    {
        public const double Min = 0.1;
        public const double Max = 4.0;

        /// <summary>Hoeveel dichterbij één klik op een ingrediënt mag zoomen.</summary>
        public const double ClickStep = 1.4;

        /// <summary>
        /// Houdt een zoomwaarde binnen de grenzen. Bewust met vaste grenzen: een clamp
        /// met een variabele ondergrens kan omklappen (min &gt; max) en gooit dan een
        /// ArgumentException midden in een klik.
        /// </summary>
        public static double Clamp(double zoom)
        {
            if (double.IsNaN(zoom)) return 1.0;
            return Math.Clamp(zoom, Min, Max);
        }

        /// <summary>
        /// Zoom na een klik op een ingrediënt: nooit verder uitzoomen dan nu, vanuit ver
        /// uitgezoomd eerst naar 100%, en daarna hoogstens <see cref="ClickStep"/>× dichterbij.
        /// </summary>
        public static double Target(double fromZoom)
        {
            fromZoom = Clamp(fromZoom);
            return Clamp(Math.Min(Math.Max(fromZoom * ClickStep, 1.0), Max));
        }
    }
}
