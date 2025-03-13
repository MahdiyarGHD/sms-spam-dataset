
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text;
try
{
    string commentBody = Environment.GetEnvironmentVariable("COMMENT_BODY") ?? "";
    string issueNumber = Environment.GetEnvironmentVariable("ISSUE_NUMBER") ?? "";
    string repository = Environment.GetEnvironmentVariable("REPOSITORY") ?? "";
    string githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";

    if (!commentBody.StartsWith("!NormalizeAndAdd"))
    {
        Console.WriteLine("No normalization command found in comment");
        return;
    }

    string content = commentBody["!NormalizeAndAdd".Length..].Trim();

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
        select new NormalizedItem { Text = NormalizeText(element), Label = 1 });

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

static string NormalizeText(string text)
{
    string modifiedText = text;

    // 1. Replace HTTP/HTTPS links
    modifiedText = Regex.Replace(modifiedText,
        @"(?:https?://)?(?:[-\w.]|(?:%[\da-fA-F]{2}))+(?:\.[a-zA-Z]{2,})(?:/[^\s]*)?",
        "[LINK_REMOVED]");

    // 2. Replace phone numbers
    modifiedText = Regex.Replace(modifiedText,
        @"(?:\+|00)?(?:\d[\s-]?){9,12}\d",
        "[RANDOM_NUMBER]");

    // 3. Remove emojis
    modifiedText = Regex.Replace(modifiedText,
        @"[\uD83C-\uD83E][\uDC00-\uDFFF]|\uD83D[\uDC00-\uDE4F\uDE80-\uDEFF]|[\u2600-\u26FF\u2700-\u27BF]",
        "");

    // 4. Normalize spaces
    modifiedText = Regex.Replace(modifiedText,
        @"[\s\u200C]+",
        " ").Trim();

    // 5. Normalize Persian text directly
    modifiedText = modifiedText
        .Replace("ك", "ک")
        .Replace("ي", "ی")
        .Replace("ة", "ه");

    // 6. Replace USSD codes
    modifiedText = Regex.Replace(modifiedText,
        @"[\*\#][\d\*]{2,15}\#",
        "[USSD_REMOVED]");

    return modifiedText;
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
            if (root.TryGetProperty("content", out var contentProp))
            {
                string base64Content = contentProp.GetString();
                string existingJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64Content));
                existingItems = JsonSerializer.Deserialize<List<NormalizedItem>>(existingJson) ?? [];
            }
            if (root.TryGetProperty("sha", out var shaProp))
            {
                currentSha = shaProp.GetString();
            }
        }
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {}

    existingItems.AddRange(newItems);

    string newJsonContent = JsonSerializer.Serialize(existingItems, new JsonSerializerOptions { WriteIndented = true });
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

static async Task<string> GetFileSha(string repository, string filePath, string token)
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("Authorization", $"token {token}");
    client.DefaultRequestHeaders.Add("User-Agent", "NormalizeTextAction");

    string apiUrl = $"https://api.github.com/repos/{repository}/contents/{filePath}";
    var response = await client.GetAsync(apiUrl);
    response.EnsureSuccessStatusCode();

    string content = await response.Content.ReadAsStringAsync();
    var fileData = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
    return fileData["sha"];
}

record NormalizedItem
{
    public string Text { get; set; } = "";
    public int Label { get; set; } = 1;
}