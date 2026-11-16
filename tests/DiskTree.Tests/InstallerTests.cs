using System.IO;
using DiskTree.Features.Install;
using DiskTree.Infrastructure;
using Microsoft.Win32;
using Xunit;

namespace DiskTree.Tests;

/// <summary>Install / uninstall into temp folders and a throw-away HKCU key; the Explorer context menu is not touched.</summary>
public class InstallerTests : TestBase
{
    private readonly string _regKey = @"Software\DiskTree.Tests\Uninstall-" + Guid.NewGuid().ToString("N");
    private readonly InstallLayout _layout;
    private readonly string _source;

    public InstallerTests()
    {
        _layout = new InstallLayout(Path.Combine(T.Path, "Programs", "DiskTree"), Path.Combine(T.Path, "StartMenu"), _regKey);
        Directory.CreateDirectory(Path.Combine(T.Path, "Download"));
        _source = Path.Combine(T.Path, "Download", "DiskTree-slim.exe");
        File.WriteAllBytes(_source, new byte[4096]);
    }

    public override void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_regKey, throwOnMissingSubKey: false);
        base.Dispose();
    }

    [Fact]
    public void Install_CopiesExe_CreatesShortcut_AndAppsEntry()
    {
        Installer.Install(_source, contextMenu: false, _layout);

        Assert.True(File.Exists(_layout.ExePath));
        Assert.Equal(4096, new FileInfo(_layout.ExePath).Length);
        Assert.True(File.Exists(_layout.ShortcutPath));
        Assert.True(Installer.IsInstalled(_layout));

        using var k = Registry.CurrentUser.OpenSubKey(_regKey);
        Assert.NotNull(k);
        Assert.Equal("DiskTree", k!.GetValue("DisplayName"));
        Assert.Equal($"\"{_layout.ExePath}\" --uninstall", k.GetValue("UninstallString"));
        Assert.Equal(_layout.InstallDir, k.GetValue("InstallLocation"));
    }

    [Fact]
    public void Install_Twice_Overwrites()
    {
        Installer.Install(_source, contextMenu: false, _layout);
        File.WriteAllBytes(_source, new byte[8192]);
        Installer.Install(_source, contextMenu: false, _layout);
        Assert.Equal(8192, new FileInfo(_layout.ExePath).Length);
    }

    [Fact]
    public void Install_FromInstalledExe_KeepsFile()
    {
        Installer.Install(_source, contextMenu: false, _layout);
        Installer.Install(_layout.ExePath, contextMenu: false, _layout); // "repair" from the installed copy
        Assert.True(File.Exists(_layout.ExePath));
    }

    [Fact]
    public void Uninstall_RemovesEverything()
    {
        Installer.Install(_source, contextMenu: false, _layout);
        File.WriteAllText(_layout.ExePath + ".old", "");

        var report = Installer.Uninstall(_source, _layout);

        Assert.Contains("deinstalliert", report);
        Assert.False(File.Exists(_layout.ExePath));
        Assert.False(File.Exists(_layout.ExePath + ".old"));
        Assert.False(File.Exists(_layout.ShortcutPath));
        Assert.False(Directory.Exists(_layout.InstallDir));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_regKey));
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public void Uninstall_WhenNotInstalled_SaysSo()
    {
        Assert.Contains("nicht installiert", Installer.Uninstall(_source, _layout));
    }

    [Theory]
    [InlineData("--install")]
    [InlineData("--INSTALL")]
    public void Cli_Install(string flag)
    {
        var o = CliOptions.Parse(new[] { flag });
        Assert.True(o.Install);
        Assert.False(o.NoContextMenu);
        Assert.Null(o.Error);
    }

    [Fact]
    public void Cli_InstallWithoutContextMenu()
    {
        var o = CliOptions.Parse(new[] { "--install", "--no-context-menu" });
        Assert.True(o.Install);
        Assert.True(o.NoContextMenu);
        Assert.Null(o.Error);
    }

    [Fact]
    public void Cli_Uninstall() => Assert.True(CliOptions.Parse(new[] { "--uninstall" }).Uninstall);

    [Theory]
    [InlineData("--install", "--uninstall")]
    [InlineData("--no-context-menu")]
    [InlineData("--install", "C:\\")]
    [InlineData("--uninstall", "--no-context-menu")]
    public void Cli_InvalidCombinations(params string[] args) => Assert.NotNull(CliOptions.Parse(args).Error);
}
