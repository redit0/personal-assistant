using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using Assistant.Core.Connectors;

namespace Assistant.Connectors.Microsoft;

public sealed record DocumentHit(string Name, string WebUrl, string? Summary, DateTime? LastModified, string? LastModifiedBy, string? DriveId, string? ItemId);

public sealed record DocumentContent(string Name, string WebUrl, string? Text, string? Note, DateTime? LastModified, string? LastModifiedBy);

/// <summary>SharePoint and OneDrive search and reading, for the chat and agents.</summary>
public sealed partial class GraphClient
{
    /// <summary>Files.Read.All and Sites.Read.All need admin consent.</summary>
    public static readonly string[] FileScopes = ["https://graph.microsoft.com/Files.Read.All", "https://graph.microsoft.com/Sites.Read.All"];

    private const int MaxDocumentChars = 30_000;
    private const long MaxDownloadBytes = 25 * 1024 * 1024;

    public async Task<List<DocumentHit>> SearchDocumentsAsync(string query, int size, CancellationToken cancellationToken)
    {
        var body = new
        {
            requests = new[]
            {
                new { entityTypes = new[] { "driveItem" }, query = new { queryString = query }, from = 0, size = Math.Clamp(size, 1, 25) },
            },
        };

        using var doc = await SendAsync(HttpMethod.Post, "search/query", body, FileScopes, cancellationToken);
        var hits = new List<DocumentHit>();
        foreach (var container in doc.RootElement.GetProperty("value").EnumerateArray().SelectMany(v => v.GetProperty("hitsContainers").EnumerateArray()))
        {
            if (!container.TryGetProperty("hits", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var hit in list.EnumerateArray())
            {
                var r = hit.GetProperty("resource");
                var parent = r.TryGetProperty("parentReference", out var pr) && pr.ValueKind == JsonValueKind.Object ? pr : (JsonElement?)null;
                hits.Add(new DocumentHit(
                    Str(r, "name") ?? "(untitled)",
                    Str(r, "webUrl") ?? "",
                    Core.Text.HtmlText.ToText(Str(hit, "summary")),
                    Date(r, "lastModifiedDateTime"),
                    ModifiedBy(r),
                    parent is { } p ? Str(p, "driveId") : null,
                    Str(r, "id")));
            }
        }

        return hits;
    }

    /// <summary>Reads a document by its web link (any SharePoint or OneDrive URL, including sharing links).</summary>
    public async Task<DocumentContent> ReadDocumentAsync(string url, CancellationToken cancellationToken)
    {
        // The shares endpoint resolves any SharePoint/OneDrive link: "u!" + unpadded base64url of the URL.
        var encoded = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('/', '_').Replace('+', '-');
        using var item = await SendAsync(HttpMethod.Get, $"shares/{encoded}/driveItem?$select=id,name,webUrl,size,file,lastModifiedDateTime,lastModifiedBy,parentReference", null, FileScopes, cancellationToken);
        var root = item.RootElement;
        var name = Str(root, "name") ?? "document";
        var webUrl = Str(root, "webUrl") ?? url;
        var modified = Date(root, "lastModifiedDateTime");
        var modifiedBy = ModifiedBy(root);

        if (!root.TryGetProperty("file", out _))
        {
            return new DocumentContent(name, webUrl, null, "That link is a folder, not a file.", modified, modifiedBy);
        }

        var size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!Extractable(extension))
        {
            return new DocumentContent(name, webUrl, null, $"Text can't be read from {extension} files here; open the link instead.", modified, modifiedBy);
        }

        if (size > MaxDownloadBytes)
        {
            return new DocumentContent(name, webUrl, null, "The file is too large to read here; open the link instead.", modified, modifiedBy);
        }

        var driveId = root.GetProperty("parentReference").GetProperty("driveId").GetString();
        var itemId = Str(root, "id");
        var content = await DownloadAsync($"drives/{driveId}/items/{itemId}/content", cancellationToken);
        var text = ExtractText(content, extension);
        var note = text is { Length: > MaxDocumentChars } ? $"Showing the first {MaxDocumentChars:N0} characters." : null;
        return new DocumentContent(name, webUrl, text is { Length: > MaxDocumentChars } ? text[..MaxDocumentChars] : text, note, modified, modifiedBy);
    }

    public static bool Extractable(string extension) =>
        extension is ".docx" or ".pptx" or ".xlsx" or ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".html" or ".htm";

    /// <summary>Pulls readable text out of Office Open XML and plain-text files without any Office libraries.</summary>
    public static string? ExtractText(byte[] content, string extension)
    {
        try
        {
            return extension switch
            {
                ".docx" => FromZip(content, e => e.FullName == "word/document.xml", "p", "t", "w"),
                ".pptx" => FromZip(content, e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal), "p", "t", "a"),
                ".xlsx" => FromZip(content, e => e.FullName == "xl/sharedStrings.xml", "si", "t", null),
                ".html" or ".htm" => Core.Text.HtmlText.ToText(Encoding.UTF8.GetString(content)),
                _ => Encoding.UTF8.GetString(content),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            return null;
        }
    }

    /// <summary>Collects text runs from matching XML parts, starting a new line at each paragraph-like element.</summary>
    private static string FromZip(byte[] content, Func<ZipArchiveEntry, bool> parts, string paragraph, string textElement, string? prefix)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries.Where(parts).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == textElement && (prefix is null || reader.Prefix == prefix))
                {
                    text.Append(reader.ReadElementContentAsString());
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == paragraph)
                {
                    text.AppendLine();
                }
            }

            text.AppendLine();
            if (text.Length > MaxDocumentChars * 2)
            {
                break;
            }
        }

        return text.ToString().Trim();
    }

    private async Task<byte[]> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        var token = await auth.GetAccessTokenAsync(FileScopes, cancellationToken);
        using var http = httpClients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, path));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ConnectorException($"Couldn't download the file ({(int)response.StatusCode}).");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static string? ModifiedBy(JsonElement resource) =>
        resource.TryGetProperty("lastModifiedBy", out var by) && by.ValueKind == JsonValueKind.Object
        && by.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            ? Str(user, "displayName")
            : null;
}
