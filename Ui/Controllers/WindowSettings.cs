using System.Windows;
using System.Windows.Controls;
using DiskTree.Core;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>
/// The settings the window itself keeps: size and state, the theme button and the exclusion rules (with their
/// button). Reads from and writes to <see cref="AppSettings"/>; the window saves them on closing.
/// </summary>
public sealed class WindowSettings
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly Button _themeButton;
    private readonly Button _excludeButton;

    public WindowSettings(Window window, AppSettings settings, Button themeButton, Button excludeButton)
    {
        _window = window;
        _settings = settings;
        _themeButton = themeButton;
        _excludeButton = excludeButton;

        _themeButton.Click += (_, _) => NextTheme();
        _excludeButton.Click += (_, _) => EditExcludes();
        ShowThemeButton();
        ShowExcludeButton();
    }

    // ---- size and state

    /// <summary>Restores the size and state of the last run (called once while the window is built).</summary>
    public void ApplyBounds()
    {
        if (_settings.Width >= _window.MinWidth && _settings.Height >= _window.MinHeight)
        {
            _window.Width = Math.Min(_settings.Width, SystemParameters.WorkArea.Width);
            _window.Height = Math.Min(_settings.Height, SystemParameters.WorkArea.Height);
        }
        if (_settings.Maximized) _window.WindowState = WindowState.Maximized;
    }

    /// <summary>Remembers size and state (called when the window closes).</summary>
    public void CaptureBounds()
    {
        var bounds = _window.WindowState == WindowState.Maximized
            ? _window.RestoreBounds
            : new Rect(0, 0, _window.ActualWidth, _window.ActualHeight);
        _settings.Maximized = _window.WindowState == WindowState.Maximized;
        _settings.Width = bounds.Width;
        _settings.Height = bounds.Height;
    }

    // ---- theme

    private void NextTheme()
    {
        // Windows -> Hell -> Dunkel -> Windows ...
        _settings.Theme = _settings.Theme switch
        {
            ThemeSetting.System => ThemeSetting.Light,
            ThemeSetting.Light => ThemeSetting.Dark,
            _ => ThemeSetting.System,
        };
        Theme.Setting = _settings.Theme;
        ShowThemeButton();
    }

    private void ShowThemeButton() =>
        _themeButton.Content = _settings.Theme switch
        {
            ThemeSetting.Light => "☀ Hell",
            ThemeSetting.Dark => "🌙 Dunkel",
            _ => "🌗 Windows",
        };

    // ---- exclusions: saved ones plus those from the command line (--exclude)

    public ExcludeRules CurrentExcludes() => new(_settings.Excludes.Concat(App.Options.Excludes));

    private void ShowExcludeButton()
    {
        int n = _settings.Excludes.Count + App.Options.Excludes.Count;
        _excludeButton.Content = n > 0 ? $"Ausschlüsse ({n})…" : "Ausschlüsse…";
    }

    private void EditExcludes()
    {
        var dlg = new ExcludeDialog(_settings.Excludes) { Owner = _window };
        if (dlg.ShowDialog() != true) return;
        _settings.Excludes = dlg.Result;
        _settings.Save();
        ShowExcludeButton();
    }
}
