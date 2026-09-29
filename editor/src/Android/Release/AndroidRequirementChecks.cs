// ============================================================
//  AndroidRequirementChecks.cs — ビルドの前に分かる Google Play の要件の判定（純粋な処理。段階D。docs/android.md §24）
//
//  【項目】（表は runtime/android/play_requirements.json → PlayRequirements）
//    target_sdk     … エンジンの targetSdk（AndroidRuntimeContract.TargetApiLevel）が表の下限以上か
//    version_code   … 前回の同じアプリ・形式の配布用ビルド（AndroidReleaseHistory）より大きいか（小さい = 不合格・同じ = 注意）
//    abi_64bit      … 64 bit の ABI だけで、実機向け（arm64-v8a）が入っているか
//    application_id … 予約された前半（com.example.）でないか・エンジンの既定の ID のままでないか
//    signing        … 配布用の鍵で署名するか（鍵が決まらなければ不合格。ビルドはその前に止まる）
//    format         … AAB か（Google Play の新しいアプリは AAB 必須。APK は知らせ）
//    icon           … アイコンを設定したか（未設定は注意。システムの既定のアイコンになる）
//    platform_features / play_policy/<権限> … プラットフォーム機能の権限と Google Play の方針の注意（W1-2。AndroidPlatformFeatureChecks）
//  ビルドの後は、できた配布物から読み直した値で同じ項目の一部を確かめ直す（AndroidArtifactChecks。一覧では上書き）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Android.Platform;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Signing;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Release;

/// <summary>ビルドの前の判定の材料。</summary>
public sealed record AndroidPreBuildFacts
{
    /// <summary>形式。</summary>
    public required AndroidPackageFormat Format { get; init; }

    /// <summary>エンジンの targetSdk。</summary>
    public required int TargetApiLevel { get; init; }

    /// <summary>詰める ABI。</summary>
    public required IReadOnlyList<AndroidAbi> Abis { get; init; }

    /// <summary>アプリの識別情報。</summary>
    public required AndroidAppIdentity Identity { get; init; }

    /// <summary>前回の同じアプリ・形式の配布用ビルド（無ければ null）。</summary>
    public AndroidReleaseRecord? PreviousRelease { get; init; }

    /// <summary>決まった署名（決まらなければ null）。</summary>
    public AndroidSigningConfig? Signing { get; init; }

    /// <summary>署名が決まらなかった理由（決まったら null）。</summary>
    public string? SigningProblem { get; init; }

    /// <summary>アイコンを設定しているか。</summary>
    public bool LauncherIcon { get; init; }

    /// <summary>
    /// このビルドのプラットフォーム機能（W1-2。null なら機能の項目を出さない＝判定の材料を集めていない呼び出し）。
    /// </summary>
    public AndroidPlatformFeatureSet? PlatformFeatures { get; init; }
}

/// <summary>ビルドの前の要件の判定。</summary>
public static class AndroidRequirementChecks
{
    /// <summary>
    /// 表が読めないときの判定（これ 1 件だけにする）。
    /// </summary>
    /// <param name="error">読めなかった理由。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem TableMissing(string error) =>
        new(AndroidRequirementIds.Table, "要件の表", AndroidRequirementSeverity.Failure,
            $"{error} runtime/android/play_requirements.json を直すまで Google Play の要件を判定できません。");

    /// <summary>
    /// ビルドの前の判定をすべて行う。
    /// </summary>
    /// <param name="requirements">要件の表。</param>
    /// <param name="facts">材料。</param>
    /// <returns>判定の一覧（表の順）。</returns>
    public static IReadOnlyList<AndroidRequirementItem> Evaluate(PlayRequirements requirements, AndroidPreBuildFacts facts)
    {
        var items = new List<AndroidRequirementItem>
        {
            TargetSdk(requirements, facts.TargetApiLevel),
            VersionCode(facts),
            Abis(requirements, facts.Abis.Select(abi => abi.Name).ToList(), facts.Format),
            ApplicationId(requirements, facts.Identity.ApplicationId),
            Signing(facts),
            Format(facts.Format),
            Icon(facts.LauncherIcon),
        };
        // プラットフォーム機能の権限と、その権限の Google Play の方針（W1-2。AndroidPlatformFeatureChecks）
        if (facts.PlatformFeatures is { } features) items.AddRange(AndroidPlatformFeatureChecks.BeforeBuild(requirements, features));
        return items;
    }

    /// <summary>
    /// targetSdk の判定（ビルドの後はできた配布物の値でも同じ関数を使う）。
    /// </summary>
    /// <param name="requirements">要件の表。</param>
    /// <param name="targetSdk">targetSdk。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem TargetSdk(PlayRequirements requirements, int targetSdk)
    {
        var rule = requirements.TargetSdk;
        var since = rule.RequiredSince is null ? string.Empty : $"{rule.RequiredSince} 以降の";
        return targetSdk >= rule.MinApiLevel
            ? new(AndroidRequirementIds.TargetSdk, "targetSdk", AndroidRequirementSeverity.Pass,
                $"{targetSdk}（Google Play: {since}新規・更新は {rule.MinApiLevel} 以上）")
            : new(AndroidRequirementIds.TargetSdk, "targetSdk", AndroidRequirementSeverity.Failure,
                $"{targetSdk} は Google Play の要件（{since}新規・更新は {rule.MinApiLevel} 以上" +
                (rule.ExtensionUntil is null ? string.Empty : $"。延長を申請すれば {rule.ExtensionUntil} まで") +
                "）を満たしません。エンジンの runtime/android/app/build.gradle.kts の seedTargetSdk を上げる必要があります。");
    }

    /// <summary>versionCode の単調増加の判定。</summary>
    /// <param name="facts">材料。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem VersionCode(AndroidPreBuildFacts facts)
    {
        const string title = "versionCode";
        var current = facts.Identity.VersionCode.Value;
        if (current is not int code)
        {
            return new(AndroidRequirementIds.VersionCode, title, AndroidRequirementSeverity.Warning,
                "versionCode が決まっていません（プロジェクトの無いビルド。Gradle の既定値 1）。プロジェクト設定の Android アプリ情報で設定してください。");
        }
        var defaultNote = facts.Identity.VersionCode.Source == AndroidIdentitySource.FixedDefault
            ? "（プロジェクト設定で未設定のため既定値）" : string.Empty;
        var previous = facts.PreviousRelease;
        if (previous is null)
        {
            return new(AndroidRequirementIds.VersionCode, title, AndroidRequirementSeverity.Info,
                $"{code}{defaultNote}。このプロジェクトの配布用ビルドの記録がありません。Google Play に出したことがあれば、" +
                "Play Console の最後の versionCode より大きいことを確かめてください。");
        }
        var previousText = $"前回の配布用ビルド {previous.VersionCode}（{previous.VersionName ?? "?"}・{previous.BuiltAt:yyyy-MM-dd HH:mm}）";
        if (code > previous.VersionCode)
        {
            return new(AndroidRequirementIds.VersionCode, title, AndroidRequirementSeverity.Pass, $"{code}{defaultNote}（{previousText} より大きい）");
        }
        if (code == previous.VersionCode)
        {
            return new(AndroidRequirementIds.VersionCode, title, AndroidRequirementSeverity.Warning,
                $"{code} は{previousText}と同じです。Google Play に出した後なら、プロジェクト設定の Android アプリ情報の「バージョン番号」を上げてください" +
                "（同じ versionCode は受け付けられません）。");
        }
        return new(AndroidRequirementIds.VersionCode, title, AndroidRequirementSeverity.Failure,
            $"{code} が{previousText}より小さいです。Google Play と端末は小さい versionCode の更新を受け付けません。" +
            "プロジェクト設定の Android アプリ情報の「バージョン番号」を上げてください。");
    }

    /// <summary>
    /// 64 bit の ABI の判定（ビルドの後はできた配布物の ABI でも同じ関数を使う）。
    /// </summary>
    /// <param name="requirements">要件の表。</param>
    /// <param name="abis">入っている ABI の名前。</param>
    /// <param name="format">形式。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem Abis(PlayRequirements requirements, IReadOnlyList<string> abis, AndroidPackageFormat format)
    {
        const string title = "64 bit の ABI";
        var listed = abis.Count == 0 ? "（.so なし）" : string.Join(", ", abis);
        var not64 = abis.Where(name => AndroidAbis.Find(name) is null).ToList();
        if (requirements.Only64BitAbis && not64.Count > 0)
        {
            return new(AndroidRequirementIds.Abi, title, AndroidRequirementSeverity.Failure,
                $"64 bit でない（SEED が作らない）ABI が入っています: {string.Join(", ", not64)}。");
        }
        var missing = requirements.RequiredAbis.Where(required => !abis.Contains(required, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            return new(AndroidRequirementIds.Abi, title, AndroidRequirementSeverity.Warning,
                $"{listed}。実機向けの {string.Join(", ", missing)} が入っていません（ほとんどの端末で動きません）。ABI に arm64-v8a を選んでください。");
        }
        if (abis.Count > 1)
        {
            return new(AndroidRequirementIds.Abi, title,
                format == AndroidPackageFormat.Aab ? AndroidRequirementSeverity.Warning : AndroidRequirementSeverity.Info,
                $"{listed}。同梱 .NET の BCL（assets。1 ABI 約 65 MB）は ABI で分けられないので、どの端末にも両方の分が配られます" +
                "（配布は arm64-v8a だけを勧めます。docs/android.md §17.3）。");
        }
        return new(AndroidRequirementIds.Abi, title, AndroidRequirementSeverity.Pass, $"{listed}（64 bit だけ）");
    }

    /// <summary>アプリ ID の判定。</summary>
    /// <param name="requirements">要件の表。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem ApplicationId(PlayRequirements requirements, string applicationId)
    {
        const string title = "アプリ ID";
        var reserved = requirements.ReservedApplicationIdPrefixes
            .FirstOrDefault(prefix => applicationId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (reserved is not null)
        {
            return new(AndroidRequirementIds.ApplicationId, title, AndroidRequirementSeverity.Failure,
                $"{applicationId} は Google Play が受け付けない前半（{reserved}）で始まっています。プロジェクト設定の Android アプリ情報で自分の ID にしてください" +
                "（Google Play に出した後は ID を変えられません）。");
        }
        if (requirements.EngineDefaultApplicationIds.Contains(applicationId, StringComparer.Ordinal))
        {
            return new(AndroidRequirementIds.ApplicationId, title, AndroidRequirementSeverity.Warning,
                $"{applicationId} はエンジンの既定の ID です。配布するゲームはプロジェクト設定の Android アプリ情報で自分の ID にしてください" +
                "（Google Play に出した後は ID を変えられません）。");
        }
        return AndroidAppIdentityResolver.IsValidApplicationId(applicationId)
            ? new(AndroidRequirementIds.ApplicationId, title, AndroidRequirementSeverity.Pass, applicationId)
            : new(AndroidRequirementIds.ApplicationId, title, AndroidRequirementSeverity.Failure,
                $"{applicationId} はアプリ ID の形式（2 区切り以上・各区切りは英字で始まり英数字と _）に合いません。");
    }

    /// <summary>配布用の署名の判定（ビルドの前。鍵が決まったか）。</summary>
    /// <param name="facts">材料。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem Signing(AndroidPreBuildFacts facts) => facts.Signing is { } signing
        ? new(AndroidRequirementIds.Signing, "配布用の署名", AndroidRequirementSeverity.Pass,
            $"{signing.KeystorePath}（別名 {signing.KeyAlias}）。Google Play の Play App Signing では、これがアップロード鍵になります。")
        : new(AndroidRequirementIds.Signing, "配布用の署名", AndroidRequirementSeverity.Failure,
            facts.SigningProblem ?? AndroidSigningResolver.MissingKeystoreMessage);

    /// <summary>形式の判定。</summary>
    /// <param name="format">形式。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem Format(AndroidPackageFormat format) => format == AndroidPackageFormat.Aab
        ? new(AndroidRequirementIds.Format, "形式", AndroidRequirementSeverity.Pass, "AAB（Google Play へ出す形）")
        : new(AndroidRequirementIds.Format, "形式", AndroidRequirementSeverity.Info,
            "APK（端末へ直接入れる形）。Google Play の新しいアプリは AAB でなければ出せません（形式を AAB にしてください）。");

    /// <summary>アイコンの判定。</summary>
    /// <param name="launcherIcon">アイコンを設定しているか。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem Icon(bool launcherIcon) => launcherIcon
        ? new(AndroidRequirementIds.Icon, "アイコン", AndroidRequirementSeverity.Pass, "プロジェクト設定の android.icon から生成")
        : new(AndroidRequirementIds.Icon, "アイコン", AndroidRequirementSeverity.Warning,
            "設定されていません（ランチャーにはシステムの既定のアイコンが出ます）。プロジェクト設定の Android アプリ情報の「アイコン」に PNG を指定してください" +
            "（Google Play のストアの掲載用の 512x512 のアイコンは Play Console で別に登録します）。");
}
