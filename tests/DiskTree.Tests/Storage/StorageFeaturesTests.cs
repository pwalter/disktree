using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>Which parts of the UI a source allows (StorageCaps), and the reasons shown when it does not.</summary>
public class StorageFeaturesTests
{
    [Fact]
    public void The_disk_has_every_feature()
    {
        foreach (var f in Enum.GetValues<StorageFeature>())
        {
            Assert.True(StorageFeatures.Has(DiskProvider.Instance, f), f.ToString());
            Assert.Null(StorageFeatures.WhyNot(DiskProvider.Instance, f));
        }
        foreach (var s in Enum.GetValues<AgeStamp>())
            Assert.Equal(s, StorageFeatures.EffectiveStamp(s, DiskProvider.Instance));
    }

    [Fact]
    public void A_source_with_few_caps_offers_only_what_it_has()
    {
        var p = new FakeProvider { Caps = StorageCaps.CaseSensitive | StorageCaps.ParallelListing | StorageCaps.CostsPerRequest };
        foreach (var f in Enum.GetValues<StorageFeature>())
        {
            Assert.False(StorageFeatures.Has(p, f), f.ToString());
            var why = StorageFeatures.WhyNot(p, f);
            Assert.False(string.IsNullOrWhiteSpace(why));
            Assert.StartsWith(p.DisplayName + ":", why);
        }
        Assert.Equal(AgeStamp.Modified, StorageFeatures.EffectiveStamp(AgeStamp.Created, p));
        Assert.Equal(AgeStamp.Modified, StorageFeatures.EffectiveStamp(AgeStamp.Accessed, p));
        Assert.True(StorageFeatures.Supports(p, AgeStamp.Modified));
        Assert.False(StorageFeatures.Supports(p, AgeStamp.Created));
    }

    [Fact]
    public void Caps_alone_do_not_unlock_features_that_only_work_on_windows_paths()
    {
        // A source that claims everything still gets no shell menu, owners, duplicates or file actions: that code uses
        // System.IO / the Windows shell. Timestamps, attributes, capacity and live listing follow the caps.
        var p = new FakeProvider { Caps = DiskProvider.Instance.Caps };
        Assert.False(StorageFeatures.Has(p, StorageFeature.ShellMenu));
        Assert.False(StorageFeatures.Has(p, StorageFeature.Owner));
        Assert.False(StorageFeatures.Has(p, StorageFeature.Duplicates));
        Assert.False(StorageFeatures.Has(p, StorageFeature.Delete));
        Assert.False(StorageFeatures.Has(p, StorageFeature.Explorer));
        Assert.True(StorageFeatures.Has(p, StorageFeature.CreatedTime));
        Assert.True(StorageFeatures.Has(p, StorageFeature.AccessedTime));
        Assert.True(StorageFeatures.Has(p, StorageFeature.Attributes));
        Assert.True(StorageFeatures.Has(p, StorageFeature.FreeSpace));
        Assert.True(StorageFeatures.Has(p, StorageFeature.LiveFileList));
        Assert.Equal(AgeStamp.Created, StorageFeatures.EffectiveStamp(AgeStamp.Created, p));
    }

    [Fact]
    public void S3_shows_only_modified_and_no_file_actions()
    {
        var s3 = S3Provider.Generic;
        Assert.Equal(AgeStamp.Modified, StorageFeatures.EffectiveStamp(AgeStamp.Accessed, s3));
        Assert.False(StorageFeatures.Has(s3, StorageFeature.LiveFileList));
        Assert.False(StorageFeatures.Has(s3, StorageFeature.FreeSpace));
        Assert.False(StorageFeatures.Has(s3, StorageFeature.Attributes));
        Assert.Contains("S3", StorageFeatures.WhyNot(s3, StorageFeature.Owner));
    }

    [Fact]
    public async Task A_scan_with_an_unsupported_stamp_falls_back_to_modified()
    {
        var p = new FakeProvider { Caps = StorageCaps.CaseSensitive };
        var old = DateTime.UtcNow.AddYears(-3);
        p.File("/d/a.bin", 100, modified: old, created: DateTime.UtcNow, accessed: DateTime.UtcNow);
        var scanner = new Scanner { Provider = p, AgeStamp = StorageFeatures.EffectiveStamp(AgeStamp.Created, p) };
        await scanner.ScanAsync("/d", 2, CancellationToken.None);
        Assert.Equal(AgeStamp.Modified, scanner.Ages.Stamp);
        Assert.Equal(StampSet.Modified, p.LastStamps);
        var ages = scanner.Root!.OwnAges!;
        Assert.Equal(0, ages[0]);                                  // by "created" it would be the newest bucket
        Assert.Equal(100, ages.Take(AgeBuckets.Count).Sum());
    }
}
