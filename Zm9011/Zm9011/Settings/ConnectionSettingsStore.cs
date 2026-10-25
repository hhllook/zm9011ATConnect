using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Zm9011.Services.Interfaces;

namespace Zm9011.Settings
{
    /// <summary>上一次成功连接的串口参数。</summary>
    public class ConnectionSettings
    {
        public string PortName { get; set; } = string.Empty;

        public int BaudRate { get; set; } = 115200;
    }

    /// <summary>
    /// 连接参数本地存储：%LOCALAPPDATA%\Zm9011\settings.json。
    /// 启动时据此自动重连上次成功连接的串口。
    /// </summary>
    public static class ConnectionSettingsStore
    {
        private static readonly object Sync = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static string FolderPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zm9011");

        private static string FilePath => Path.Combine(FolderPath, "settings.json");

        /// <summary>读取上次连接参数；文件不存在或损坏时返回 null。</summary>
        public static ConnectionSettings? Load(ILogService log)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<ConnectionSettings>(json, JsonOptions);
                if (settings == null || string.IsNullOrWhiteSpace(settings.PortName))
                {
                    return null;
                }

                return settings;
            }
            catch (Exception ex)
            {
                log.Error($"[ConnectionSettingsStore.Load] 读取连接参数失败 file={FilePath} error={ex.Message} stack={ex.StackTrace}");
                return null;
            }
        }

        /// <summary>保存本次成功连接参数。</summary>
        public static void Save(ConnectionSettings settings, ILogService log)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(FolderPath);
                    var json = JsonSerializer.Serialize(settings, JsonOptions);
                    var tempPath = FilePath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, FilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    log.Error($"[ConnectionSettingsStore.Save] 保存连接参数失败 port={settings.PortName} baud={settings.BaudRate} error={ex.Message} stack={ex.StackTrace}");
                }
            }
        }
    }
}
