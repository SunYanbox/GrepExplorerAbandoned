using System.Text.Json;

namespace Infrastructure;

/// <summary>
/// 应用配置（config.json）。历史去重/清理/大文件阈值等可配置项。
/// </summary>
public sealed class AppConfig
{
    /// <summary>历史上限条数，-1 表示无上限，超出时滚动删除最旧。</summary>
    public int HistoryLimit { get; set; } = -1;

    /// <summary>单文件大小阈值（MB），超过则跳过并提示。</summary>
    public double LargeFileThresholdMB { get; set; } = 10;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrepExplorer");

    public static AppConfig Load(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "config.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppConfig();
    }

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
