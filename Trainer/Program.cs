using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML;
using Microsoft.ML.Data;
using Normalizer;

// Trainer <data.json> <version> <output directory>
// Writes spam.mlnet and the manifest.json the Gheychi app reads to load it.
if (args.Length != 3 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
{
    Console.Error.WriteLine("Usage: Trainer <data.json> <version> <output directory>");
    return 2;
}

const string ModelFile = "spam.mlnet";
const string SpamLabel = "spam";
const string HamLabel = "ham";
const int Folds = 5;
const int Seed = 1;

// The app's default spam threshold (SpamDetector.DefaultThreshold). The gate is about what users see at that setting:
// a real message hidden as spam costs far more than an ad that reaches the inbox.
const float AppThreshold = 0.85f;
const double MinMacroAccuracy = 0.90;
const double MaxRealHiddenRate = 0.04;

var dataPath = args[0];
var outputDirectory = args[2];

var raw = JsonSerializer.Deserialize<List<DatasetItem>>(File.ReadAllText(dataPath)) ?? [];

// The app scores normalized text, so the model is trained on exactly what TextNormalizer produces, even for rows
// added before the normalizer reached its current form. A text labelled both ways teaches nothing and is dropped.
var rows = raw
    .Select(item => new Row { Text = TextNormalizer.Normalize(item.Text), Label = item.Label == 1 ? SpamLabel : HamLabel })
    .Where(row => row.Text.Length > 0)
    .GroupBy(row => row.Text)
    .Where(group => group.Select(row => row.Label).Distinct().Count() == 1)
    .Select(group => group.First())
    .ToList();

var spamCount = rows.Count(row => row.Label == SpamLabel);
Console.WriteLine($"{raw.Count} items, {rows.Count} distinct usable rows ({spamCount} spam, {rows.Count - spamCount} ham)");

var ml = new MLContext(Seed);
var data = ml.Data.LoadFromEnumerable(rows);

// Cross-validation rather than one holdout: on a few thousand rows a single 10% split moves by more than a point
// from one seed to the next, which hides the changes worth measuring.
var folds = ml.MulticlassClassification.CrossValidate(data, BuildPipeline(ml), Folds, seed: Seed);
var macro = folds.Select(f => f.Metrics.MacroAccuracy).ToList();
var macroMean = macro.Average();
var macroSd = Math.Sqrt(macro.Sum(m => (m - macroMean) * (m - macroMean)) / (macro.Count - 1));

int realHidden = 0, realTotal = 0, spamCaught = 0, spamTotal = 0;
foreach (var fold in folds)
{
    foreach (var (score, isSpam) in SpamScores(ml, fold.ScoredHoldOutSet))
    {
        if (isSpam)
        {
            spamTotal++;
            if (score >= AppThreshold) spamCaught++;
        }
        else
        {
            realTotal++;
            if (score >= AppThreshold) realHidden++;
        }
    }
}
var realHiddenRate = realHidden / (double)realTotal;
var spamCaughtRate = spamCaught / (double)spamTotal;

Console.WriteLine($"{Folds}-fold macro accuracy {macroMean:F4} (SD {macroSd:F4}; folds {string.Join(" ", macro.Select(m => m.ToString("F4", CultureInfo.InvariantCulture)))})");
Console.WriteLine($"At threshold {AppThreshold}: real messages hidden {realHidden} of {realTotal} ({realHiddenRate:P2}), spam caught {spamCaught} of {spamTotal} ({spamCaughtRate:P2})");

if (macroMean < MinMacroAccuracy || realHiddenRate > MaxRealHiddenRate)
{
    Console.Error.WriteLine($"Not publishing: needs macro accuracy >= {MinMacroAccuracy:P0} and at most {MaxRealHiddenRate:P0} of real messages hidden at {AppThreshold}.");
    return 1;
}

// The released model learns from every row; the folds above only measured how well this pipeline generalizes.
var model = BuildPipeline(ml).Fit(data);

Directory.CreateDirectory(outputDirectory);
var modelPath = Path.Combine(outputDirectory, ModelFile);
ml.Model.Save(model, data.Schema, modelPath);
VerifyLoads(modelPath);

var manifest = new JsonObject
{
    ["version"] = version,
    ["model"] = ModelFile,
    ["textColumn"] = nameof(Row.Text),
    ["labelColumn"] = nameof(Row.Label),
    ["spamLabel"] = SpamLabel,
    ["normalizer"] = TextNormalizer.Version,
    ["size"] = new FileInfo(modelPath).Length,
    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(modelPath))),
    ["metrics"] = new JsonObject
    {
        ["rows"] = rows.Count,
        ["spamRows"] = spamCount,
        ["folds"] = Folds,
        ["macroAccuracy"] = Math.Round(macroMean, 4),
        ["macroAccuracySd"] = Math.Round(macroSd, 4),
        ["threshold"] = AppThreshold,
        ["realHiddenRate"] = Math.Round(realHiddenRate, 4),
        ["spamCaughtRate"] = Math.Round(spamCaughtRate, 4)
    }
};
File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Model v{version} written to {outputDirectory}");
return 0;

// A linear model over the text features. Tree ensembles scored most messages at exactly 0 or 1, so the app's
// threshold setting barely changed anything; this one gives graded scores and hid half as many real messages at
// the same threshold in cross-validation. L2 0.03 was the best of the values tried.
static IEstimator<ITransformer> BuildPipeline(MLContext ml) =>
    ml.Transforms.Text.FeaturizeText("Features", nameof(Row.Text))
        .Append(ml.Transforms.Conversion.MapValueToKey(nameof(Row.Label)))
        .Append(ml.MulticlassClassification.Trainers.LbfgsMaximumEntropy(
            labelColumnName: nameof(Row.Label),
            featureColumnName: "Features",
            l1Regularization: 0,
            l2Regularization: 0.03f))
        .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

// The scored label column is a key: its value is the 1-based index of the label among the key values.
static IEnumerable<(float Score, bool IsSpam)> SpamScores(MLContext ml, IDataView scored)
{
    var labels = default(VBuffer<ReadOnlyMemory<char>>);
    scored.Schema[nameof(Row.Label)].GetKeyValues(ref labels);
    var spamIndex = labels.DenseValues().Select(v => v.ToString()).ToList().IndexOf(SpamLabel);
    foreach (var p in ml.Data.CreateEnumerable<ScoredRow>(scored, false))
        yield return (p.Score[spamIndex], p.Label == spamIndex + 1);
}

// The app finds the spam score by the label's position among the model's key values; check it can.
static void VerifyLoads(string modelPath)
{
    var ml = new MLContext();
    var model = ml.Model.Load(modelPath, out _);
    var engine = ml.Model.CreatePredictionEngine<Row, Prediction>(model);
    var labels = default(VBuffer<ReadOnlyMemory<char>>);
    engine.OutputSchema[nameof(Row.Label)].GetKeyValues(ref labels);
    var names = labels.DenseValues().Select(v => v.ToString()).ToList();
    if (!names.Contains(SpamLabel) || engine.Predict(new Row()).Score.Length != names.Count)
        throw new InvalidDataException($"The saved model's labels ({string.Join(", ", names)}) do not include '{SpamLabel}'.");
}

sealed record DatasetItem(string Text, int Label);

sealed class Row
{
    public string Text { get; set; } = "";
    public string Label { get; set; } = "";
}

sealed class Prediction
{
    public float[] Score { get; set; } = [];
}

sealed class ScoredRow
{
    public uint Label { get; set; }
    public float[] Score { get; set; } = [];
}
