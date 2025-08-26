using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Web;

namespace ArchiveOrgRecursiveSearch
{
    class Program
    {
        private static readonly HttpClient client = new HttpClient();
        private const int RowsPerPage = 50;
        private static string baseDownloadFolder = Directory.GetCurrentDirectory();
        private static string? lastSearchUrl = null;
        private static string fileTypeSelection = "pdf"; // default

        private const string ConfigFile = "appsettings.json";

        class Config
        {
            public string DownloadFolder { get; set; } = "";
            public string SearchUrl { get; set; } = "";
            public string FileTypes { get; set; } = "pdf"; // pdf, iso, both
        }

        class FileEntry
        {
            public string Name { get; set; } = "";
            public long Size { get; set; } = 0;
        }

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Archive.org Recursive Search & Downloader ===");

            // Load config
            Config config = LoadConfig();

            // Ask for download folder
            Console.Write($"Enter download folder (default: {config.DownloadFolder}): ");
            string? folderInput = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(folderInput) && !string.IsNullOrEmpty(config.DownloadFolder))
                baseDownloadFolder = config.DownloadFolder;
            else if (!string.IsNullOrEmpty(folderInput))
            {
                if (Directory.Exists(folderInput)) baseDownloadFolder = folderInput;
                else
                {
                    try { Directory.CreateDirectory(folderInput); baseDownloadFolder = folderInput; }
                    catch { Console.WriteLine("Invalid folder. Using current directory."); }
                }
                config.DownloadFolder = baseDownloadFolder;
            }

            // Ask for file type filter
            Console.Write("Download file types? (pdf / iso / both) " +
                          $"(default: {config.FileTypes}): ");
            string? typeInput = Console.ReadLine()?.Trim().ToLower();
            if (string.IsNullOrEmpty(typeInput)) fileTypeSelection = config.FileTypes;
            else if (typeInput == "pdf" || typeInput == "iso" || typeInput == "both")
            {
                fileTypeSelection = typeInput;
                config.FileTypes = fileTypeSelection;
            }
            else { fileTypeSelection = "pdf"; config.FileTypes = "pdf"; }

            while (true)
            {
                Console.Write($"Enter Archive.org search URL (default: {config.SearchUrl}): ");
                string searchUrl = Console.ReadLine()?.Trim();
                if (string.IsNullOrWhiteSpace(searchUrl))
                {
                    if (string.IsNullOrEmpty(config.SearchUrl))
                    { Console.WriteLine("URL cannot be empty."); continue; }
                    searchUrl = config.SearchUrl;
                }
                else config.SearchUrl = searchUrl;

                lastSearchUrl = searchUrl;

                // Save config each time user updates
                SaveConfig(config);

                // Extract query parameter
                Uri uri;
                try { uri = new Uri(searchUrl); }
                catch { Console.WriteLine("Invalid URL."); continue; }

                var queryParams = HttpUtility.ParseQueryString(uri.Query);
                string query = queryParams["query"];
                if (string.IsNullOrWhiteSpace(query))
                {
                    Console.WriteLine("No 'query' parameter found.");
                    continue;
                }

                Console.WriteLine($"\nSearching Archive.org for: {query}\n");
                var identifiers = await GetAllIdentifiers(query);
                if (identifiers.Count == 0) { Console.WriteLine("No items found."); continue; }

                Console.WriteLine("\nItems found:");
                for (int i = 0; i < identifiers.Count; i++)
                    Console.WriteLine($"{i + 1}. {identifiers[i]}");

                Console.Write("\nDownload from all identifiers? (y/n): ");
                string? allAns = Console.ReadLine();

                if (allAns?.Trim().ToLower() == "y")
                {
                    int downloadedIdentifiers = 0;
                    Console.Clear(); Console.WriteLine();
                    foreach (var id in identifiers)
                    {
                        downloadedIdentifiers++;
                        UpdateIdentifierProgress(downloadedIdentifiers, identifiers.Count);
                        await ListAndDownloadFiles(id, autoDownload: true);
                    }
                    Console.WriteLine("\nAll downloads completed.");
                    continue;
                }
                else
                {
                    Console.Write($"\nChoose an item number (1-{identifiers.Count}): ");
                    if (!int.TryParse(Console.ReadLine(), out int pick) || pick < 1 || pick > identifiers.Count)
                    { Console.WriteLine("Invalid choice."); continue; }

                    string selectedId = identifiers[pick - 1];
                    Console.Clear(); Console.WriteLine();
                    UpdateIdentifierProgress(1, 1);
                    await ListAndDownloadFiles(selectedId, autoDownload: false);
                    Console.WriteLine("\nDownload completed.");
                    continue;
                }
            }
        }

        static Config LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    string json = File.ReadAllText(ConfigFile);
                    return JsonSerializer.Deserialize<Config>(json) ?? new Config();
                }
            }
            catch { }
            return new Config();
        }

        static void SaveConfig(Config cfg)
        {
            try
            {
                string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }

        static async Task<List<string>> GetAllIdentifiers(string query)
        {
            var identifiers = new List<string>();
            int page = 1, total = 0;

            while (true)
            {
                string apiUrl =
                    $"https://archive.org/advancedsearch.php?q={Uri.EscapeDataString(query)}&fl[]=identifier&rows={RowsPerPage}&page={page}&output=json";

                string json;
                try { json = await client.GetStringAsync(apiUrl); }
                catch (Exception ex) { Console.WriteLine($"Error fetching page {page}: {ex.Message}"); break; }

                using JsonDocument doc = JsonDocument.Parse(json);
                var response = doc.RootElement.GetProperty("response");
                if (page == 1) { total = response.GetProperty("numFound").GetInt32(); Console.WriteLine($"Total results: {total}"); }

                var docs = response.GetProperty("docs");
                if (docs.GetArrayLength() == 0) break;

                foreach (var d in docs.EnumerateArray())
                    if (d.TryGetProperty("identifier", out var idEl)) identifiers.Add(idEl.GetString()!);

                if (identifiers.Count >= total) break;
                page++;
            }
            return identifiers;
        }

        static async Task ListAndDownloadFiles(string identifier, bool autoDownload)
        {
            string metadataUrl = $"https://archive.org/metadata/{identifier}";

            try
            {
                string metadataJson = await client.GetStringAsync(metadataUrl);
                using JsonDocument doc = JsonDocument.Parse(metadataJson);

                if (!doc.RootElement.TryGetProperty("files", out JsonElement files)) return;

                var selectedFiles = new List<FileEntry>();
                foreach (JsonElement file in files.EnumerateArray())
                {
                    string? name = file.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? format = file.TryGetProperty("format", out var f) ? f.GetString()?.ToLower() : null;

                    long size = 0;
                    if (file.TryGetProperty("size", out var s))
                    {
                        if (s.ValueKind == JsonValueKind.Number) size = s.GetInt64();
                        else if (s.ValueKind == JsonValueKind.String && long.TryParse(s.GetString(), out long parsed)) size = parsed;
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        bool matches = (fileTypeSelection == "both") ||
                                       (fileTypeSelection == "pdf" && format?.Contains("pdf") == true) ||
                                       (fileTypeSelection == "iso" && format?.Contains("iso") == true);

                        if (matches)
                            selectedFiles.Add(new FileEntry { Name = name, Size = size });
                    }
                }

                if (selectedFiles.Count == 0) return;

                bool shouldDownload = autoDownload;
                if (!autoDownload)
                {
                    Console.Write("\nDownload all files? (y/n): ");
                    string? ans = Console.ReadLine();
                    shouldDownload = ans?.Trim().ToLower() == "y";
                }

                if (shouldDownload)
                {
                    string downloadFolder = Path.Combine(baseDownloadFolder, identifier);
                    Directory.CreateDirectory(downloadFolder);

                    Console.WriteLine($"\n{identifier}:");
                    int baseLine = Console.CursorTop;
                    for (int i = 0; i < selectedFiles.Count; i++)
                    {
                        double fsizeMB = selectedFiles[i].Size / (1024.0 * 1024.0);
                        Console.WriteLine($"  {i + 1}. {selectedFiles[i].Name} ({fsizeMB:F2} MB)");
                    }

                    for (int i = 0; i < selectedFiles.Count; i++)
                    {
                        var fileEntry = selectedFiles[i];
                        string url = $"https://archive.org/download/{identifier}/{fileEntry.Name}";
                        string path = Path.Combine(downloadFolder, fileEntry.Name);

                        try
                        {
                            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            response.EnsureSuccessStatusCode();
                            using var stream = await response.Content.ReadAsStreamAsync();
                            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

                            byte[] buffer = new byte[64 * 1024];
                            long totalRead = 0;
                            var sw = System.Diagnostics.Stopwatch.StartNew();

                            while (true)
                            {
                                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                                if (read == 0) break;

                                await fs.WriteAsync(buffer, 0, read);
                                totalRead += read;

                                double percent = fileEntry.Size > 0 ? (totalRead / (double)fileEntry.Size) * 100 : 0;
                                double sizeMB = fileEntry.Size / (1024.0 * 1024.0);
                                double speedKBs = (totalRead / 1024.0) / Math.Max(1, sw.Elapsed.TotalSeconds);

                                Console.SetCursorPosition(0, baseLine + i);
                                DrawFileProgress(fileEntry.Name, i + 1, selectedFiles.Count, (int)percent, sizeMB, speedKBs);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.SetCursorPosition(0, baseLine + i);
                            Console.WriteLine($"  {i + 1}. {fileEntry.Name} [FAILED: {ex.Message}]");
                        }
                    }
                    Console.SetCursorPosition(0, baseLine + selectedFiles.Count);
                }
            }
            catch (Exception ex) { Console.WriteLine($"\n{identifier} - Error: {ex.Message}"); }
        }

        static void UpdateIdentifierProgress(int current, int total)
        {
            int percent = (int)((double)current / total * 100);
            Console.SetCursorPosition(0, 0);
            Console.Write($"Identifiers Progress: {current}/{total} ({percent}%)        ");
        }

        static void DrawFileProgress(string fileName, int current, int total, int percent, double sizeMB, double speedKBs)
        {
            int barLength = 20;
            int filled = (int)(percent / 100.0 * barLength);
            string bar = "[" + new string('#', filled) + new string('-', barLength - filled) + "]";
            Console.Write($"  {current}. {fileName} {bar} {current}/{total} ({percent}%)  {sizeMB:F2} MB @ {speedKBs:F1} kB/s      ");
        }
    }
}
