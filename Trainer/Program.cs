using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
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
const double MinMacroAccuracy = 0.90;
const int Seed = 1;

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

var split = ml.Data.TrainTestSplit(data, testFraction: 0.1, seed: Seed);
var metrics = ml.MulticlassClassification.Evaluate(BuildPipeline(ml).Fit(split.TrainSet).Transform(split.TestSet));
Console.WriteLine($"Macro accuracy {metrics.MacroAccuracy:F4}, micro accuracy {metrics.MicroAccuracy:F4}, log loss {metrics.LogLoss:F4}");
Console.WriteLine(metrics.ConfusionMatrix.GetFormattedConfusionTable());

if (metrics.MacroAccuracy < MinMacroAccuracy)
{
    Console.Error.WriteLine($"Macro accuracy is below {MinMacroAccuracy:F2}; not publishing this model.");
    return 1;
}

// The released model learns from every row; the split above only measured how well this pipeline generalizes.
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
        ["macroAccuracy"] = Math.Round(metrics.MacroAccuracy, 4),
        ["microAccuracy"] = Math.Round(metrics.MicroAccuracy, 4)
    }
};
File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Model v{version} written to {outputDirectory}");
return 0;

// The pipeline Model Builder chose for the first model, with its tuned hyperparameters, so retraining only changes the data.
static IEstimator<ITransformer> BuildPipeline(MLContext ml) =>
    ml.Transforms.Text.FeaturizeText("Features", nameof(Row.Text))
        .Append(ml.Transforms.Conversion.MapValueToKey(nameof(Row.Label)))
        .Append(ml.MulticlassClassification.Trainers.OneVersusAll(
            ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options
            {
                NumberOfLeaves = 288,
                MinimumExampleCountPerLeaf = 11,
                NumberOfTrees = 52,
                MaximumBinCountPerFeature = 391,
                FeatureFraction = 0.6822378736053805,
                LearningRate = 0.9999997766729865,
                LabelColumnName = nameof(Row.Label),
                FeatureColumnName = "Features",
                DiskTranspose = false
            }),
            labelColumnName: nameof(Row.Label)))
        .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

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
