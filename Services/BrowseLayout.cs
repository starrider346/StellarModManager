using System;

namespace StellarModManager.Services;

internal sealed record CompactPageLayout(int Columns, int Rows, double CardHeight)
{
    internal int Capacity => Columns * Rows;
    internal double ImageHeight { get; init; }
    internal double MetadataHeight { get; init; }
}

public static class BrowseLayout
{
    public const double MinimumCardWidth = 246;
    public const double MetadataHeight = 166;
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
        var naturalImageHeight = imageWidth * 9 / 16;
        var minimumImageHeight = naturalImageHeight * 0.88;
        var maximumImageHeight = naturalImageHeight * 1.08;
        const double minimumMetadataHeight = 150;
        const double maximumMetadataHeight = 178;
        var minimumCardHeight = Math.Ceiling(
            (minimumImageHeight + minimumMetadataHeight + CardMarginAndBorder) * renderScaling) / renderScaling;
        var maximumCardHeight = Math.Floor(
            (maximumImageHeight + maximumMetadataHeight + CardMarginAndBorder) * renderScaling) / renderScaling;
        var rows = Math.Max(1, (int)Math.Floor(Math.Max(0, height) / minimumCardHeight));
        var availableRowHeight = Math.Floor(Math.Max(0, height) / rows * renderScaling) / renderScaling;
        var cardHeight = Math.Clamp(availableRowHeight, minimumCardHeight, maximumCardHeight);
        var contentHeight = cardHeight - CardMarginAndBorder;
        var adjustment = contentHeight - naturalImageHeight - MetadataHeight;
        var metadataHeight = Math.Clamp(MetadataHeight + adjustment / 2, minimumMetadataHeight, maximumMetadataHeight);
        var imageHeight = Math.Clamp(contentHeight - metadataHeight, minimumImageHeight, maximumImageHeight);
        metadataHeight = contentHeight - imageHeight;
        return new CompactPageLayout(columns, rows, cardHeight)
        {
            ImageHeight = imageHeight,
            MetadataHeight = metadataHeight
        };
    }
}
