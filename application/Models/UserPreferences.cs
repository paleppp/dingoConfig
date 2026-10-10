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
    public CanBitRate SelectedBitrate { get; set; }
    public NumberFormat IdFormat { get; set; }
}
