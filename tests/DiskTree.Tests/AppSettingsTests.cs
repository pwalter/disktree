using System.IO;
using DiskTree.Infrastructure;
using Xunit;

namespace DiskTree.Tests;

public class AppSettingsTests : TestBase
{
    private string File_ => T.Sub("settings.json");

    [Fact]
    public void Missing_File_GivesDefaults_WithoutBackup()
    {
        var s = AppSettings.LoadFrom(File_);
        Assert.Equal(16, s.Threads);
        Assert.Empty(Directory.GetFiles(T.Path));
    }

    [Fact]
    public void Valid_File_IsRead_AndNoBackupIsMade()
    {
        File.WriteAllText(File_, "{\"Threads\":4,\"LastPath\":\"C:\\\\x\"}");
        var s = AppSettings.LoadFrom(File_);
        Assert.Equal(4, s.Threads);
        Assert.Equal(@"C:\x", s.LastPath);
        Assert.Single(Directory.GetFiles(T.Path));
    }

    [Fact]
    public void Damaged_File_IsBackedUp_AndDefaultsAreReturned()
    {
        File.WriteAllText(File_, "{\"Threads\": 4, \"LastPath\": ");
        var s = AppSettings.LoadFrom(File_);
        Assert.Equal(16, s.Threads);
        Assert.Equal("{\"Threads\": 4, \"LastPath\": ", File.ReadAllText(File_ + ".bak"));

        // saving the defaults overwrites settings.json, the backup stays
        File.WriteAllText(File_, "{}");
        Assert.Equal("{\"Threads\": 4, \"LastPath\": ", File.ReadAllText(File_ + ".bak"));
    }

    [Fact]
    public void Damaged_Again_DoesNotOverwriteTheFirstBackup_AndIdenticalOnesAreNotRepeated()
    {
        File.WriteAllText(File_, "kaputt eins");
        AppSettings.LoadFrom(File_);
        AppSettings.LoadFrom(File_); // second load in the same start: same content, no new copy
        Assert.Single(Directory.GetFiles(T.Path, "*.bak"));

        File.WriteAllText(File_, "kaputt zwei");
        AppSettings.LoadFrom(File_);
        var backups = Directory.GetFiles(T.Path, "*.bak");
        Assert.Equal(2, backups.Length);
        Assert.Equal("kaputt eins", File.ReadAllText(File_ + ".bak"));
        Assert.Contains(backups, b => File.ReadAllText(b) == "kaputt zwei");
    }

    [Fact]
    public void WrongValueType_CountsAsDamaged()
    {
        File.WriteAllText(File_, "{\"Theme\":\"nope\"}");
        Assert.Equal(16, AppSettings.LoadFrom(File_).Threads);
        Assert.True(File.Exists(File_ + ".bak"));
    }

    [Fact]
    public void EmptyFile_GivesDefaults_WithoutBackup()
    {
        File.WriteAllText(File_, "");
        AppSettings.LoadFrom(File_);
        Assert.Single(Directory.GetFiles(T.Path));
    }
}
