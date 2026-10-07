using System.Text.Json;
using Xunit;

namespace Normalizer.Tests;

public sealed class TextNormalizerTests
{
    public static TheoryData<string, string> Vectors()
    {
        var data = new TheoryData<string, string>();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")));
        foreach (var vector in document.RootElement.EnumerateArray())
            data.Add(vector.GetProperty("input").GetString()!, vector.GetProperty("expected").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Normalize_MatchesVector(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));
}
