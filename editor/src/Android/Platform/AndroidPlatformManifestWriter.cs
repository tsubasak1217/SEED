// ============================================================
//  AndroidPlatformManifestWriter.cs — プラットフォーム機能 → マニフェストの断片と values の XML（純粋な処理。W1-2）
//
//  【作るもの】（置き場は runtime/android/app/src/seedFeatures/。Gradle が variant の API で足す。build.gradle.kts）
//    AndroidManifest.xml
//      <uses-permission android:name android:maxSdkVersion>        … 有効な機能の権限（表の順）
//      <application>
//        <activity android:name="com.seedengine.runtime.MainActivity"> … ディープリンク 1 件ごとに intent-filter
//          <intent-filter [android:autoVerify="true"]>                （VIEW・DEFAULT・BROWSABLE と data 1 つ。
//                                                                      1 つの intent-filter の data は掛け合わされるので件ごとに分ける）
//        （機能の表の application_elements。受信機・サービス等。W1-3・W1-4）
//      機能が無ければ中身の無い <manifest/>（古い生成物を残さないため、空でも必ず書く）
//    res/values/seed_platform.xml
//      <bool name="seed_system_bars_visible">                      … system_bars が visible なら true
//      <bool name="seed_predictive_back">true</bool>                 … predictive_back が true のときだけ（W2 の手直し P1-3。
//                                                                      false のプロジェクトは頭のコメントも含めて従来と同じバイト列）
//  main のマニフェストの MainActivity は相対名（.MainActivity）で、ここは完全修飾名で書く（マージは名前空間
//  com.seedengine.runtime で解いた完全修飾名で同じ activity とみなす。2026-09-27 に実ビルドの aapt2 で確かめた）。
//  同じ入力からは同じバイト列（UTF-8・BOM なし・改行 LF・字下げ 4）を作る（APK の工程の指紋が中身の SHA-256 のため）。
//  値は XmlWriter が必ずエスケープするので、設定の文字がマニフェストの構造を壊すことはない。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Platform;

/// <summary>マニフェストの断片と values の XML を作る。</summary>
public static class AndroidPlatformManifestWriter
{
    /// <summary>Android の XML の名前空間。</summary>
    public const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    /// <summary>Android の名前空間の前置き。</summary>
    public const string AndroidPrefix = "android";

    /// <summary>
    /// システムバーを出したままにするかの bool のリソース名。MainActivity 側の SystemBarsController.java の
    /// R.bool.seed_system_bars_visible と、main の res/values/seed_platform_defaults.xml の既定値（false）と一致させる
    /// （単体テストが 3 か所を突き合わせる）。
    /// </summary>
    public const string SystemBarsVisibleResourceName = "seed_system_bars_visible";

    /// <summary>
    /// 予測型の戻るを使う印の bool のリソース名（W2 の手直し P1-3）。Java の back/BackCallbackController の
    /// R.bool.seed_predictive_back と、main の res/values/seed_platform_defaults.xml の既定値（false）と、build.gradle.kts の
    /// 食い違いの確かめ（predictiveBackResourceLine）と一致させる（単体テストが突き合わせる）。
    /// </summary>
    public const string PredictiveBackResourceName = "seed_predictive_back";

    /// <summary>ディープリンクの intent-filter の action。</summary>
    public const string ViewAction = "android.intent.action.VIEW";

    /// <summary>ディープリンクの intent-filter の category（暗黙の Intent を受ける）。</summary>
    public const string DefaultCategory = "android.intent.category.DEFAULT";

    /// <summary>ディープリンクの intent-filter の category（ブラウザ等のリンクから開ける）。</summary>
    public const string BrowsableCategory = "android.intent.category.BROWSABLE";

    /// <summary>字下げ（main のマニフェストと同じ 4 文字）。</summary>
    private const string IndentChars = "    ";

    /// <summary>改行（リポジトリの .xml と同じ LF）。</summary>
    private const string NewLine = "\n";

    /// <summary>生成物の頭のコメント（編集しないこと・出どころ）。</summary>
    private const string GeneratedNotice =
        " SeedAndroid が生成したファイル（編集しない。ビルドのたびに作り直す）。W1-2・docs/android.md §25.10。" +
        "\n  元: プロジェクト設定 android.features / deep_links / system_bars（機能の表 runtime/android/platform_features.json）";

    /// <summary>
    /// ファイル一式を作る。
    /// </summary>
    /// <param name="set">このビルドのプラットフォーム機能。</param>
    /// <returns>ファイル一式（マニフェストの断片と values）。</returns>
    public static AndroidPlatformFeatureFiles Render(AndroidPlatformFeatureSet set) => new(new Dictionary<string, byte[]>
    {
        [AndroidPlatformFeatureFiles.ManifestRelativePath] = WriteManifest(set),
        [AndroidPlatformFeatureFiles.ValuesRelativePath] = WriteValues(set),
    });

    /// <summary>マニフェストの断片を書く。</summary>
    /// <param name="set">このビルドのプラットフォーム機能。</param>
    /// <returns>UTF-8 のバイト列。</returns>
    public static byte[] WriteManifest(AndroidPlatformFeatureSet set) => WriteDocument(writer =>
    {
        writer.WriteComment(GeneratedNotice + NewLine + "  機能: " +
                            (set.Features.Count == 0 ? "なし（権限・部品を足さない）" : string.Join(", ", set.FeatureNames)) + " ");
        writer.WriteStartElement("manifest");
        writer.WriteAttributeString("xmlns", AndroidPrefix, null, AndroidNamespace);

        // ── 権限（表の順）──
        foreach (var permission in set.Permissions)
        {
            writer.WriteStartElement("uses-permission");
            WriteAndroidAttribute(writer, "name", permission.Name);
            if (permission.MaxSdkVersion is int maxSdk)
            {
                WriteAndroidAttribute(writer, "maxSdkVersion", maxSdk.ToString(CultureInfo.InvariantCulture));
            }
            writer.WriteEndElement();
        }

        // ── <application>（ディープリンクか部品があるときだけ）──
        if (set.DeepLinks.Count > 0 || set.ApplicationElements.Count > 0)
        {
            writer.WriteStartElement("application");
            if (set.DeepLinks.Count > 0) WriteLaunchActivityFilters(writer, set.DeepLinks);
            foreach (var element in set.ApplicationElements) WriteElement(writer, element);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    });

    /// <summary>
    /// values（seed_system_bars_visible と、予測型の戻るを使うときだけ seed_predictive_back）を書く。
    /// 予測型の戻るを使わないときは頭のコメントも含めて W1-2 と同じバイト列（APK の工程の指紋を変えない）。
    /// </summary>
    /// <param name="set">このビルドのプラットフォーム機能。</param>
    /// <returns>UTF-8 のバイト列。</returns>
    public static byte[] WriteValues(AndroidPlatformFeatureSet set) => WriteDocument(writer =>
    {
        var comment = GeneratedNotice + NewLine + $"  {AndroidAppSettings.SystemBarsKey}: {set.SystemBars} ";
        if (set.PredictiveBack)
        {
            // 予測型の戻る（W2 の手直し P1-3）: true のときだけコメントの行を足す（false では何も足さない）
            comment += NewLine + $"  {AndroidAppSettings.PredictiveBackKey}: {AndroidPlatformFeatureCatalog.XmlTrue} ";
        }
        writer.WriteComment(comment);
        writer.WriteStartElement("resources");
        WriteBool(writer, SystemBarsVisibleResourceName, set.SystemBarsVisible);
        if (set.PredictiveBack) WriteBool(writer, PredictiveBackResourceName, true);
        writer.WriteEndElement();
    });

    /// <summary>&lt;bool name="…"&gt;true / false&lt;/bool&gt; を 1 つ書く。</summary>
    private static void WriteBool(XmlWriter writer, string name, bool value)
    {
        writer.WriteStartElement("bool");
        writer.WriteAttributeString("name", name);
        writer.WriteString(value ? AndroidPlatformFeatureCatalog.XmlTrue : AndroidPlatformFeatureCatalog.XmlFalse);
        writer.WriteEndElement();
    }

    /// <summary>MainActivity へのディープリンクの intent-filter（1 件 1 つ）を書く。</summary>
    private static void WriteLaunchActivityFilters(XmlWriter writer, IReadOnlyList<AndroidDeepLinkSetting> links)
    {
        writer.WriteStartElement("activity");
        WriteAndroidAttribute(writer, "name", AndroidRuntimeContract.LaunchActivityClassName);
        foreach (var link in links)
        {
            writer.WriteStartElement("intent-filter");
            if (link.AutoVerify) WriteAndroidAttribute(writer, "autoVerify", AndroidPlatformFeatureCatalog.XmlTrue);
            WriteNamedElement(writer, "action", ViewAction);
            WriteNamedElement(writer, "category", DefaultCategory);
            WriteNamedElement(writer, "category", BrowsableCategory);
            writer.WriteStartElement("data");
            WriteAndroidAttribute(writer, "scheme", AndroidDeepLinkRules.Trimmed(link.Scheme) ?? string.Empty);
            if (AndroidDeepLinkRules.Trimmed(link.Host) is { } host) WriteAndroidAttribute(writer, "host", host);
            if (AndroidDeepLinkRules.Trimmed(link.PathPrefix) is { } pathPrefix) WriteAndroidAttribute(writer, "pathPrefix", pathPrefix);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    /// <summary>機能の表の要素を書く（子も同じ形で）。</summary>
    private static void WriteElement(XmlWriter writer, AndroidManifestElement element)
    {
        writer.WriteStartElement(element.Tag);
        foreach (var (name, value) in element.Attributes)
        {
            if (name.StartsWith(AndroidPlatformFeatureCatalog.AndroidAttributePrefix, System.StringComparison.Ordinal))
            {
                WriteAndroidAttribute(writer, name[AndroidPlatformFeatureCatalog.AndroidAttributePrefix.Length..], value);
            }
            else
            {
                writer.WriteAttributeString(name, value);
            }
        }
        foreach (var child in element.Children) WriteElement(writer, child);
        writer.WriteEndElement();
    }

    /// <summary>android:name だけを持つ要素（action・category）を書く。</summary>
    private static void WriteNamedElement(XmlWriter writer, string tag, string name)
    {
        writer.WriteStartElement(tag);
        WriteAndroidAttribute(writer, "name", name);
        writer.WriteEndElement();
    }

    /// <summary>android: の属性を書く。</summary>
    private static void WriteAndroidAttribute(XmlWriter writer, string localName, string value) =>
        writer.WriteAttributeString(AndroidPrefix, localName, AndroidNamespace, value);

    /// <summary>XML の文書を 1 つ書く（UTF-8・BOM なし・LF・字下げ 4・宣言つき）。</summary>
    private static byte[] WriteDocument(System.Action<XmlWriter> body)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = IndentChars,
            NewLineChars = NewLine,
            NewLineHandling = NewLineHandling.Replace,
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            body(writer);
            writer.WriteEndDocument();
        }
        var bytes = stream.ToArray();
        // 最後に改行を 1 つ足す（エディタで開いたときの差分を穏やかにする。中身の意味は変わらない）
        return bytes.Concat(Encoding.UTF8.GetBytes(NewLine)).ToArray();
    }
}
