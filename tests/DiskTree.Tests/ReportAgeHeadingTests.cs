using DiskTree.Core;
using DiskTree.Features.Reports;
using Xunit;

namespace DiskTree.Tests;

public class ReportAgeHeadingTests : TestBase
{
    [Theory]
    [InlineData(AgeStamp.Modified, "Alter der Dateien (letzte Änderung)")]
    [InlineData(AgeStamp.Created, "Alter der Dateien (Erstellungsdatum)")]
    [InlineData(AgeStamp.Accessed, "Alter der Dateien (letzter Zugriff)")]
    public async Task HtmlReport_NamesTheTimestampTheScanUsed(AgeStamp stamp, string heading)
    {
        T.File(@"a\f.txt", 10, Ago(days: 5));
        var s = await ScanT(stamp: stamp);

        var data = ReportBuilder.Build(s, false);
        Assert.Equal(stamp, data.AgeStamp);
        Assert.Equal(heading, data.AgeHeading); // the PDF writer prints exactly this

        var html = ReportHtml.Render(data);
        Assert.Contains("<h2>" + heading + "</h2>", html);
        foreach (var other in new[] { "letzte Änderung", "Erstellungsdatum", "letzter Zugriff" })
            if (!heading.Contains(other)) Assert.DoesNotContain("Alter der Dateien (" + other + ")", html);
    }

    [Fact]
    public void DefaultReportData_KeepsTheOldHeading() =>
        Assert.Equal("Alter der Dateien (letzte Änderung)", new ReportData().AgeHeading);
}
