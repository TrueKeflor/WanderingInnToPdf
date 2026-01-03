using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Text;
using System.IO.Compression;
using System.Text.Json;
using AngleSharp.Html.Parser;
using PuppeteerSharp;

class Program
{
    // Defaults requested
    private const string DefaultTocUrl = "https://wanderinginn.com/table-of-contents/";
    private const string DefaultSectionId = "table-of-contents";

    // Manifest filename for offline rebuilds
    private const string ManifestFileName = "manifest.json";

    // Project-relative folders (inside WanderingInnToPdf/)
    private const string ChaptersFolderName = "chapters";
    private const string VolumesFolderName = "volumes";
    private const string AssetsFolderName = "assets";
    private const string CoverFileName = "cover.jpg";

    static async Task<int> Main(string[] args)
    {
        // Flags
        bool offline = args.Any(a => a.Equals("--offline", StringComparison.OrdinalIgnoreCase));

        // Output format (DEFAULT = epub)
        string format = "epub";
        int fmtIndex = Array.FindIndex(args, a => a.Equals("--format", StringComparison.OrdinalIgnoreCase));
        if (fmtIndex >= 0)
        {
            if (fmtIndex + 1 >= args.Length)
            {
                Console.WriteLine("Missing value after --format. Use: --format epub|pdf");
                return 1;
            }
            format = args[fmtIndex + 1].Trim().ToLowerInvariant();
        }

        if (format != "pdf" && format != "epub")
        {
            Console.WriteLine($"Unknown format '{format}'. Use: --format epub|pdf");
            return 1;
        }

        // URL: optional. If none provided, use DefaultTocUrl.
        string url = args.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                             a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                     ?? DefaultTocUrl;

        // Optional volume index (1-based): first non-flag, non-url int
        string volumeSpecifier = args
            .Where(a => !a.StartsWith("--") &&
                        !(a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(a => int.TryParse(a, out _));

        // Determine the project root (folder containing WanderingInnToPdf.csproj)
        var projectRoot = FindProjectRoot();
        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Could not locate WanderingInnToPdf.csproj by walking up from the current directory.");
            Console.Error.WriteLine("Run from within the repo, or update FindProjectRoot() to match your csproj name/location.");
            return 3;
        }

        // Force all outputs to be inside the project folder
        var chaptersFolder = Path.Combine(projectRoot, ChaptersFolderName);
        var volumesFolder = Path.Combine(projectRoot, VolumesFolderName);
        var assetsFolder = Path.Combine(projectRoot, AssetsFolderName);

        Directory.CreateDirectory(chaptersFolder);
        Directory.CreateDirectory(volumesFolder);
        Directory.CreateDirectory(assetsFolder);

        // Cover image (optional)
        var coverPath = Path.Combine(assetsFolder, CoverFileName);
        byte[] coverBytes = File.Exists(coverPath) ? File.ReadAllBytes(coverPath) : null;
        if (coverBytes != null)
            Console.WriteLine($"Cover enabled: {coverPath}");
        else
            Console.WriteLine($"No cover found (optional). To add one, place: {coverPath}");

        var sectionId = DefaultSectionId;

        try
        {
            // OFFLINE MODE: rebuild from cached chapters + manifest only
            if (offline)
            {
                var manifestPath = Path.Combine(chaptersFolder, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    Console.Error.WriteLine($"Error: --offline requires a manifest file at: {manifestPath}");
                    Console.Error.WriteLine("Run once online (without --offline) to generate cached chapters and the manifest.");
                    return 3;
                }

                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<CacheManifest>(manifestJson);
                if (manifest == null || manifest.Volumes == null || manifest.Volumes.Count == 0)
                {
                    Console.Error.WriteLine($"Error: manifest is empty or invalid: {manifestPath}");
                    return 3;
                }

                // Determine which volumes to process (offline)
                Dictionary<string, List<CachedChapterRef>> volumesToProcess;
                if (volumeSpecifier == null)
                {
                    volumesToProcess = manifest.Volumes;
                }
                else
                {
                    if (int.TryParse(volumeSpecifier, out int volumeIndex) &&
                        volumeIndex >= 1 && volumeIndex <= manifest.Volumes.Count)
                    {
                        var volumeKey = manifest.Volumes.Keys.ElementAt(volumeIndex - 1);
                        volumesToProcess = new Dictionary<string, List<CachedChapterRef>>
                        {
                            { volumeKey, manifest.Volumes[volumeKey] }
                        };
                    }
                    else
                    {
                        Console.WriteLine($"Invalid volume specifier: {volumeSpecifier}. Available volumes: {string.Join(", ", manifest.Volumes.Keys)}");
                        return 1;
                    }
                }

                // Build chapter map purely from cache
                var chapterMap = new Dictionary<string, List<(string Name, string Content)>>();
                foreach (var kv in volumesToProcess)
                {
                    var volumeKey = kv.Key;
                    var refs = kv.Value;

                    Console.WriteLine($"[OFFLINE] Rebuilding volume: {volumeKey}");

                    var chapters = new List<(string Name, string Content)>();
                    for (int i = 0; i < refs.Count; i++)
                    {
                        var r = refs[i];
                        var path = Path.Combine(chaptersFolder, r.FileName);

                        if (!File.Exists(path))
                        {
                            Console.Error.WriteLine($"Error: missing cached chapter file: {path}");
                            Console.Error.WriteLine("Re-run without --offline to download missing chapters.");
                            return 3;
                        }

                        RenderProgress($"Loading {volumeKey}", i + 1, refs.Count);
                        chapters.Add((r.Name ?? $"Chapter {i + 1}", File.ReadAllText(path)));
                    }

                    chapterMap[volumeKey] = chapters;
                }

                // Output
                if (format == "pdf")
                    await GeneratePdfs(chapterMap, volumesFolder);
                else
                    GenerateEpubs(chapterMap, volumesFolder, coverBytes);

                return 0;
            }

            // ONLINE MODE: cache-first scraping + manifest generation
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WanderingInnToPdf/1.0 (+https://example)");

            var baseUri = new Uri(url);

            var html = await client.GetStringAsync(url);
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var section = document.QuerySelector("#" + sectionId);
            if (section == null)
            {
                Console.WriteLine($"No element found with id '{sectionId}'");
                return 0;
            }

            var wrappers = section.QuerySelectorAll(".volume-wrapper");
            if (wrappers.Length == 0)
            {
                Console.WriteLine("No .volume-wrapper elements found inside the section.");
                return 0;
            }

            var map = BuildVolumeMap(wrappers, baseUri);

            // Determine which volumes to process
            Dictionary<string, List<(string Text, string Href)>> volumesToProcessOnline;
            if (volumeSpecifier == null)
            {
                volumesToProcessOnline = map;
            }
            else
            {
                if (int.TryParse(volumeSpecifier, out int volumeIndex) && volumeIndex >= 1 && volumeIndex <= map.Count)
                {
                    var volumeKey = map.Keys.ElementAt(volumeIndex - 1);
                    volumesToProcessOnline = new Dictionary<string, List<(string Text, string Href)>> { { volumeKey, map[volumeKey] } };
                }
                else
                {
                    Console.WriteLine($"Invalid volume specifier: {volumeSpecifier}. Available volumes: {string.Join(", ", map.Keys)}");
                    return 1;
                }
            }

            // Build chapter map (cache-first: use /chapters if present, otherwise download missing)
            var chapterMapOnline = new Dictionary<string, List<(string Name, string Content)>>();
            var manifestOut = new CacheManifest
            {
                TocUrl = url,
                GeneratedUtc = DateTime.UtcNow,
                Volumes = new Dictionary<string, List<CachedChapterRef>>()
            };

            foreach (var kv in volumesToProcessOnline)
            {
                var volumeKey = kv.Key;
                Console.WriteLine($"Processing volume: {volumeKey}");

                var chapterContents = new List<(string Name, string Content)>();
                var manifestChapters = new List<CachedChapterRef>();

                int chapterIndex = 0;
                foreach (var item in kv.Value)
                {
                    chapterIndex++;

                    var safeTitle = SanitizeFileName(item.Text);
                    var chapterFileName = $"{chapterIndex}_{safeTitle}.txt";
                    var chapterFilePath = Path.Combine(chaptersFolder, chapterFileName);

                    string extractedContent;

                    if (File.Exists(chapterFilePath))
                    {
                        Console.WriteLine($"  Using cached chapter {chapterIndex}");
                        extractedContent = File.ReadAllText(chapterFilePath);
                    }
                    else
                    {
                        Console.WriteLine($"  Fetching missing chapter {chapterIndex} from {item.Href}");
                        try
                        {
                            var pageHtml = await client.GetStringAsync(item.Href);
                            var pageParser = new HtmlParser();
                            var doc = await pageParser.ParseDocumentAsync(pageHtml);

                            var mainContent = doc.QuerySelector("#main-content");
                            if (mainContent != null)
                            {
                                // Remove Previous Chapter and Next Chapter links
                                var linksToRemove = mainContent.QuerySelectorAll("a")
                                    .Where(a => a.TextContent.Trim().Equals("Previous Chapter", StringComparison.OrdinalIgnoreCase) ||
                                                a.TextContent.Trim().Equals("Next Chapter", StringComparison.OrdinalIgnoreCase));
                                foreach (var link in linksToRemove)
                                {
                                    link.Remove();
                                }
                            }

                            extractedContent = mainContent?.OuterHtml ?? "<p>No main-content element found</p>";
                            File.WriteAllText(chapterFilePath, extractedContent);
                        }
                        catch (Exception ex)
                        {
                            extractedContent = $"<p>Error fetching {EscapeHtml(item.Href)}: {EscapeHtml(ex.Message)}</p>";
                            File.WriteAllText(chapterFilePath, extractedContent);
                        }
                    }

                    chapterContents.Add((item.Text, extractedContent));
                    manifestChapters.Add(new CachedChapterRef
                    {
                        Index = chapterIndex,
                        Name = item.Text,
                        FileName = chapterFileName
                    });
                }

                chapterMapOnline[volumeKey] = chapterContents;
                manifestOut.Volumes[volumeKey] = manifestChapters;
            }

            // Write manifest for offline rebuilds
            var manifestPathOut = Path.Combine(chaptersFolder, ManifestFileName);
            File.WriteAllText(manifestPathOut, JsonSerializer.Serialize(manifestOut, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Wrote cache manifest: {manifestPathOut}");

            // Output
            if (format == "pdf")
                await GeneratePdfs(chapterMapOnline, volumesFolder);
            else
                GenerateEpubs(chapterMapOnline, volumesFolder, coverBytes);

            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"HTTP error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 3;
        }
    }

    static string FindProjectRoot()
    {
        // 1) Walk UP: look for csproj in current folder (works if you run inside WanderingInnToPdf or below)
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var expected = Path.Combine(dir.FullName, "WanderingInnToPdf.csproj");
            if (File.Exists(expected))
                return dir.FullName;

            var csprojs = Directory.GetFiles(dir.FullName, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojs.Length > 0)
                return dir.FullName;

            dir = dir.Parent;
        }

        // 2) Walk DOWN: look for the csproj under the current folder (works if you run from repo root)
        var cwd = Directory.GetCurrentDirectory();
        var expectedDown = Directory.GetFiles(cwd, "WanderingInnToPdf.csproj", SearchOption.AllDirectories).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(expectedDown))
            return Path.GetDirectoryName(expectedDown);

        var anyDown = Directory.GetFiles(cwd, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(anyDown))
            return Path.GetDirectoryName(anyDown);

        return null;
    }


    static void RenderProgress(string label, int current, int total)
    {
        const int barWidth = 30;
        double pct = total == 0 ? 1 : (double)current / total;
        int filled = (int)(pct * barWidth);

        string bar = new string('#', filled) + new string('-', barWidth - filled);
        int percentInt = (int)(pct * 100);

        Console.Write($"\r{label} [{bar}] {percentInt,3}% ({current}/{total})");

        if (current >= total)
            Console.WriteLine();
    }

    static async Task GeneratePdfs(Dictionary<string, List<(string Name, string Content)>> chapterMap, string volumesFolder)
    {
        await new BrowserFetcher().DownloadAsync();
        using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true });

        foreach (var kv in chapterMap)
        {
            var volumeKey = kv.Key;
            var chapters = kv.Value;

            var fileName = Path.Combine(volumesFolder, $"{SanitizeFileName(volumeKey)}.pdf");
            Console.WriteLine($"Generating PDF: {fileName}");

            var htmlBuilder = new StringBuilder();
            htmlBuilder.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><title>" + EscapeHtml(volumeKey) + "</title></head><body>");
            htmlBuilder.AppendLine("<h1>" + EscapeHtml(volumeKey) + "</h1>");

            for (int i = 0; i < chapters.Count; i++)
            {
                RenderProgress($"Compiling {volumeKey}", i + 1, chapters.Count);

                var chapter = chapters[i];
                htmlBuilder.AppendLine("<h2>Chapter " + (i + 1) + ": " + EscapeHtml(chapter.Name) + "</h2>");
                htmlBuilder.AppendLine("<div>" + chapter.Content + "</div>");
            }

            htmlBuilder.AppendLine("</body></html>");

            var page = await browser.NewPageAsync();
            await page.SetContentAsync(htmlBuilder.ToString());

            Console.WriteLine("Rendering PDF...");
            await page.PdfAsync(fileName);
            Console.WriteLine("PDF complete.");

            await page.CloseAsync();
        }

        await browser.CloseAsync();
    }

    static void GenerateEpubs(Dictionary<string, List<(string Name, string Content)>> chapterMap, string volumesFolder, byte[] coverBytes)
    {
        foreach (var kv in chapterMap)
        {
            var volumeKey = kv.Key;
            var chapters = kv.Value;

            var outFile = Path.Combine(volumesFolder, $"{SanitizeFileName(volumeKey)}.epub");
            Console.WriteLine($"Generating EPUB: {outFile}");

            BuildEpub(volumeKey, chapters, outFile, coverBytes);

            Console.WriteLine("EPUB complete.");
        }
    }

    static void BuildEpub(string title, List<(string Name, string Content)> chapters, string outputPath, byte[] coverBytes)
    {
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        using var fs = new FileStream(outputPath, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        // mimetype must be first, uncompressed
        var mimetypeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mimetypeEntry.Open(), new UTF8Encoding(false)))
        {
            w.Write("application/epub+zip");
        }

        // META-INF/container.xml
        var containerEntry = zip.CreateEntry("META-INF/container.xml", CompressionLevel.Optimal);
        using (var w = new StreamWriter(containerEntry.Open(), new UTF8Encoding(false)))
        {
            w.Write(@"<?xml version=""1.0"" encoding=""UTF-8""?>
<container version=""1.0"" xmlns=""urn:oasis:names:tc:opendocument:xmlns:container"">
  <rootfiles>
    <rootfile full-path=""OEBPS/content.opf"" media-type=""application/oebps-package+xml""/>
  </rootfiles>
</container>");
        }

        var manifestItems = new List<(string id, string href, string mediaType, string properties)>();
        var spineIds = new List<string>();

        // CSS
        var cssHref = "styles.css";
        var cssEntry = zip.CreateEntry($"OEBPS/{cssHref}", CompressionLevel.Optimal);
        using (var w = new StreamWriter(cssEntry.Open(), new UTF8Encoding(false)))
        {
            w.Write(@"body { font-family: serif; line-height: 1.4; }
h1, h2 { page-break-after: avoid; }
.chapter-title { margin-top: 1.2em; }
.cover { text-align: center; margin-top: 10%; }
.cover img { max-width: 100%; height: auto; }
");
        }
        manifestItems.Add(("css", cssHref, "text/css", null));

        // Cover (optional)
        bool hasCover = coverBytes != null && coverBytes.Length > 0;
        if (hasCover)
        {
            // Add cover image
            var coverImgHref = "images/cover.jpg";
            var coverImgEntry = zip.CreateEntry($"OEBPS/{coverImgHref}", CompressionLevel.Optimal);
            using (var s = coverImgEntry.Open())
            {
                s.Write(coverBytes, 0, coverBytes.Length);
            }
            // Mark as cover-image (EPUB 3)
            manifestItems.Add(("cover-img", coverImgHref, "image/jpeg", "cover-image"));

            // Add cover.xhtml
            var coverXhtmlHref = "cover.xhtml";
            var coverXhtml = BuildCoverXhtml(title, coverImgHref, cssHref);
            var coverXhtmlEntry = zip.CreateEntry($"OEBPS/{coverXhtmlHref}", CompressionLevel.Optimal);
            using (var w = new StreamWriter(coverXhtmlEntry.Open(), new UTF8Encoding(false)))
            {
                w.Write(coverXhtml);
            }
            manifestItems.Add(("cover", coverXhtmlHref, "application/xhtml+xml", null));
            spineIds.Add("cover");
        }

        // nav.xhtml (TOC)
        var navHref = "nav.xhtml";
        var navXhtml = BuildNavXhtml(title, chapters);
        var navEntry = zip.CreateEntry($"OEBPS/{navHref}", CompressionLevel.Optimal);
        using (var w = new StreamWriter(navEntry.Open(), new UTF8Encoding(false)))
        {
            w.Write(navXhtml);
        }
        manifestItems.Add(("nav", navHref, "application/xhtml+xml", "nav"));
        spineIds.Add("nav");

        // Chapters
        for (int i = 0; i < chapters.Count; i++)
        {
            RenderProgress($"Building EPUB {title}", i + 1, chapters.Count);

            string chapId = $"chap{i + 1}";
            string chapHref = $"chapter{(i + 1).ToString("D3")}.xhtml";

            var xhtml = WrapChapterAsXhtml(title, i + 1, chapters[i].Name, chapters[i].Content, cssHref, chapId);

            var entry = zip.CreateEntry($"OEBPS/{chapHref}", CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                w.Write(xhtml);
            }

            manifestItems.Add((chapId, chapHref, "application/xhtml+xml", null));
            spineIds.Add(chapId);
        }

        // content.opf
        var opf = BuildOpf(title, manifestItems, spineIds);
        var opfEntry = zip.CreateEntry("OEBPS/content.opf", CompressionLevel.Optimal);
        using (var w = new StreamWriter(opfEntry.Open(), new UTF8Encoding(false)))
        {
            w.Write(opf);
        }
    }

    static string BuildCoverXhtml(string title, string coverImgHref, string cssHref)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
        sb.AppendLine(@"<!DOCTYPE html>");
        sb.AppendLine(@"<html xmlns=""http://www.w3.org/1999/xhtml"">");
        sb.AppendLine("<head>");
        sb.AppendLine(@"  <meta charset=""utf-8"" />");
        sb.AppendLine($@"  <title>{EscapeHtml(title)} - Cover</title>");
        sb.AppendLine($@"  <link rel=""stylesheet"" type=""text/css"" href=""{EscapeHtml(cssHref)}"" />");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine(@"  <div class=""cover"">");
        sb.AppendLine($@"    <img src=""{EscapeHtml(coverImgHref)}"" alt=""Cover"" />");
        sb.AppendLine(@"  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    static string BuildNavXhtml(string title, List<(string Name, string Content)> chapters)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
        sb.AppendLine(@"<!DOCTYPE html>");
        sb.AppendLine(@"<html xmlns=""http://www.w3.org/1999/xhtml"" xmlns:epub=""http://www.idpf.org/2007/ops"">");
        sb.AppendLine("<head>");
        sb.AppendLine(@"  <meta charset=""utf-8"" />");
        sb.AppendLine($"  <title>{EscapeHtml(title)} - Table of Contents</title>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($@"  <nav epub:type=""toc"" id=""toc"">");
        sb.AppendLine($"    <h1>{EscapeHtml(title)}</h1>");
        sb.AppendLine("    <ol>");

        for (int i = 0; i < chapters.Count; i++)
        {
            var name = chapters[i].Name ?? $"Chapter {i + 1}";
            var href = $"chapter{(i + 1).ToString("D3")}.xhtml#chap{i + 1}";
            sb.AppendLine($"      <li><a href=\"{href}\">Chapter {i + 1}: {EscapeHtml(name)}</a></li>");
        }

        sb.AppendLine("    </ol>");
        sb.AppendLine("  </nav>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    static string WrapChapterAsXhtml(string bookTitle, int chapterNumber, string chapterTitle, string htmlFragment, string cssHref, string anchorId)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
        sb.AppendLine(@"<!DOCTYPE html>");
        sb.AppendLine(@"<html xmlns=""http://www.w3.org/1999/xhtml"">");
        sb.AppendLine("<head>");
        sb.AppendLine(@"  <meta charset=""utf-8"" />");
        sb.AppendLine($@"  <title>{EscapeHtml(bookTitle)} - Chapter {chapterNumber}</title>");
        sb.AppendLine($@"  <link rel=""stylesheet"" type=""text/css"" href=""{EscapeHtml(cssHref)}"" />");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($@"  <h1 class=""chapter-title"" id=""{EscapeHtml(anchorId)}"">Chapter {chapterNumber}: {EscapeHtml(chapterTitle)}</h1>");
        sb.AppendLine(@"  <div>");
        sb.AppendLine(htmlFragment ?? "<p>(No content)</p>");
        sb.AppendLine(@"  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    static string BuildOpf(
        string title,
        List<(string id, string href, string mediaType, string properties)> manifestItems,
        List<string> spineIds)
    {
        var uid = "urn:uuid:" + Guid.NewGuid().ToString();

        var sb = new StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
        sb.AppendLine(@"<package xmlns=""http://www.idpf.org/2007/opf"" version=""3.0"" unique-identifier=""bookid"">");
        sb.AppendLine(@"  <metadata xmlns:dc=""http://purl.org/dc/elements/1.1/"">");
        sb.AppendLine($@"    <dc:identifier id=""bookid"">{EscapeHtml(uid)}</dc:identifier>");
        sb.AppendLine($@"    <dc:title>{EscapeHtml(title)}</dc:title>");
        sb.AppendLine(@"    <dc:language>en</dc:language>");
        sb.AppendLine(@"  </metadata>");

        sb.AppendLine(@"  <manifest>");
        foreach (var item in manifestItems)
        {
            var props = string.IsNullOrWhiteSpace(item.properties) ? "" : $@" properties=""{EscapeHtml(item.properties)}""";
            sb.AppendLine($@"    <item id=""{EscapeHtml(item.id)}"" href=""{EscapeHtml(item.href)}"" media-type=""{EscapeHtml(item.mediaType)}""{props} />");
        }
        sb.AppendLine(@"  </manifest>");

        sb.AppendLine(@"  <spine>");
        foreach (var id in spineIds)
        {
            sb.AppendLine($@"    <itemref idref=""{EscapeHtml(id)}"" />");
        }
        sb.AppendLine(@"  </spine>");

        sb.AppendLine(@"</package>");
        return sb.ToString();
    }

    static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
    }

    static string SanitizeFileName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "untitled";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (invalid.Contains(ch))
                sb.Append('-');
            else
                sb.Append(ch);
        }

        var s = sb.ToString()
            .Replace("/", "-")
            .Replace("\\", "-")
            .Replace(":", "-")
            .Replace("?", "")
            .Replace("*", "")
            .Replace("\"", "")
            .Replace("<", "")
            .Replace(">", "")
            .Replace("|", "");

        if (s.Length > 120) s = s.Substring(0, 120);
        return s.Trim();
    }

    static Dictionary<string, List<(string Text, string Href)>> BuildVolumeMap(
        AngleSharp.Dom.IHtmlCollection<AngleSharp.Dom.IElement> wrappers,
        Uri baseUri)
    {
        var map = new Dictionary<string, List<(string Text, string Href)>>();

        for (int i = 0; i < wrappers.Length; i++)
        {
            var w = wrappers[i];

            var titleEl = w.QuerySelector("h2.volume-title");
            var h2 = titleEl?.TextContent?.Trim();
            if (string.IsNullOrEmpty(h2))
                h2 = $"Untitled {i + 1}";

            var linkSelector = ".book-body > .chapter-entry > .body-web a";
            var links = w.QuerySelectorAll(linkSelector);

            var items = new List<(string Text, string Href)>();
            foreach (var a in links)
            {
                var txt = a.TextContent?.Trim() ?? string.Empty;
                var hrefRaw = a.GetAttribute("href") ?? string.Empty;

                string hrefAbs;
                try { hrefAbs = new Uri(baseUri, hrefRaw).ToString(); }
                catch { hrefAbs = hrefRaw; }

                items.Add((txt, hrefAbs));
            }

            var key = h2;
            if (map.ContainsKey(key))
                key = $"{h2} ({i + 1})";

            map[key] = items;
        }

        return map;
    }

    // --------- Offline Manifest Models ---------
    public class CacheManifest
    {
        public string TocUrl { get; set; }
        public DateTime GeneratedUtc { get; set; }
        public Dictionary<string, List<CachedChapterRef>> Volumes { get; set; }
    }

    public class CachedChapterRef
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string FileName { get; set; }
    }
}
