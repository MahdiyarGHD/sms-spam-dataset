 **Persian SMS Spam (Including Scam/Ads/Annoying Messages) Dataset**
   * This repo contains a dataset of Persian SMS messages that are labeled as spam or ham. The dataset is collected from different sources and contains different types of spam messages including scam, ads, and annoying messages. The dataset is collected for research purposes and can be used for different tasks such as text classification, text clustering, and text summarization. The dataset is collected in a JSON file format and contains two properties: "Text" and "Label". The "Text" column contains the SMS messages and the "Label" column contains the labels of the messages (0 for ham & 1 for spam).


*Data offer: https://forms.gle/zFNUFxDAy5vkZaMEA*


**Spam model for Gheychi**
   * `Trainer/` trains the spam model used by the Gheychi SMS app on `data.json`. Pushing a tag `model-v<N>` (e.g. `model-v3`) runs `.github/workflows/train.yml`, which trains model version N and publishes `spam.mlnet` and `manifest.json` as the latest release. The app offers that release as an update, and the app's own release build bundles it.
   * `Normalizer/TextNormalizer.cs` is the normalization applied both to new dataset entries and, inside the app, to every message before it is scored. The app keeps an identical copy; `Normalizer/vectors.json` pins its output and is checked by the tests in both repositories. Any change to the normalizer must bump `TextNormalizer.Version` and regenerate the vectors in both places.
   * Releases in this repository should only be model releases, since the app reads the latest one.
