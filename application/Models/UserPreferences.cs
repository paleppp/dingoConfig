using domain.Enums;

namespace application.Models;

public class UserPreferences
{
    public string WorkingDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "dingoConfig");

    public string? SelectedAdapter { get; set; }
    public string? SelectedPort { get; set; }
    public string WifiAddress { get; set; } = "192.168.4.1";
    public string? WifiBridge { get; set; }
    public bool WifiManualAddress { get; set; }
    public CanBitRate SelectedBitrate { get; set; } = CanBitRate.BitRate500K; // dingoPDM's own default
    public NumberFormat IdFormat { get; set; }

    /// <summary>Most recently used first.</summary>
    public List<RecentProject> RecentProjects { get; set; } = [];
}
