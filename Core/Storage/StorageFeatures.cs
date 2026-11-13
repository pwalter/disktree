namespace DiskTree.Core.Storage;

/// <summary>The parts of the app that depend on what the scanned source can do (<see cref="StorageCaps"/>).</summary>
public enum StorageFeature
{
    /// <summary>"Besitzer" tab.</summary>
    Owner,
    /// <summary>"Duplikate" tab (reads and hashes file contents).</summary>
    Duplicates,
    /// <summary>"Übergeordneten Ordner öffnen" (Windows Explorer).</summary>
    Explorer,
    /// <summary>The Windows shell context menu.</summary>
    ShellMenu,
    /// <summary>Delete / recycle bin.</summary>
    Delete,
    Move,
    Rename,
    Zip,
    /// <summary>"Erstellt" as age timestamp / search filter.</summary>
    CreatedTime,
    /// <summary>"Zuletzt geöffnet" as age timestamp / search filter.</summary>
    AccessedTime,
    /// <summary>Attribute filters of the search.</summary>
    Attributes,
    /// <summary>Free space / capacity rows of the tree.</summary>
    FreeSpace,
    /// <summary>Listing a folder again on demand ("Dateien" row, file search) without asking.</summary>
    LiveFileList,
}

/// <summary>
/// Which features work for a source, and why not (for tooltips). The disk can do everything; other sources only
/// what their <see cref="StorageCaps"/> allow. Features whose code still works with System.IO / the Windows shell
/// directly (owner, duplicates, file actions, Explorer, shell menu) are, in addition, only offered for the disk.
/// </summary>
public static class StorageFeatures
{
    public static StorageCaps Needs(StorageFeature f) => f switch
    {
        StorageFeature.Owner => StorageCaps.Owner,
        StorageFeature.Duplicates => StorageCaps.ContentHash | StorageCaps.LiveFileList,
        StorageFeature.Explorer => StorageCaps.ShellMenu,
        StorageFeature.ShellMenu => StorageCaps.ShellMenu,
        StorageFeature.Delete => StorageCaps.Delete,
        StorageFeature.Move => StorageCaps.Move,
        StorageFeature.Rename => StorageCaps.Rename,
        StorageFeature.Zip => StorageCaps.ZipCreate,
        StorageFeature.CreatedTime => StorageCaps.CreatedTime,
        StorageFeature.AccessedTime => StorageCaps.AccessedTime,
        StorageFeature.Attributes => StorageCaps.Attributes,
        StorageFeature.FreeSpace => StorageCaps.Capacity,
        StorageFeature.LiveFileList => StorageCaps.LiveFileList,
        _ => StorageCaps.None,
    };

    /// <summary>Implemented with System.IO / the Windows shell: works for local (and UNC) paths only.</summary>
    private static bool DiskOnly(StorageFeature f) => f is StorageFeature.Owner or StorageFeature.Duplicates
        or StorageFeature.Explorer or StorageFeature.ShellMenu or StorageFeature.Delete or StorageFeature.Move
        or StorageFeature.Rename or StorageFeature.Zip;

    public static bool Has(IStorageProvider provider, StorageFeature f) =>
        (provider.Caps & Needs(f)) == Needs(f) && (!DiskOnly(f) || provider.Id == DiskProvider.ProviderId);

    /// <summary>Null if the feature works for this source, else a German sentence for a tooltip / notice.</summary>
    public static string? WhyNot(IStorageProvider provider, StorageFeature f)
    {
        if (Has(provider, f)) return null;
        string reason = f switch
        {
            StorageFeature.Owner => "Die Quelle kennt keine Besitzer (NTFS-Rechte).",
            StorageFeature.Duplicates => "Die Duplikatsuche liest Dateiinhalte; das geht nur bei Laufwerken und Netzwerkfreigaben.",
            StorageFeature.Explorer => "Der Pfad liegt nicht auf diesem Rechner; der Explorer kann ihn nicht öffnen.",
            StorageFeature.ShellMenu => "Das Windows-Kontextmenü gibt es nur für Dateien auf Laufwerken und Freigaben.",
            StorageFeature.Delete => "Löschen wird für diese Quelle nicht unterstützt (nur lesender Zugriff).",
            StorageFeature.Move => "Verschieben wird für diese Quelle nicht unterstützt (nur lesender Zugriff).",
            StorageFeature.Rename => "Umbenennen wird für diese Quelle nicht unterstützt (nur lesender Zugriff).",
            StorageFeature.Zip => "Archivieren wird für diese Quelle nicht unterstützt (Inhalte werden nicht gelesen).",
            StorageFeature.CreatedTime => "Die Quelle liefert kein Erstellungsdatum, nur „Zuletzt geändert“.",
            StorageFeature.AccessedTime => "Die Quelle liefert kein Datum des letzten Zugriffs, nur „Zuletzt geändert“.",
            StorageFeature.Attributes => "Die Quelle kennt keine Dateiattribute (versteckt, System, schreibgeschützt …).",
            StorageFeature.FreeSpace => "Kapazität und freier Speicher der Quelle sind unbekannt.",
            StorageFeature.LiveFileList => "Jede Auflistung ist eine Anfrage an die Quelle (bei Cloud-Speicher ggf. kostenpflichtig).",
            _ => "Nicht verfügbar.",
        };
        return $"{provider.DisplayName}: {reason}";
    }

    /// <summary>The timestamp a scan of this source can sum up: the wanted one, or "Zuletzt geändert" if the source lacks it.</summary>
    public static AgeStamp EffectiveStamp(AgeStamp wanted, IStorageProvider provider) => wanted switch
    {
        AgeStamp.Created when !Has(provider, StorageFeature.CreatedTime) => AgeStamp.Modified,
        AgeStamp.Accessed when !Has(provider, StorageFeature.AccessedTime) => AgeStamp.Modified,
        _ => wanted,
    };

    /// <summary>The timestamps that make sense for this source (index = (int)AgeStamp).</summary>
    public static bool Supports(IStorageProvider provider, AgeStamp stamp) => EffectiveStamp(stamp, provider) == stamp;
}
