// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StormRemoteControl.Models;
using Windows.UI;

namespace StormRemoteControl.Services
{
    /// <summary>
    /// Represents color definition of a visual theme.
    /// </summary>
    public record StormThemeDefinition(
        string Id,
        string Name,
        string Description,
        Color AccentColor,
        Color BackgroundColor,
        Color CardBackgroundColor,
        Color BorderColor,
        Color TextPrimaryColor,
        Color TextSecondaryColor,
        Color PulsingGreenColor,
        ElementTheme RequestedTheme
    );

    /// <summary>
    /// Unified theme manager implementing 8 standardized STORM SOFT themes.
    /// Dynamically updates Application.Current.Resources at runtime.
    /// </summary>
    public static class ThemeManager
    {
        public static event Action? ThemeChanged;

        private static string _currentTheme = "STORM DARK";

        public static string CurrentTheme
        {
            get => _currentTheme;
            set
            {
                if (Themes.ContainsKey(value))
                {
                    _currentTheme = value;
                    AppSettings.ActiveTheme = value;
                    AppSettings.Save();
                    ApplyTheme(value);
                    ThemeChanged?.Invoke();
                }
            }
        }

        public static readonly Dictionary<string, StormThemeDefinition> Themes = new()
        {
            ["STORM DARK"] = new(
                Id: "STORM DARK",
                Name: "STORM DARK",
                Description: "Тёмный кибер (сланцевые тона, акцент Cyan #00D2FF)",
                AccentColor: ColorHelper.FromArgb(255, 0, 210, 255),      // #00D2FF
                BackgroundColor: ColorHelper.FromArgb(255, 11, 14, 20),     // #0B0E14
                CardBackgroundColor: ColorHelper.FromArgb(255, 21, 27, 38), // #151B26
                BorderColor: ColorHelper.FromArgb(255, 36, 48, 68),        // #243044
                TextPrimaryColor: ColorHelper.FromArgb(255, 255, 255, 255),
                TextSecondaryColor: ColorHelper.FromArgb(180, 255, 255, 255),
                PulsingGreenColor: ColorHelper.FromArgb(255, 16, 185, 129),
                RequestedTheme: ElementTheme.Dark
            ),
            ["STORM NIGHT"] = new(
                Id: "STORM NIGHT",
                Name: "STORM NIGHT",
                Description: "Чёрный OLED (глубокий чёрный, чистый неон #00F0FF)",
                AccentColor: ColorHelper.FromArgb(255, 0, 240, 255),      // #00F0FF
                BackgroundColor: ColorHelper.FromArgb(255, 10, 11, 16),     // #0A0B10
                CardBackgroundColor: ColorHelper.FromArgb(255, 18, 19, 26), // #12131A
                BorderColor: ColorHelper.FromArgb(255, 31, 34, 48),        // #1F2230
                TextPrimaryColor: ColorHelper.FromArgb(255, 255, 255, 255),
                TextSecondaryColor: ColorHelper.FromArgb(170, 255, 255, 255),
                PulsingGreenColor: ColorHelper.FromArgb(255, 0, 255, 157),
                RequestedTheme: ElementTheme.Dark
            ),
            ["STORM DAY"] = new(
                Id: "STORM DAY",
                Name: "STORM DAY",
                Description: "Светлая тема (чистый светлый фон, акцент #0284C7)",
                AccentColor: ColorHelper.FromArgb(255, 2, 132, 199),      // #0284C7
                BackgroundColor: ColorHelper.FromArgb(255, 248, 250, 252), // #F8FAFC
                CardBackgroundColor: ColorHelper.FromArgb(255, 255, 255, 255),
                BorderColor: ColorHelper.FromArgb(255, 226, 232, 240),     // #E2E8F0
                TextPrimaryColor: ColorHelper.FromArgb(255, 15, 23, 42),   // #0F172A
                TextSecondaryColor: ColorHelper.FromArgb(200, 71, 85, 105), // #475569
                PulsingGreenColor: ColorHelper.FromArgb(255, 5, 150, 105),
                RequestedTheme: ElementTheme.Light
            ),
            ["STORM MIDNIGHT"] = new(
                Id: "STORM MIDNIGHT",
                Name: "STORM MIDNIGHT",
                Description: "Аметистовая ночь (тёмно-фиолетовый, акцент #A855F7)",
                AccentColor: ColorHelper.FromArgb(255, 168, 85, 247),     // #A855F7
                BackgroundColor: ColorHelper.FromArgb(255, 21, 17, 43),     // #15112B
                CardBackgroundColor: ColorHelper.FromArgb(255, 30, 24, 61), // #1E183D
                BorderColor: ColorHelper.FromArgb(255, 50, 40, 100),       // #322864
                TextPrimaryColor: ColorHelper.FromArgb(255, 255, 255, 255),
                TextSecondaryColor: ColorHelper.FromArgb(180, 230, 220, 255),
                PulsingGreenColor: ColorHelper.FromArgb(255, 16, 185, 129),
                RequestedTheme: ElementTheme.Dark
            ),
            ["STORM MATRIX"] = new(
                Id: "STORM MATRIX",
                Name: "STORM MATRIX",
                Description: "Матричный зелёный (кибер-неон #00FF66 на изумрудном)",
                AccentColor: ColorHelper.FromArgb(255, 0, 255, 102),      // #00FF66
                BackgroundColor: ColorHelper.FromArgb(255, 10, 20, 15),     // #0A140F
                CardBackgroundColor: ColorHelper.FromArgb(255, 16, 34, 25), // #102219
                BorderColor: ColorHelper.FromArgb(255, 24, 59, 42),        // #183B2A
                TextPrimaryColor: ColorHelper.FromArgb(255, 230, 255, 235),
                TextSecondaryColor: ColorHelper.FromArgb(180, 160, 240, 180),
                PulsingGreenColor: ColorHelper.FromArgb(255, 0, 255, 102),
                RequestedTheme: ElementTheme.Dark
            ),
            ["STORM CYBERPUNK"] = new(
                Id: "STORM CYBERPUNK",
                Name: "STORM CYBERPUNK",
                Description: "Неон Найт-Сити (ярко-розовый #FF007F на ультрафиолете)",
                AccentColor: ColorHelper.FromArgb(255, 255, 0, 127),      // #FF007F
                BackgroundColor: ColorHelper.FromArgb(255, 24, 11, 36),     // #180B24
                CardBackgroundColor: ColorHelper.FromArgb(255, 38, 18, 56), // #261238
                BorderColor: ColorHelper.FromArgb(255, 64, 30, 94),        // #401E5E
                TextPrimaryColor: ColorHelper.FromArgb(255, 255, 255, 255),
                TextSecondaryColor: ColorHelper.FromArgb(180, 255, 180, 220),
                PulsingGreenColor: ColorHelper.FromArgb(255, 0, 240, 255),
                RequestedTheme: ElementTheme.Dark
            ),
            ["STORM FANTASY"] = new(
                Id: "STORM FANTASY",
                Name: "STORM FANTASY",
                Description: "Королевское золото (благородный золотой #F59E0B)",
                AccentColor: ColorHelper.FromArgb(255, 245, 158, 11),     // #F59E0B
                BackgroundColor: ColorHelper.FromArgb(255, 18, 19, 26),     // #12131A
                CardBackgroundColor: ColorHelper.FromArgb(255, 28, 30, 41), // #1C1E29
                BorderColor: ColorHelper.FromArgb(255, 47, 51, 70),        // #2F3346
                TextPrimaryColor: ColorHelper.FromArgb(255, 255, 255, 255),
                TextSecondaryColor: ColorHelper.FromArgb(180, 240, 230, 200),
                PulsingGreenColor: ColorHelper.FromArgb(255, 16, 185, 129),
                RequestedTheme: ElementTheme.Dark
            ),
            ["STORM WARHAMMER 40K"] = new(
                Id: "STORM WARHAMMER 40K",
                Name: "STORM WARHAMMER 40K",
                Description: "Имперская готика (готическое золото #D4AF37 на титане)",
                AccentColor: ColorHelper.FromArgb(255, 212, 175, 55),     // #D4AF37
                BackgroundColor: ColorHelper.FromArgb(255, 20, 23, 26),     // #14171A
                CardBackgroundColor: ColorHelper.FromArgb(255, 31, 36, 41), // #1F2429
                BorderColor: ColorHelper.FromArgb(255, 54, 62, 71),        // #363E47
                TextPrimaryColor: ColorHelper.FromArgb(255, 245, 245, 245),
                TextSecondaryColor: ColorHelper.FromArgb(180, 200, 190, 170),
                PulsingGreenColor: ColorHelper.FromArgb(255, 16, 185, 129),
                RequestedTheme: ElementTheme.Dark
            )
        };

        public static void Initialize()
        {
            AppSettings.Load();
            string savedTheme = AppSettings.ActiveTheme;
            if (string.IsNullOrEmpty(savedTheme) || !Themes.ContainsKey(savedTheme))
            {
                savedTheme = "STORM DARK";
            }
            _currentTheme = savedTheme;
            ApplyTheme(_currentTheme);
        }

        public static void ApplyTheme(string themeId)
        {
            if (!Themes.TryGetValue(themeId, out var def)) return;

            var res = Application.Current.Resources;
            res["AccentColorBrush"] = new SolidColorBrush(def.AccentColor);
            res["DarkBgBrush"] = new SolidColorBrush(def.BackgroundColor);
            res["CardBgBrush"] = new SolidColorBrush(def.CardBackgroundColor);
            res["BorderBrush"] = new SolidColorBrush(def.BorderColor);
            res["PulsingGreenBrush"] = new SolidColorBrush(def.PulsingGreenColor);
            res["TextFillColorPrimaryBrush"] = new SolidColorBrush(def.TextPrimaryColor);
            res["TextFillColorSecondaryBrush"] = new SolidColorBrush(def.TextSecondaryColor);

            if (App.MainWindow?.Content is FrameworkElement root)
            {
                root.RequestedTheme = def.RequestedTheme;
            }
        }
    }
}
