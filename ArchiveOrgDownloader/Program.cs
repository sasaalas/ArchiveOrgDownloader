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
        private const int RowsPerPage = 50; // results per page from API
        private static string baseDownloadFolder = Directory.GetCurrentDirectory();
        private static string? lastSearchUrl = null;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Archive.org Recursive Search & Downloader ===");

            Console.Write("Enter download folder (leave empty for current directory): ");
            string? folderInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(folderInput))
            {
                if (Directory.Exists(folderInput))
                {
                    baseDownloadFolder = folderInput;
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(folderInput);
                        baseDownloadFolder = folderInput;
                    }
                    catch
                    {
                        Console.WriteLine("Invalid folder. Using current directory instead.");
                    }
                }
            }

            while (true)
            {
                Console.Write($"Enter Archive.org search URL{(lastSearchUrl != null ? $" (default: {lastSearchUrl})" : "")}: ");
                string searchUrl = Console.ReadLine()?.Trim();

                if (string.IsNullOrWhiteSpace(searchUrl))
                {
                    if (lastSearchUrl == null)
                    {
                        Console.WriteLine("URL cannot be empty.");
                        continue;
                    }
                    searchUrl = lastSearchUrl;
                }
                else
                {
                    lastSearchUrl = searchUrl;
                }

                // Extract query parameter from given URL
                Uri uri;
                try { uri = new Uri(searchUrl); }
                catch { Console.WriteLine("Invalid URL."); continue; }

                var queryParams = HttpUtility.ParseQueryString(uri.Query);
                string query = queryParams["query"];
                if (string.IsNullOrWhiteSpace(query))
                {
                    Console.WriteLine("No 'query' parameter found in URL.");
                    continue;
                }

                Console.WriteLine($"\nSearching Archive.org for: {query}\n");

                // Get identifiers recursively
                var identifiers = await GetAllIdentifiers(query);

                if (identifiers.Count == 0)
                {
                    Console.WriteLine("No items found.");
                    continue;
                }

                Console.WriteLine("\nItems found:");
                for (int i = 0; i < identifiers.Count; i++)
                    Console.WriteLine($"{i + 1}. {identifiers[i]}");

                Console.Write("\nDownload from all identifiers? (y/n): ");
                string? allAns = Console.ReadLine();

                if (allAns?.Trim().ToLower() == "y")
                {
                    foreach (var id in identifiers)
                    {
                        Console.WriteLine($"\nProcessing identifier: {id}");
                        await ListAndDownloadFiles(id, autoDownload: true);
                    }
                    Console.WriteLine("\nAll downloads completed. Returning to identifier listing...\n");
                    continue;
                }
                else
                {
                    Console.Write($"\nChoose an item number (1-{identifiers.Count}): ");
                    if (!int.TryParse(Console.ReadLine(), out int pick) ||
                        pick < 1 || pick > identifiers.Count)
                    {
                        Console.WriteLine("Invalid choice.");
                        continue;
                    }

                    string selectedId = identifiers[pick - 1];
                    Console.WriteLine($"\nSelected identifier: {selectedId}");

                    await ListAndDownloadFiles(selectedId, autoDownload: false);
                    Console.WriteLine("\nDownload completed. Returning to identifier listing...\n");
                    continue;
                }
            }
        }

        // ----- Recursively fetch all identifiers -----
        static async Task<List<string>> GetAllIdentifiers(string query)
        {
            var identifiers = new List<string>();

            int page = 1;
            int total = 0;

            while (true)
            {
                string apiUrl =
                    $"https://archive.org/advancedsearch.php?q={Uri.EscapeDataString(query)}&fl[]=identifier&rows={RowsPerPage}&page={page}&output=json";

                string json;
                try { json = await client.GetStringAsync(apiUrl); }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error fetching page {page}: {ex.Message}");
                    break;
                }

                using JsonDocument doc = JsonDocument.Parse(json);
                var response = doc.RootElement.GetProperty("response");
                if (page == 1)
                {
                    total = response.GetProperty("numFound").GetInt32();
                    Console.WriteLine($"Total results: {total}");
                }

                var docs = response.GetProperty("docs");
                if (docs.GetArrayLength() == 0)
                    break;

                foreach (var d in docs.EnumerateArray())
                {
                    if (d.TryGetProperty("identifier", out var idEl))
                        identifiers.Add(idEl.GetString()!);
                }

                if (identifiers.Count >= total)
                    break;

                page++;
            }

            return identifiers;
        }

        // ----- List all files and optionally download -----
        static async Task ListAndDownloadFiles(string identifier, bool autoDownload)
        {
            string metadataUrl = $"https://archive.org/metadata/{identifier}";

            try
            {
                string metadataJson = await client.GetStringAsync(metadataUrl);
                using JsonDocument doc = JsonDocument.Parse(metadataJson);

                if (!doc.RootElement.TryGetProperty("files", out JsonElement files))
                {
                    Console.WriteLine("No files found in metadata.");
                    return;
                }

                var allFiles = new List<string>();
                int index = 1;
                foreach (JsonElement file in files.EnumerateArray())
                {
                    string? name = file.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? format = file.TryGetProperty("format", out var f) ? f.GetString() : null;

                    if (!string.IsNullOrEmpty(name))
                    {
                        allFiles.Add(name);
                        Console.WriteLine($"{index}. {name}   ({format})");
                        index++;
                    }
                }

                if (allFiles.Count == 0)
                {
                    Console.WriteLine("No files found.");
                    return;
                }

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

                    foreach (string fname in allFiles)
                    {
                        string url = $"https://archive.org/download/{identifier}/{fname}";
                        string path = Path.Combine(downloadFolder, fname);
                        Console.WriteLine($"Downloading {fname}...");

                        try
                        {
                            byte[] data = await client.GetByteArrayAsync(url);
                            await File.WriteAllBytesAsync(path, data);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed: {fname} ({ex.Message})");
                        }
                    }
                    Console.WriteLine("All files downloaded.");
                }
                else
                {
                    Console.WriteLine("No files downloaded.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching metadata: {ex.Message}");
            }
        }
    }
}
