// ============================================================
//  AndroidRequirementReport.cs — Google Play の要件チェックの結果（1 項目ずつの判定と一覧。段階D。docs/android.md §24）
//
//  【判定】
//    Pass    … 合格
//    Info    … 知らせ（要件ではないが知っておくこと。例: APK で作ったので Google Play へは出せない形）
//    Warning … 注意（出せるが直すことを勧める。例: アイコン未設定・前回と同じ versionCode）
//    Failure … 不合格（Google Play が受け付けない・配布物として壊れている。例: debuggable・targetSdk 不足）
//  不合格があってもビルドは止めない（配布物は作る。手元で試すこともあるため）。SeedAndroid の build / check は
//  不合格があれば終了コード 6 で終わり、エディタのパッケージ化ウィンドウは赤で出す。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android.Release;

/// <summary>判定。</summary>
public enum AndroidRequirementSeverity
{
    /// <summary>合格。</summary>
    Pass,

    /// <summary>知らせ。</summary>
    Info,

    /// <summary>注意（出せるが直すことを勧める）。</summary>
    Warning,

    /// <summary>不合格（Google Play が受け付けない・配布物として壊れている）。</summary>
    Failure,
}

/// <summary>要件の項目の名前（判定の一覧・単体テスト・エディタの表示で使う決まった値）。</summary>
public static class AndroidRequirementIds
{
    /// <summary>要件の表そのもの（読めないとき）。</summary>
    public const string Table = "requirements_table";

    /// <summary>targetSdk。</summary>
    public const string TargetSdk = "target_sdk";

    /// <summary>versionCode の単調増加。</summary>
    public const string VersionCode = "version_code";

    /// <summary>64 bit の ABI。</summary>
    public const string Abi = "abi_64bit";

    /// <summary>アプリ ID の形式。</summary>
    public const string ApplicationId = "application_id";

    /// <summary>配布用の署名。</summary>
    public const string Signing = "signing";

    /// <summary>形式（AAB / APK）。</summary>
    public const string Format = "format";

    /// <summary>ランチャーのアイコン。</summary>
    public const string Icon = "icon";

    /// <summary>できた配布物の中身が指定どおりか（ID・版・targetSdk）。</summary>
    public const string ArtifactIdentity = "artifact_identity";

    /// <summary>debuggable でないこと。</summary>
    public const string Debuggable = "debuggable";

    /// <summary>release に要らない権限（INTERNET）が無いこと。</summary>
    public const string Permissions = "permissions";

    /// <summary>.so の LOAD セグメントの 16 KB 整列。</summary>
    public const string PageSizeElf = "page_size_elf";

    /// <summary>非圧縮の .so の zip 内の 16 KB 整列。</summary>
    public const string PageSizeZip = "page_size_zip";

    /// <summary>署名の証明書（デバッグ用の鍵でないこと）。</summary>
    public const string Certificate = "certificate";

    /// <summary>配布物を調べる道具（aapt2・zipalign・apksigner・keytool）の問題。</summary>
    public const string Tools = "inspection_tools";

    /// <summary>プラットフォーム機能（android.features）と権限が一致しているか（W1-2）。</summary>
    public const string PlatformFeatures = "platform_features";

    /// <summary>権限ごとの Google Play の方針の注意の項目の名前の頭（play_policy/android.permission.USE_EXACT_ALARM 等。W1-2）。</summary>
    public const string PlayPolicyPrefix = "play_policy/";

    /// <summary>権限の Google Play の方針の注意の項目の名前。</summary>
    /// <param name="permission">権限の名前。</param>
    /// <returns>項目の名前。</returns>
    public static string PlayPolicy(string permission) => PlayPolicyPrefix + permission;
}

/// <summary>1 項目の判定。</summary>
/// <param name="Id">項目の名前（<see cref="AndroidRequirementIds"/>）。</param>
/// <param name="Title">見出し（表示用）。</param>
/// <param name="Severity">判定。</param>
/// <param name="Detail">説明（直し方を含む）。</param>
public sealed record AndroidRequirementItem(string Id, string Title, AndroidRequirementSeverity Severity, string Detail)
{
    /// <summary>ログ・コンソール用の一行（[合格] 見出し: 説明）。</summary>
    /// <returns>一行。</returns>
    public string Describe() => $"[{AndroidRequirementReport.Label(Severity)}] {Title}: {Detail}";
}

/// <summary>要件チェックの結果の一覧。</summary>
public sealed class AndroidRequirementReport
{
    /// <summary>項目（チェックした順）。</summary>
    public List<AndroidRequirementItem> Items { get; } = new();

    /// <summary>不合格があるか。</summary>
    public bool HasFailures => Items.Any(item => item.Severity == AndroidRequirementSeverity.Failure);

    /// <summary>判定ごとの数。</summary>
    /// <param name="severity">判定。</param>
    /// <returns>数。</returns>
    public int Count(AndroidRequirementSeverity severity) => Items.Count(item => item.Severity == severity);

    /// <summary>要約の一行（合格 N・注意 N・不合格 N）。</summary>
    /// <returns>要約。</returns>
    public string Summary() =>
        $"合格 {Count(AndroidRequirementSeverity.Pass)}・知らせ {Count(AndroidRequirementSeverity.Info)}・" +
        $"注意 {Count(AndroidRequirementSeverity.Warning)}・不合格 {Count(AndroidRequirementSeverity.Failure)}";

    /// <summary>
    /// 同じ項目の判定があれば置き換え、無ければ足す（ビルドの後の判定で、ビルド前の同じ項目を上書きする）。
    /// </summary>
    /// <param name="item">判定。</param>
    public void Put(AndroidRequirementItem item)
    {
        var index = Items.FindIndex(existing => existing.Id == item.Id);
        if (index >= 0) Items[index] = item;
        else Items.Add(item);
    }

    /// <summary>判定の表示名。</summary>
    /// <param name="severity">判定。</param>
    /// <returns>表示名。</returns>
    public static string Label(AndroidRequirementSeverity severity) => severity switch
    {
        AndroidRequirementSeverity.Pass    => "合格",
        AndroidRequirementSeverity.Info    => "知らせ",
        AndroidRequirementSeverity.Warning => "注意",
        _                                  => "不合格",
    };
}
