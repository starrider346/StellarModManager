using System;

namespace StellarModManager.Services;

internal sealed record CompactPageLayout(int Columns, int Rows, double CardHeight)
{
    internal int Capacity => Columns * Rows;
}

internal static class BrowseLayout
{
    internal const double MinimumCardWidth = 225;
    internal const double MetadataHeight = 160;
    private const double CardMarginAndBorder = 14;

    internal static int CalculateListCapacity(double height, double renderScaling = 1)
    {
        var rowHeight = Math.Ceiling(66 * renderScaling) / renderScaling;
        return Math.Max(1, (int)Math.Floor(Math.Max(0, height) / rowHeight));
    }

    internal static CompactPageLayout Calculate(double width, double height, double renderScaling = 1)
    {
        var columns = Math.Max(1, (int)Math.Floor(width / MinimumCardWidth));
        var imageWidth = Math.Max(0, width / columns - CardMarginAndBorder);
        var cardHeight = Math.Ceiling((imageWidth * 9 / 16 + MetadataHeight + CardMarginAndBorder) * renderScaling) / renderScaling;
        var rows = Math.Max(1, (int)Math.Floor(Math.Max(0, height - 1) / cardHeight));
        return new CompactPageLayout(columns, rows, cardHeight);
    }
}
