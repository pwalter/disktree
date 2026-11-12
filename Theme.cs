using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiskTree;

/// <summary>
/// Light / dark colours. Follows the Windows setting "Choose your default app mode" and switches live.
/// Standard controls come from WPF's Fluent theme (Application.ThemeMode); the colours drawn by DiskTree itself
/// are DynamicResources set here, plus the brushes used by <see cref="NodeVM"/> and <see cref="PieChart"/>.
/// </summary>
public enum ThemeSetting { System, Light, Dark }

public static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; }

    private static ThemeSetting _setting;

    /// <summary>Follow Windows, or force light / dark.</summary>
    public static ThemeSetting Setting
    {
        get => _setting;
        set
        {
            _setting = value;
            Apply();
        }
    }

    /// <summary>Raised on the UI thread after the colours were switched.</summary>
    public static event Action? Changed;

    public static Color PanelBg => IsDark ? Rgb(0x25, 0x28, 0x2D) : Colors.White;
    public static Color TrackBg => IsDark ? Rgb(0x3B, 0x41, 0x48) : Rgb(0xE6, 0xE8, 0xEC);
    public static Color Ink => IsDark ? Rgb(0xE8, 0xEA, 0xED) : Rgb(0x1F, 0x23, 0x29);
    public static Color Muted => IsDark ? Rgb(0x9A, 0xA1, 0xAA) : Rgb(0x6B, 0x72, 0x7B);

    public static void Init(ThemeSetting setting)
    {
        _setting = setting;
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_setting == ThemeSetting.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                Application.Current?.Dispatcher.InvokeAsync(Apply);
        };
    }

    private static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Apply()
    {
        bool dark = _setting switch
        {
            ThemeSetting.Light => false,
            ThemeSetting.Dark => true,
            _ => SystemIsDark(),
        };
        var app = Application.Current;
        if (app == null) return;

        bool first = app.Resources["WindowBg"] == null;
        if (!first && dark == IsDark) return;
        IsDark = dark;

#pragma warning disable WPF0001 // Fluent theme is still marked experimental
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        Set("WindowBg", dark ? Rgb(0x1B, 0x1D, 0x21) : Rgb(0xF5, 0xF6, 0xF8));
        Set("PanelBg", PanelBg);
        Set("PanelBorder", dark ? Rgb(0x3A, 0x3F, 0x46) : Rgb(0xDD, 0xE0, 0xE5));
        Set("TrackBg", TrackBg);
        Set("Ink", Ink);
        Set("Muted", Muted);
        Set("Subtle", dark ? Rgb(0x7D, 0x84, 0x8D) : Rgb(0x9A, 0xA0, 0xA8));
        Set("ToolGray", Rgb(0x5B, 0x6C, 0x7D));

        if (!first) Changed?.Invoke();
    }

    private static void Set(string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Application.Current.Resources[key] = b;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
