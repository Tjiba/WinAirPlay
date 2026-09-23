using System.Text.Json;

namespace WinAirPlay;

internal sealed class Settings
{
    public string EndpointId { get; set; } = "";
    public string ReceiverId { get; set; } = "";
    public int TargetMs { get; set; } = 100;
    public int Volume { get; set; } = 50;
    public bool SharedVolume { get; set; } = true;
    public Dictionary<string, int> ReceiverVolumes { get; set; } = [];
    public bool StartMinimized { get; set; }
    public bool MinimizeOnClose { get; set; } = true;
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAirPlay");
    public static string LogPath => Path.Combine(Folder, "native-v2.log");
    private static string FilePath => Path.Combine(Folder, "settings-v2.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
