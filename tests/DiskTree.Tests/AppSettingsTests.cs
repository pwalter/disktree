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
    public void Save_Writes_ThroughTmp_AndKeepsThePreviousVersion()
    {
        var s = new AppSettings { Threads = 4 };
        s.Sources.Add(new DiskTree.Core.Storage.SourceProfile { Kind = "s3", Name = "Archiv", Path = "s3://archiv" });
        Assert.True(s.SaveTo(File_));
        Assert.False(File.Exists(File_ + ".tmp"));
        Assert.False(File.Exists(AppSettings.PreviousOf(File_)));

        s.Threads = 8;
        Assert.True(s.SaveTo(File_));
        Assert.Equal(8, AppSettings.LoadFrom(File_).Threads);
        Assert.Equal(4, AppSettings.LoadFrom(AppSettings.PreviousOf(File_)).Threads);
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Theory]
    [InlineData("")]                   // a crash of the old, direct write left an empty file
    [InlineData("{\"Threads\": 8, ")]  // ... or half of it
    [InlineData(null)]                 // ... or none
    public void Broken_Or_Missing_File_FallsBack_ToThePreviousVersion_WithItsSources(string? content)
    {
        var s = new AppSettings { Threads = 4 };
        s.Sources.Add(new DiskTree.Core.Storage.SourceProfile { Kind = "sftp", Name = "NAS", Path = "sftp://nas/daten" });
        s.SaveTo(File_);
        s.SaveTo(File_); // now settings.json.prev exists too
        if (content == null) File.Delete(File_); else File.WriteAllText(File_, content);

        var loaded = AppSettings.LoadFrom(File_);

        Assert.Equal(4, loaded.Threads);
        Assert.Equal("NAS", Assert.Single(loaded.Sources).Name);
    }

    [Fact]
    public void A_Damaged_File_DoesNotReplace_TheLastGoodCopy()
    {
        new AppSettings { Threads = 4 }.SaveTo(File_);
        new AppSettings { Threads = 5 }.SaveTo(File_);   // .prev = 4
        File.WriteAllText(File_, "kaputt");

        new AppSettings { Threads = 6 }.SaveTo(File_);

        Assert.Equal(6, AppSettings.LoadFrom(File_).Threads);
        Assert.Equal(4, AppSettings.LoadFrom(AppSettings.PreviousOf(File_)).Threads);
    }

    [Fact]
    public void A_Leftover_Tmp_From_A_Crash_IsIgnored_AndOverwritten()
    {
        new AppSettings { Threads = 4 }.SaveTo(File_);
        File.WriteAllText(File_ + ".tmp", "{\"Threads\": 9, ");

        Assert.Equal(4, AppSettings.LoadFrom(File_).Threads);
        new AppSettings { Threads = 5 }.SaveTo(File_);
        Assert.Equal(5, AppSettings.LoadFrom(File_).Threads);
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Fact]
    public void EmptyFile_GivesDefaults_WithoutBackup()
    {
        File.WriteAllText(File_, "");
        AppSettings.LoadFrom(File_);
        Assert.Single(Directory.GetFiles(T.Path));
    }
}
