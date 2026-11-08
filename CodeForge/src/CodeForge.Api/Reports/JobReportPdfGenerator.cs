using System.Globalization;
using System.Text;
using CodeForge.Infrastructure.Models;

namespace CodeForge.Api.Reports;

internal static class JobReportPdfGenerator
{
    private const int MaximumLineLength = 88;
    private const int LinesPerPage = 43;

    public static byte[] Generate(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var pages = BuildReportLines(job)
            .Chunk(LinesPerPage)
            .Select(page => page.ToArray())
            .ToArray();

        return BuildPdf(pages);
    }

    private static List<string> BuildReportLines(Job job)
    {
        var lines = new List<string>();

        AddField(lines, "Job ID", job.Id.ToString());
        AddField(lines, "Repository", job.RepoUrl);
        AddField(lines, "Title", job.JobTitle ?? "Untitled build");
        AddField(lines, "Status", job.Status.ToString());
        AddField(lines, "Queued", FormatDate(job.CreatedAt));
        AddField(lines, "Started", FormatDate(job.StartedAt));
        AddField(lines, "Finished", FormatDate(job.CompletedAt));
        lines.Add(string.Empty);

        lines.Add("DESCRIPTION");
        lines.AddRange(Wrap(job.JobDescription ?? "No description was provided."));
        lines.Add(string.Empty);

        lines.Add(job.Status is JobStatus.Failed ? "FAILURE REASON" : "ANALYSIS");
        var result = job.Status switch
        {
            JobStatus.Failed => job.FailureReason
                ?? "The worker did not provide a failure reason.",
            JobStatus.Completed =>
                "The repository was cloned and the .NET project built successfully.",
            JobStatus.Running => "The repository build is currently running.",
            _ => "The repository build is waiting to be processed."
        };
        lines.AddRange(Wrap(result));

        return lines;
    }

    private static void AddField(List<string> lines, string label, string value)
    {
        lines.AddRange(Wrap($"{label}: {value}"));
    }

    private static string FormatDate(DateTime? value)
    {
        return value?.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            CultureInfo.InvariantCulture) ?? "Not available";
    }

    private static IEnumerable<string> Wrap(string value)
    {
        foreach (var rawParagraph in value.Replace("\r", string.Empty).Split('\n'))
        {
            var remaining = ToPdfAscii(rawParagraph).Trim();
            if (remaining.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            while (remaining.Length > MaximumLineLength)
            {
                var breakAt = remaining.LastIndexOf(' ', MaximumLineLength);
                if (breakAt <= 0)
                {
                    breakAt = MaximumLineLength;
                }

                yield return remaining[..breakAt].TrimEnd();
                remaining = remaining[breakAt..].TrimStart();
            }

            yield return remaining;
        }
    }

    private static byte[] BuildPdf(IReadOnlyList<string[]> pages)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, "%PDF-1.4\n% CodeForge\n");

        var fontObjectId = 3 + (pages.Count * 2);
        var objectCount = fontObjectId;
        var offsets = new long[objectCount + 1];

        WriteObject(stream, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");

        var pageReferences = string.Join(
            " ",
            Enumerable.Range(0, pages.Count).Select(index => $"{3 + (index * 2)} 0 R"));
        WriteObject(
            stream,
            offsets,
            2,
            $"<< /Type /Pages /Kids [{pageReferences}] /Count {pages.Count} >>");

        for (var index = 0; index < pages.Count; index++)
        {
            var pageObjectId = 3 + (index * 2);
            var contentObjectId = pageObjectId + 1;
            var pageBody =
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {fontObjectId} 0 R >> >> " +
                $"/Contents {contentObjectId} 0 R >>";
            WriteObject(stream, offsets, pageObjectId, pageBody);

            var content = BuildPageContent(pages[index], index + 1, pages.Count);
            var contentLength = Encoding.ASCII.GetByteCount(content);
            WriteObject(
                stream,
                offsets,
                contentObjectId,
                $"<< /Length {contentLength} >>\nstream\n{content}endstream");
        }

        WriteObject(
            stream,
            offsets,
            fontObjectId,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var xrefOffset = stream.Position;
        WriteAscii(stream, $"xref\n0 {objectCount + 1}\n");
        WriteAscii(stream, "0000000000 65535 f \n");
        for (var objectId = 1; objectId <= objectCount; objectId++)
        {
            WriteAscii(stream, $"{offsets[objectId]:D10} 00000 n \n");
        }

        WriteAscii(
            stream,
            $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\n" +
            $"startxref\n{xrefOffset}\n%%EOF\n");

        return stream.ToArray();
    }

    private static string BuildPageContent(
        IReadOnlyList<string> lines,
        int pageNumber,
        int pageCount)
    {
        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 18 Tf");
        content.AppendLine("54 744 Td");
        content.AppendLine("(CodeForge Build Report) Tj");
        content.AppendLine("/F1 9 Tf");
        content.AppendLine("0 -22 Td");
        content.AppendLine($"(Page {pageNumber} of {pageCount}) Tj");
        content.AppendLine("/F1 10 Tf");
        content.AppendLine("0 -26 Td");

        foreach (var line in lines)
        {
            content.Append('(')
                .Append(EscapePdfText(line))
                .AppendLine(") Tj");
            content.AppendLine("0 -14 Td");
        }

        content.AppendLine("ET");
        return content.ToString();
    }

    private static string ToPdfAscii(string value)
    {
        var characters = value.Select(character =>
            character is >= ' ' and <= '~' ? character : '?');
        return new string(characters.ToArray());
    }

    private static string EscapePdfText(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
    }

    private static void WriteObject(
        Stream stream,
        long[] offsets,
        int objectId,
        string body)
    {
        offsets[objectId] = stream.Position;
        WriteAscii(stream, $"{objectId} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
    }
}
