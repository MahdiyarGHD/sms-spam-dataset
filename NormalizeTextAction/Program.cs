
using System.Text.RegularExpressions;
using System.Text.Json;ع
using System.Text;
using System.Text.Encodings.Web;

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
        select new NormalizedItem { Text = NormalizeText(element), Label = hasSpam ? 1 : 0 });

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
   var modifiedText = text ?? "";

    // 1. Replace HTTP/HTTPS links
    modifiedText = Regex.Replace(modifiedText,
        @"(?:https?://)?(?:[-\w.]|(?:%[\da-fA-F]{2}))+(?:\.[a-zA-Z]{2,})(?:/[^\s]*)?",
        "[LINK_REMOVED]");

    // 2. Replace phone numbers
    modifiedText = Regex.Replace(modifiedText,
        @"(?:\+|00)?(?:\d[\s-]?){9,12}\d",
        "[MASKED_NUMBER]");

    // 3. Remove emojis
    modifiedText = Regex.Replace(modifiedText,
        @"[\uD83C-\uD83E][\uDC00-\uDFFF]|\uD83D[\uDC00-\uDE4F\uDE80-\uDEFF]|[\u2600-\u26FF\u2700-\u27BF]",
        "");

    // 4. Normalize spaces
    modifiedText = Regex.Replace(modifiedText,
        @"[\s\u200C]+",
        " ").Trim();

    // 4. Convert Latin digits to Persian digits
    modifiedText = ToPersianDigits(modifiedText);

    // 6. Replace dates with "[DATE]"
    modifiedText = Regex.Replace(modifiedText,
        @"[0-9\u06F0-\u06F9]+\/[0-9\u06F0-\u06F9]+\/[0-9\u06F0-\u06F9]+",
        "[DATE]");

    // 7. Replace times with "[TIME]"
    modifiedText = ReplaceTimes(modifiedText);

    // 8. Normalize Persian text
    modifiedText = modifiedText
        .Replace("ك", "ک")
        .Replace("ي", "ی")
        .Replace("ة", "ه")
        .Replace("لغو۱۱", "");

    // 9. Replace USSD codes
    modifiedText = Regex.Replace(modifiedText,
        @"[\*\#][\d\*]{2,15}\#",
        "[USSD_REMOVED]");

    // 10. Replace all sequences of digits with "[RANDOM_NUMBER]"
    modifiedText = Regex.Replace(modifiedText, @"\d+", "[RANDOM_NUMBER]");

    // 11. Temporarily protect placeholders
    modifiedText = modifiedText.Replace("[DATE]", "%%%4%%%");
    modifiedText = modifiedText.Replace("[TIME]", "%%%5%%%");
    modifiedText = modifiedText.Replace("[MASKED_NUMBER]", "%%%6%%%");
    modifiedText = modifiedText.Replace("[NAME]", "%%%7%%%");
    modifiedText = modifiedText.Replace("[ENGLISH]", "%%%8%%%");
    modifiedText = modifiedText.Replace("[LINK_REMOVED]", "%%%1%%%");
    modifiedText = modifiedText.Replace("[RANDOM_NUMBER]", "%%%2%%%");
    modifiedText = modifiedText.Replace("[USSD_REMOVED]", "%%%3%%%");

    // 12. Replace English words with "[ENGLISH]"
    modifiedText = Regex.Replace(modifiedText, @"[A-Za-z]+", "[ENGLISH]");

    // 13. Restore original placeholders
    modifiedText = modifiedText.Replace("%%%4%%%", "[DATE]");
    modifiedText = modifiedText.Replace("%%%5%%%", "[TIME]");
    modifiedText = modifiedText.Replace("%%%6%%%", "[MASKED_NUMBER]");
    modifiedText = modifiedText.Replace("%%%7%%%", "[NAME]");
    modifiedText = modifiedText.Replace("%%%8%%%", "[ENGLISH]");
    modifiedText = modifiedText.Replace("%%%1%%%", "[LINK_REMOVED]");
    modifiedText = modifiedText.Replace("%%%2%%%", "[RANDOM_NUMBER]");
    modifiedText = modifiedText.Replace("%%%3%%%", "[USSD_REMOVED]");

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

static string ReplaceTimes(string text)
{
    string pattern24 = @"(\b([01]?[0-9]|2[0-3]):[0-5][0-9](:[0-5][0-9])?\b)|" +
                       @"(\b([۰-۹]?[۰-۹]|۱[۰-۹]|۲[۰-۳]):[۰-۵][۰-۹](:[۰-۵][۰-۹])?\b)";

    string pattern12 = @"(\b([1-9]|1[0-2]):[0-5][0-9](:[0-5][0-9])?\s?(AM|PM|am|pm)\b)|" +
                       @"(\b([۱-۹]|۱[۰-۲]):[۰-۵][۰-۹](:[۰-۵][۰-۹])?\s?(ص|ع)\b)";

    string combinedPattern = $@"{pattern24}|{pattern12}";

    return Regex.Replace(text, combinedPattern, "[TIME]");
}

static string ToPersianDigits(string input)
{
    const string PersianDigits = "۰۱۲۳۴۵۶۷۸۹";

    if (string.IsNullOrEmpty(input))
        return input;

    System.Text.StringBuilder sb = new System.Text.StringBuilder(input.Length);
    foreach (char c in input)
    {
        if (c >= '0' && c <= '9')
        {
            int digit = c - '0';
            sb.Append(PersianDigits[digit]);
        }
        else if (c >= '٠' && c <= '٩')
        {
            int digit = c - '٠';
            sb.Append(PersianDigits[digit]);
        }
        else
            sb.Append(c);
    }
    return sb.ToString();
}

record NormalizedItem
{
    public string Text { get; set; } = "";
    public int Label { get; set; } = 1;
}
