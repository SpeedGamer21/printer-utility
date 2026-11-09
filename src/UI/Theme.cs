using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PrinterUtility
{
    public enum Tone { Neutral, Accent, Success, Warning, Error }

    /// <summary>
    /// Windows 11 style light/dark palette built from the user's theme and accent colour.
    /// Every brush is referenced with DynamicResource, so re-applying switches the theme live.
    /// </summary>
    internal static class Theme
    {
        public static bool IsDark { get; private set; }
        public static event Action Changed;

        private static ResourceDictionary _res;

        public static void Apply(ResourceDictionary res)
        {
            _res = res;
            Reload();
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                    Application.Current?.Dispatcher.BeginInvoke((Action)(() => { Reload(); Changed?.Invoke(); }));
            };
        }

        private static void Reload()
        {
            IsDark = Session.ForceDark ?? ReadAppsUseDark();
            var (light2, dark1) = ReadAccent();
            var accent = IsDark ? light2 : dark1;
            var onAccent = Luminance(accent) > 0.5 ? Colors.Black : Colors.White;

            if (IsDark)
            {
                Set("Bg", "#202020");
                Set("CardBg", "#2B2B2B");
                Set("CardHover", "#323232");
                Set("CardBorder", "#1C1C1C");
                Set("Control", "#2D2D2D");
                Set("ControlHover", "#383838");
                Set("ControlPressed", "#272727");
                Set("ControlBorder", "#3B3B3B");
                Set("ControlStrong", "#9A9A9A");
                Set("Subtle", "#0FFFFFFF");
                Set("SubtleHover", "#15FFFFFF");
                Set("Text", "#FFFFFF");
                Set("TextSub", "#C8C8C8");
                Set("TextMuted", "#8A8A8A");
                Set("Divider", "#333333");
                Set("Scrim", "#99000000");
                Set("Dialog", "#2B2B2B");
                Set("ScrollThumbBg", "#66FFFFFF");
                Set("SuccessFg", "#6CCB5F"); Set("SuccessBg", "#1F3A1F");
                Set("WarningFg", "#FCE100"); Set("WarningBg", "#433519");
                Set("ErrorFg", "#FF99A4");   Set("ErrorBg", "#442726");
                Set("NeutralFg", "#C8C8C8"); Set("NeutralBg", "#383838");
            }
            else
            {
                Set("Bg", "#F3F3F3");
                Set("CardBg", "#FBFBFB");
                Set("CardHover", "#F6F6F6");
                Set("CardBorder", "#E5E5E5");
                Set("Control", "#FFFFFF");
                Set("ControlHover", "#F9F9F9");
                Set("ControlPressed", "#F0F0F0");
                Set("ControlBorder", "#E0E0E0");
                Set("ControlStrong", "#8A8A8A");
                Set("Subtle", "#09000000");
                Set("SubtleHover", "#0F000000");
                Set("Text", "#1B1B1B");
                Set("TextSub", "#5D5D5D");
                Set("TextMuted", "#8C8C8C");
                Set("Divider", "#E5E5E5");
                Set("Scrim", "#66000000");
                Set("Dialog", "#FFFFFF");
                Set("ScrollThumbBg", "#70000000");
                Set("SuccessFg", "#0F7B0F"); Set("SuccessBg", "#DFF6DD");
                Set("WarningFg", "#9D5D00"); Set("WarningBg", "#FFF4CE");
                Set("ErrorFg", "#C42B1C");   Set("ErrorBg", "#FDE7E9");
                Set("NeutralFg", "#5D5D5D"); Set("NeutralBg", "#EBEBEB");
            }

            Set("Accent", accent);
            Set("AccentHover", Mix(accent, IsDark ? Colors.Black : Colors.White, 0.12));
            Set("AccentPressed", Mix(accent, IsDark ? Colors.Black : Colors.White, 0.25));
            Set("OnAccent", onAccent);
            Set("AccentSoft", Color.FromArgb((byte)(IsDark ? 0x40 : 0x22), accent.R, accent.G, accent.B));
            Set("AccentFg", accent);
        }

        public static Brush Fg(Tone t) => Brush(t == Tone.Accent ? "AccentFg" : t + "Fg");
        public static Brush BgOf(Tone t) => Brush(t == Tone.Accent ? "AccentSoft" : t + "Bg");
        public static Brush Brush(string key) => (Brush)_res[key];
        public static Color ColorOf(string key) => ((SolidColorBrush)_res[key]).Color;

        /// <summary>Dark title bar + caption colour that blends with the window (Windows 11; ignored on 10).</summary>
        public static void StyleTitleBar(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);        // DWMWA_USE_IMMERSIVE_DARK_MODE
            var bg = ColorOf("Bg");
            int colorRef = bg.R | (bg.G << 8) | (bg.B << 16);
            Native.DwmSetWindowAttribute(hwnd, 35, ref colorRef, 4);    // DWMWA_CAPTION_COLOR
        }

        private static void Set(string key, string hex) => Set(key, (Color)ColorConverter.ConvertFromString(hex));

        private static void Set(string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            _res[key] = b;
        }

        private static bool ReadAppsUseDark()
        {
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return v is int i && i == 0;
            }
            catch { return false; }
        }

        /// <summary>(Light2, Dark1) shades of the Windows accent - what Windows 11 uses for dark/light mode.</summary>
        private static (Color light2, Color dark1) ReadAccent()
        {
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) is byte[] p
                    && p.Length >= 32)
                    return (Color.FromRgb(p[4], p[5], p[6]), Color.FromRgb(p[16], p[17], p[18]));
            }
            catch { /* fall back */ }
            return ((Color)ColorConverter.ConvertFromString("#4CC2FF"), (Color)ColorConverter.ConvertFromString("#0067C0"));
        }

        private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    }
}
