// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StormRemoteControl.Models
{
    /// <summary>
    /// Global application settings with automatic JSON persistence.
    /// Stored in %LocalAppData%\StormRemoteControl\settings.json.
    /// </summary>
    public static class AppSettings
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StormRemoteControl");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

        // ── Connection ───────────────────────────────────────────────────

        public static string ConnectionProfile { get; set; } = "Баланс";
        public static int Port { get; set; } = 17700;
        public static string ConnectionPassword { get; set; } = "";
        public static string SignalingUrl { get; set; } = "wss://storm-signal-prod.loca.lt/ws";

        // ── Video ────────────────────────────────────────────────────────

        public static int MaxFps { get; set; } = 30;
        public static string MaxResolution { get; set; } = "Нативное";
        public static int SelectedMonitor { get; set; } = 0;

        // ── Security ─────────────────────────────────────────────────────

        public static bool RequireConfirmation { get; set; } = true;

        // ── Identity ─────────────────────────────────────────────────────

        public static string DeviceId { get; set; } = "";

        // ── Appearance & Language ────────────────────────────────────────

        public static string ActiveTheme { get; set; } = "STORM DARK";
        public static string ActiveLanguage { get; set; } = "ru";

        // ── Tray / Autostart ─────────────────────────────────────────────

        public static bool MinimizeToTray { get; set; } = true;
        public static bool AutoStart { get; set; } = false;
        public static bool StartMinimized { get; set; } = false;

        // ── Recent Connections ───────────────────────────────────────────

        public static string[] RecentConnections { get; set; } = Array.Empty<string>();

        // ── WoL ──────────────────────────────────────────────────────────

        public static string[] WolMacAddresses { get; set; } = Array.Empty<string>();

        // ═══════════════════════════════════════════════════════════════════
        //  PERSISTENCE
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Generate device ID on first launch, then load persisted settings.</summary>
        public static void InitializeDeviceId()
        {
            Load();
            if (string.IsNullOrEmpty(DeviceId))
            {
                var rng = new Random();
                DeviceId = $"{rng.Next(100, 999)} {rng.Next(100, 999)} {rng.Next(100, 999)}";
                Save();
            }
        }

        /// <summary>Force-generate a new random device ID and persist it.</summary>
        public static void RegenerateDeviceId()
        {
            var rng = new Random();
            DeviceId = $"{rng.Next(100, 999)} {rng.Next(100, 999)} {rng.Next(100, 999)}";
            Save();
        }

        /// <summary>Save all settings to disk as JSON.</summary>
        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var data = new SettingsData
                {
                    ConnectionProfile = ConnectionProfile,
                    Port = Port,
                    ConnectionPassword = ConnectionPassword,
                    MaxFps = MaxFps,
                    MaxResolution = MaxResolution,
                    SelectedMonitor = SelectedMonitor,
                    RequireConfirmation = RequireConfirmation,
                    DeviceId = DeviceId,
                    ActiveTheme = ActiveTheme,
                    ActiveLanguage = ActiveLanguage,
                    MinimizeToTray = MinimizeToTray,
                    AutoStart = AutoStart,
                    StartMinimized = StartMinimized,
                    RecentConnections = RecentConnections,
                    WolMacAddresses = WolMacAddresses,
                    SignalingUrl = SignalingUrl
                };
                string json = JsonSerializer.Serialize(data, JsonOpts);
                File.WriteAllText(SettingsFile, json);
            }
            catch { /* Non-critical: app still works without persistence */ }
        }

        /// <summary>Load settings from disk. Silent on error (keeps defaults).</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;

                string json = File.ReadAllText(SettingsFile);
                var data = JsonSerializer.Deserialize<SettingsData>(json, JsonOpts);
                if (data == null) return;

                ConnectionProfile = data.ConnectionProfile ?? "Баланс";
                Port = data.Port > 0 ? data.Port : 17700;
                ConnectionPassword = data.ConnectionPassword ?? "";
                MaxFps = data.MaxFps > 0 ? data.MaxFps : 30;
                MaxResolution = data.MaxResolution ?? "Нативное";
                SelectedMonitor = data.SelectedMonitor;
                RequireConfirmation = data.RequireConfirmation;
                DeviceId = data.DeviceId ?? "";
                ActiveTheme = data.ActiveTheme ?? "STORM DARK";
                ActiveLanguage = data.ActiveLanguage ?? "ru";
                MinimizeToTray = data.MinimizeToTray;
                AutoStart = data.AutoStart;
                StartMinimized = data.StartMinimized;
                RecentConnections = data.RecentConnections ?? Array.Empty<string>();
                WolMacAddresses = data.WolMacAddresses ?? Array.Empty<string>();
                SignalingUrl = data.SignalingUrl ?? "wss://storm-signal-prod.loca.lt/ws";
            }
            catch { /* Keep defaults on error */ }
        }

        /// <summary>Add a connection to recent history.</summary>
        public static void AddRecentConnection(string id)
        {
            var list = new System.Collections.Generic.List<string>(RecentConnections);
            list.Remove(id);
            list.Insert(0, id);
            if (list.Count > 10) list.RemoveRange(10, list.Count - 10);
            RecentConnections = list.ToArray();
            Save();
        }

        /// <summary>JSON-serializable settings data transfer object.</summary>
        private class SettingsData
        {
            public string? ConnectionProfile { get; set; }
            public int Port { get; set; }
            public string? ConnectionPassword { get; set; }
            public int MaxFps { get; set; }
            public string? MaxResolution { get; set; }
            public int SelectedMonitor { get; set; }
            public bool RequireConfirmation { get; set; }
            public string? DeviceId { get; set; }
            public string? ActiveTheme { get; set; }
            public string? ActiveLanguage { get; set; }
            public bool MinimizeToTray { get; set; }
            public bool AutoStart { get; set; }
            public bool StartMinimized { get; set; }
            public string[]? RecentConnections { get; set; }
            public string[]? WolMacAddresses { get; set; }
            public string? SignalingUrl { get; set; }
        }
    }
}
