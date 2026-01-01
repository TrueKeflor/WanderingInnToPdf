using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using AngleSharp;
using AngleSharp.Html.Parser;
using PuppeteerSharp;


class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: <program> <url>");
            Console.WriteLine("Example: dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- https://example.com");
            Console.WriteLine("Note: section id is hardcoded to 'table-of-contents'.");
            return 1;
        }

        var url = args[0];
        var sectionId = "table-of-contents";

        // Create staging folder for chapters
        var stagingFolder = Path.Combine(Directory.GetCurrentDirectory(), "chapters");
        Directory.CreateDirectory(stagingFolder);

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WanderingInnToPdf/1.0 (+https://example)");
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

            var map = BuildVolumeMap(wrappers);

            // Limit to first 2 volumes by default
            var limitedMap = map.Take(2).ToDictionary(kv => kv.Key, kv => kv.Value);

            // Build chapter map by fetching content from each href
            var chapterMap = new Dictionary<string, List<(string Name, string Content)>>();
            foreach (var kv in limitedMap)
            {
                var volumeKey = kv.Key;
                Console.WriteLine($"Processing volume: {volumeKey}");
                var chapterContents = new List<(string Name, string Content)>();
                int chapterIndex = 0;
                foreach (var item in kv.Value)
                {
                    chapterIndex++;
                    var chapterFileName = $"{chapterIndex}_{item.Text.Replace("/", "-").Replace("\\", "-").Replace(":", "-").Replace("?", "").Replace("*", "").Replace("\"", "").Replace("<", "").Replace(">", "").Replace("|", "")}.txt";
                    var chapterFilePath = Path.Combine(stagingFolder, chapterFileName);
                    string extractedContent;
                    if (File.Exists(chapterFilePath))
                    {
                        Console.WriteLine($"  Using cached chapter {chapterIndex} from {chapterFilePath}");
                        extractedContent = File.ReadAllText(chapterFilePath);
                    }
                    else
                    {
                        Console.WriteLine($"  Fetching chapter {chapterIndex} from {item.Href}");
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
                            extractedContent = $"Error fetching {item.Href}: {ex.Message}";
                            File.WriteAllText(chapterFilePath, extractedContent);
                        }
                    }
                    chapterContents.Add((item.Text, extractedContent));
                }
                chapterMap[volumeKey] = chapterContents;
            }

            // Generate PDF for each volume using Puppeteer
            await new BrowserFetcher().DownloadAsync();
            using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true });
            foreach (var kv in chapterMap)
            {
                var volumeKey = kv.Key;
                var chapters = kv.Value;
                var fileName = $"{volumeKey.Replace("/", "-").Replace("\\", "-").Replace(":", "-")}.pdf";
                Console.WriteLine($"Generating PDF: {fileName}");

                // Build HTML content for the volume
                var htmlBuilder = new System.Text.StringBuilder();
                htmlBuilder.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><title>" + volumeKey + "</title></head><body>");
                htmlBuilder.AppendLine("<h1>" + volumeKey + "</h1>");
                for (int i = 0; i < chapters.Count; i++)
                {
                    var chapter = chapters[i];
                    htmlBuilder.AppendLine("<h2>Chapter " + (i + 1) + ": " + chapter.Name + "</h2>");
                    htmlBuilder.AppendLine("<div>" + chapter.Content + "</div>");
                }
                htmlBuilder.AppendLine("</body></html>");

                var page = await browser.NewPageAsync();
                await page.SetContentAsync(htmlBuilder.ToString());
                await page.PdfAsync(fileName);
                await page.CloseAsync();
            }
            await browser.CloseAsync();


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

    static Dictionary<string, List<(string Text, string Href)>> BuildVolumeMap(AngleSharp.Dom.IHtmlCollection<AngleSharp.Dom.IElement> wrappers)
    {
        var map = new Dictionary<string, List<(string Text, string Href)>>();
        for (int i = 0; i < wrappers.Length; i++)
        {
            var w = wrappers[i];

            // select only an h2 that also has the 'volume-title' class
            var titleEl = w.QuerySelector("h2.volume-title");
            var h2 = titleEl?.TextContent?.Trim();
            if (string.IsNullOrEmpty(h2))
            {
                h2 = $"Untitled {i + 1}";
            }

            // select links under the specified selector within this wrapper
            var linkSelector = ".book-body > .chapter-entry > .body-web a";
            var links = w.QuerySelectorAll(linkSelector);
            var items = new List<(string Text, string Href)>();
            foreach (var a in links)
            {
                var txt = a.TextContent?.Trim() ?? string.Empty;
                var href = a.GetAttribute("href") ?? string.Empty;
                items.Add((txt, href));
            }

            // avoid key collisions by appending index if necessary
            var key = h2;
            if (map.ContainsKey(key))
            {
                key = $"{h2} ({i + 1})";
            }

            map[key] = items;
        }
        return map;
    }
}