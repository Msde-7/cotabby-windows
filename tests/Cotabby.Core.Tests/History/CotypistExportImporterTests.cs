using System.Text;
using System.Text.Json;
using Cotabby.Core.History;

namespace Cotabby.Core.Tests.History;

public sealed class CotypistExportImporterTests
{
    internal static byte[] Export(params Dictionary<string, string>[] rows) => JsonSerializer.SerializeToUtf8Bytes(rows);

    [Fact]
    public void KeepsOnlyTheMostCompleteSnapshotOfEachField()
    {
        var data = Export(
            new()
            {
                ["createdAt"] = "2026-06-05 16:23:53.028", ["updatedAt"] = "2026-06-05 16:23:53.028",
                ["appBundleIdentifier"] = "com.microsoft.Outlook", ["textUpToCursor"] = "Hi Arnaud, the POC is read",
                ["domain"] = "-",
            },
            new()
            {
                ["createdAt"] = "2026-06-05 16:24:10.000", ["updatedAt"] = "2026-06-05 16:24:10.000",
                ["appBundleIdentifier"] = "com.microsoft.Outlook",
                ["textUpToCursor"] = "Hi Arnaud, the POC is ready for review.",
                ["textAfterCursor"] = " Kind regards, Senad", ["domain"] = "-",
            },
            new()
            {
                ["createdAt"] = "2026-06-06 09:00:00.000", ["appBundleIdentifier"] = "com.google.Chrome",
                ["textUpToCursor"] = "Can you summarize this incident report for me please?", ["domain"] = "claude.ai",
            });

        var records = CotypistExportImporter.RecordsFromExport(data);

        Assert.Equal(2, records.Count);
        Assert.Equal("Hi Arnaud, the POC is ready for review. Kind regards, Senad", records[0].Text);
        // Cotypist's "-" means no site.
        Assert.Null(records[0].Domain);
        Assert.Equal("claude.ai", records[1].Domain);
        Assert.All(records, record => Assert.Equal(TypingHistorySource.Imported, record.Source));
    }

    [Fact]
    public void DifferentMessagesThatOpenTheSameWayAreBothKept()
    {
        var data = Export(
            new()
            {
                ["appBundleIdentifier"] = "com.apple.mail",
                ["textUpToCursor"] =
                    "Thanks for reaching out! I'd be happy to help with your billing question about the March invoice.",
            },
            new()
            {
                ["appBundleIdentifier"] = "com.apple.mail",
                ["textUpToCursor"] =
                    "Thanks for reaching out! I'd be happy to help. Unfortunately we cannot refund annual plans after thirty days.",
            });

        Assert.Equal(2, CotypistExportImporter.RecordsFromExport(data).Count);
    }

    [Fact]
    public void AnEditedEarlierSnapshotStillCollapsesIntoTheFinalText()
    {
        const string final =
            "Hi Arnaud, the POC is ready for review. I tested it on the staging server and everything works. Let me know.";
        var data = Export(
            new()
            {
                ["appBundleIdentifier"] = "com.microsoft.Outlook",
                ["textUpToCursor"] = "Hi Arnaud, the POC is ready for review. I tested it on the stagng server and it works.",
            },
            new() { ["appBundleIdentifier"] = "com.microsoft.Outlook", ["textUpToCursor"] = final });

        Assert.Equal([final], CotypistExportImporter.RecordsFromExport(data).Select(record => record.Text));
    }

    [Fact]
    public void FragmentsAreDropped()
    {
        var data = Export(new Dictionary<string, string>
        {
            ["appBundleIdentifier"] = "net.whatsapp.WhatsApp", ["textUpToCursor"] = "ok",
        });

        Assert.Empty(CotypistExportImporter.RecordsFromExport(data));
    }

    [Fact]
    public void SecretsAreScrubbedOnImport()
    {
        var data = Export(new Dictionary<string, string>
        {
            ["appBundleIdentifier"] = "com.googlecode.iterm2",
            ["textUpToCursor"] = "export OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuvwx1234 && run the build",
        });

        string text = CotypistExportImporter.RecordsFromExport(data).Single().Text;

        Assert.DoesNotContain("sk-proj", text);
        Assert.Contains("run the build", text);
    }

    [Fact]
    public void OtherJsonIsRejected()
    {
        Assert.Throws<CotypistImportException>(
            () => CotypistExportImporter.RecordsFromExport(Encoding.UTF8.GetBytes("{\"a\":1}")));
        Assert.Throws<CotypistImportException>(
            () => CotypistExportImporter.RecordsFromExport(Encoding.UTF8.GetBytes("[{\"name\":\"x\"}]")));
    }
}
