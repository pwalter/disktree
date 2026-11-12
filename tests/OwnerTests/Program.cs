using DiskTree;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "OK    " : "FEHLER ") + what);
    if (!ok) failures++;
}

// --- aggregation with a fake owner lookup
var files = new List<(string, long)>
{
    (@"a\1", 5_000_000), (@"a\2", 3_000_000), (@"b\3", 2_000_000),
    (@"b\4", 100), (@"c\denied", 9_000_000), (@"c\5", 4_000_000),
};
string? Fake(string p) => p switch
{
    @"a\1" or @"a\2" => "DOM\\anna",
    @"b\3" or @"b\4" => "DOM\\bob",
    @"c\denied" => null,
    _ => "DOM\\bob",
};

var all = OwnerAggregator.Aggregate(files, Fake, 0, 4, null, CancellationToken.None);
Check(all.Rows.Count == 2, "zwei Besitzer");
Check(all.Rows[0].Owner == "DOM\\anna" && all.Rows[0].Bytes == 8_000_000, "anna: 8.000.000 Bytes (absteigend sortiert zuerst)");
Check(all.Rows[1].Owner == "DOM\\bob" && all.Rows[1].Bytes == 6_000_100 && all.Rows[1].Files == 3, "bob: 6.000.100 Bytes, 3 Dateien");
Check(all.Failed == 1 && all.Seen == 6 && all.Skipped == 0, "Zugriff verweigert wird gezählt, nicht gemeldet");

var sample = OwnerAggregator.Aggregate(files, Fake, 1_000_000, 2, null, CancellationToken.None);
Check(sample.Skipped == 1 && sample.Rows.Sum(r => r.Files) == 4, "Stichprobe >= 1 MB überspringt die 100-Byte-Datei");

var cts = new CancellationTokenSource();
cts.Cancel();
var cancelled = OwnerAggregator.Aggregate(files, Fake, 0, 2, null, cts.Token);
Check(cancelled.Cancelled, "Abbruch liefert Teilergebnis mit Cancelled-Flag");

// --- real folder: owner lookup on the temp dir and a file in it
string dir = Path.Combine(Path.GetTempPath(), "disktree-owner-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
File.WriteAllBytes(Path.Combine(dir, "x.bin"), new byte[2048]);
Directory.CreateDirectory(Path.Combine(dir, "sub"));
File.WriteAllBytes(Path.Combine(dir, "sub", "y.bin"), new byte[1024]);
try
{
    var sid = OwnerInfo.GetOwnerSid(dir);
    Check(sid != null && sid.StartsWith("S-1-"), "Ordner-Besitzer-SID lesbar: " + sid);
    var name = await OwnerInfo.GetOwnerNameAsync(dir);
    Check(!string.IsNullOrEmpty(name), "Besitzer-Name aufgelöst: " + name);
    var acl = OwnerInfo.GetAclLines(dir);
    Check(acl.Length > 0, "ACL-Zeilen: " + acl.Length + " (erste: " + acl[0] + ")");

    var real = OwnerAggregator.Aggregate(OwnerAggregator.EnumerateFiles(dir),
        p => OwnerInfo.GetOwnerSid(p) is { } s ? OwnerInfo.ResolveName(s) : null, 0, 2, null, CancellationToken.None);
    Check(real.Rows.Count == 1 && real.Rows[0].Bytes == 3072 && real.Rows[0].Files == 2, "echter Ordner: 2 Dateien, 3072 Bytes, ein Besitzer (" + real.Rows.FirstOrDefault()?.Owner + ")");

    var unknown = await OwnerInfo.ResolveNameAsync("S-1-5-21-1-2-3-99999");
    Check(unknown.StartsWith("S-1-5-21"), "unauflösbare SID fällt auf SID-Text zurück");
}
finally
{
    Directory.Delete(dir, true);
}

Console.WriteLine(failures == 0 ? "Alle Tests bestanden." : failures + " Test(s) fehlgeschlagen.");
return failures == 0 ? 0 : 1;
