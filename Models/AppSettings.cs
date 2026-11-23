using System.Globalization;

namespace StellarModManager.Models;

public class AppSettings
{
    public string Language { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    public bool ConfirmBeforeRemove { get; set; } = true;
    public bool AutoCheckForModUpdates { get; set; } = true;
    public bool AutoCheckForAppUpdates { get; set; } = true;
    public string Theme { get; set; } = "Purple";
    public int ModSort { get; set; }
    public int? CatalogSort { get; set; }
    public string? CatalogType { get; set; }
    public bool CompactModList { get; set; }
    public bool AutoDeployDependencies { get; set; } = false;
}
