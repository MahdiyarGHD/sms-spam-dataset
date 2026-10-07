
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text;
using System.Text.Encodings.Web;
using Normalizer;

try
{
    string commentBody = Environment.GetEnvironmentVariable("COMMENT_BODY") ?? "";
    string issueNumber = Environment.GetEnvironmentVariable("ISSUE_NUMBER") ?? "";
    string repository = Environment.GetEnvironmentVariable("REPOSITORY") ?? "";
    string githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";

    bool hasHam = commentBody.StartsWith("!AddHam");
    bool hasSpam = commentBody.StartsWith("!AddSpam");

    if (!hasSpam && !hasHam)
    {
        Console.WriteLine("No normalization command found in comment");
        return;
    }

    string content = hasSpam ? commentBody["!AddSpam".Length..].Trim() : commentBody["!AddHam".Length..].Trim();

    var pattern = @"(?:- |\* )\s*";
    var matches = Regex.Split(content, pattern)
        .Select(s => s.Trim())
        .Where(s => !string.IsNullOrEmpty(s))
        .ToList();

    List<string> elements = [];
    elements.AddRange(matches);

    if (elements.Count == 0)
    {
        await CommentOnIssue(repository, issueNumber, githubToken,
            "No elements found to normalize. Please provide a list like:\n!NormalizeAndAdd\n- item1\n- item2");
        return;
    }

    List<NormalizedItem> normalizedResults = [];
    normalizedResults.AddRange(
        from element in elements
        select new NormalizedItem { Text = TextNormalizer.Normalize(element), Label = hasSpam ? 1 : 0 });

    await UpdateJsonFile(repository, githubToken, normalizedResults);

    string comment = "Normalization Results added to JSON file:\n\n" +
                     string.Join("\n\n", normalizedResults.Select(r => $"- {r.Text}"));
    await CommentOnIssue(repository, issueNumber, githubToken, comment);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Environment.Exit(1);
}

static async Task CommentOnIssue(string repository, string issueNumber, string token, string comment)
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("Authorization", $"token {token}");
    client.DefaultRequestHeaders.Add("User-Agent", "NormalizeTextAction");

    string apiUrl = $"https://api.github.com/repos/{repository}/issues/{issueNumber}/comments";

    var content = new StringContent(
        JsonSerializer.Serialize(new { body = comment }),
        Encoding.UTF8,
        "application/json"
    );

    var response = await client.PostAsync(apiUrl, content);
    response.EnsureSuccessStatusCode();
}

static async Task UpdateJsonFile(string repository, string token, List<NormalizedItem> newItems)
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("Authorization", $"token {token}");
    client.DefaultRequestHeaders.Add("User-Agent", "NormalizeTextAction");

    string filePath = "data.json";
    string apiUrl = $"https://api.github.com/repos/{repository}/contents/{filePath}";
    List<NormalizedItem> existingItems = [];
    string? currentSha = null;

    try
    {
        var getResponse = await client.GetAsync(apiUrl);
        if (getResponse.IsSuccessStatusCode)
        {
            string content = await getResponse.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("download_url", out var downloadUrlProp))
            {
                string downloadUrl = downloadUrlProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(downloadUrl))
                {
                    var fileResponse = await client.GetAsync(downloadUrl);
                    if (fileResponse.IsSuccessStatusCode)
                    {
                        string existingJson = await fileResponse.Content.ReadAsStringAsync();
                        existingItems = JsonSerializer.Deserialize<List<NormalizedItem>>(existingJson) ?? new List<NormalizedItem>();
                    }
                }
            }

            if (root.TryGetProperty("sha", out var shaProp))
            {
                currentSha = shaProp.GetString();
            }
        }
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
    }


    existingItems.AddRange(newItems);

    string newJsonContent = JsonSerializer.Serialize(existingItems, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
    string newBase64Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJsonContent));

    var payload = new
    {
        message = $"Github Action: Add {newItems.Count} normalized items",
        content = newBase64Content,
        sha = currentSha
    };

    var putContent = new StringContent(
        JsonSerializer.Serialize(payload),
        Encoding.UTF8,
        "application/json"
    );

    var putResponse = await client.PutAsync(apiUrl, putContent);
    if (!putResponse.IsSuccessStatusCode)
    {
        Console.WriteLine($"Failed to update file: {putResponse.StatusCode}");
        Console.WriteLine(await putResponse.Content.ReadAsStringAsync());
        putResponse.EnsureSuccessStatusCode();
    }
}

record NormalizedItem
{
    public string Text { get; set; } = "";
    public int Label { get; set; } = 1;
}
