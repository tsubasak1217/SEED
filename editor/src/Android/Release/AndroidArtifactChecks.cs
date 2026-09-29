// ============================================================
//  AndroidArtifactChecks.cs — できた配布物から読み直した事実の判定（純粋な処理。段階D。docs/android.md §24）
//
//  【項目】（ビルドの前の判定の一覧に、同じ項目は上書きで入る。AndroidRequirementReport.Put）
//    artifact_identity … 中のアプリ ID・versionCode・versionName が指定どおりか
//    target_sdk        … 中の targetSdk が表の下限以上か（ビルドの前はエンジンの値。ここは実物の値）
//    debuggable        … debuggable でないか（Google Play は debuggable を受け付けない）
//    permissions       … release に要らない権限（INTERNET。デバッグ版のエディタとの IPC 用）が無いか
//    abi_64bit         … 中の .so の ABI（実物）
//    page_size_elf     … すべての .so の LOAD セグメントが 16 KB 以上で整列しているか
//    page_size_zip     … 非圧縮の .so が zip の中で 16 KB 境界にあるか（APK。圧縮した .so と AAB は対象外）
//    certificate       … デバッグ用の鍵で署名していないか・指定の鍵（アップロード鍵）の証明書か
//    inspection_tools  … 道具で調べられなかったこと（あれば不合格。確かめられていないものを合格にしない）
//    platform_features / play_policy/<権限> … features と配布物の権限の一致・配布物の権限の Google Play の方針（W1-2。
//                      AndroidPlatformFeatureChecks。期待する値に機能の表があるときだけ）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SEEDEditor.Android.Platform;
using SEEDEditor.Android.Project;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Release;

/// <summary>配布物の判定の材料（期待する値）。</summary>
/// <param name="Identity">アプリの識別情報（中の ID・版と比べる）。</param>
/// <param name="ExpectedCertificateSha256">指定の鍵の証明書の SHA-256（コロン無しの小文字。分からなければ null）。</param>
public sealed record AndroidArtifactExpectation(AndroidAppIdentity Identity, string? ExpectedCertificateSha256)
{
    /// <summary>このビルドのプラットフォーム機能（W1-2。配布物の権限と比べる）。</summary>
    public AndroidPlatformFeatureSet PlatformFeatures { get; init; } = AndroidPlatformFeatureSet.Empty;

    /// <summary>
    /// 機能の表（W1-2。どの権限がどの機能のものか。null なら機能の権限の判定をしない＝材料を集めていない呼び出し）。
    /// </summary>
    public AndroidPlatformFeatureCatalog? FeatureCatalog { get; init; }
}

/// <summary>配布物の判定。</summary>
public static class AndroidArtifactChecks
{
    /// <summary>
    /// 判定をすべて行う。
    /// </summary>
    /// <param name="requirements">要件の表。</param>
    /// <param name="facts">配布物から読み直した事実。</param>
    /// <param name="expectation">期待する値。</param>
    /// <returns>判定の一覧。</returns>
    public static IReadOnlyList<AndroidRequirementItem> Evaluate(
        PlayRequirements requirements, AndroidArtifactFacts facts, AndroidArtifactExpectation expectation)
    {
        var items = new List<AndroidRequirementItem>();
        var manifest = facts.Manifest;
        if (manifest is not null)
        {
            items.Add(Identity(manifest, expectation.Identity));
            items.Add(manifest.TargetSdk is int target
                ? AndroidRequirementChecks.TargetSdk(requirements, target)
                : new(AndroidRequirementIds.TargetSdk, "targetSdk", AndroidRequirementSeverity.Failure, "配布物のマニフェストに targetSdk がありません。"));
            items.Add(Debuggable(manifest));
            items.Add(Permissions(requirements, manifest));
            // プラットフォーム機能の権限（features と配布物の一致）と、配布物の権限の Google Play の方針（W1-2）
            if (expectation.FeatureCatalog is { } catalog)
            {
                items.AddRange(AndroidPlatformFeatureChecks.AfterBuild(requirements, expectation.PlatformFeatures, catalog, manifest.Permissions));
            }
        }
        items.Add(AndroidRequirementChecks.Abis(requirements, facts.NativeLibraries.Select(lib => lib.Abi).Distinct(StringComparer.Ordinal).ToList(), facts.Format));
        items.Add(PageSizeElf(requirements, facts.NativeLibraries));
        items.Add(PageSizeZip(facts));
        items.Add(Certificate(requirements, facts.Signer, expectation.ExpectedCertificateSha256, facts.Format));
        if (facts.ToolProblems.Count > 0)
        {
            items.Add(new(AndroidRequirementIds.Tools, "配布物の調べ", AndroidRequirementSeverity.Failure,
                "調べられなかったことがあります（確かめていないものは合格にしません）: " + string.Join(" / ", facts.ToolProblems)));
        }
        return items;
    }

    /// <summary>中のアプリ ID・版が指定どおりか。</summary>
    public static AndroidRequirementItem Identity(AaptBadging manifest, AndroidAppIdentity expected)
    {
        const string title = "配布物の中身（ID・版）";
        var problems = new List<string>();
        if (!string.Equals(manifest.PackageName, expected.ApplicationId, StringComparison.Ordinal))
        {
            problems.Add($"アプリ ID が {manifest.PackageName ?? "（無し）"}（指定は {expected.ApplicationId}）");
        }
        if (expected.VersionCode.Value is int code && manifest.VersionCode != code)
        {
            problems.Add($"versionCode が {manifest.VersionCode?.ToString(CultureInfo.InvariantCulture) ?? "（無し）"}（指定は {code}）");
        }
        if (expected.VersionName.Value is { } name && !string.Equals(manifest.VersionName, name, StringComparison.Ordinal))
        {
            problems.Add($"versionName が {manifest.VersionName ?? "（無し）"}（指定は {name}）");
        }
        return problems.Count == 0
            ? new(AndroidRequirementIds.ArtifactIdentity, title, AndroidRequirementSeverity.Pass,
                $"{manifest.PackageName}・versionCode {manifest.VersionCode}・versionName {manifest.VersionName}・minSdk {manifest.MinSdk}")
            : new(AndroidRequirementIds.ArtifactIdentity, title, AndroidRequirementSeverity.Failure,
                string.Join("・", problems) + "。古い配布物を見ていないか・Gradle へ値が渡ったかを確かめてください。");
    }

    /// <summary>debuggable でないか。</summary>
    public static AndroidRequirementItem Debuggable(AaptBadging manifest) => manifest.Debuggable
        ? new(AndroidRequirementIds.Debuggable, "debuggable", AndroidRequirementSeverity.Failure,
            "debuggable の配布物です。Google Play は受け付けません（ビルドの種類が配布用（release）かを確かめてください）。")
        : new(AndroidRequirementIds.Debuggable, "debuggable", AndroidRequirementSeverity.Pass, "debuggable ではありません");

    /// <summary>release に要らない権限が無いか。</summary>
    public static AndroidRequirementItem Permissions(PlayRequirements requirements, AaptBadging manifest)
    {
        var unexpected = manifest.Permissions.Where(p => requirements.ReleaseUnexpectedPermissions.Contains(p, StringComparer.Ordinal)).ToList();
        var listed = manifest.Permissions.Count == 0 ? "権限なし" : string.Join(", ", manifest.Permissions);
        return unexpected.Count > 0
            ? new(AndroidRequirementIds.Permissions, "権限", AndroidRequirementSeverity.Warning,
                $"{listed}。{string.Join(", ", unexpected)} は配布用には要りません（デバッグ版のエディタとの IPC 用。src/debug/ のマニフェストにだけ置く）。")
            : new(AndroidRequirementIds.Permissions, "権限", AndroidRequirementSeverity.Pass, listed);
    }

    /// <summary>.so の LOAD セグメントの整列。</summary>
    public static AndroidRequirementItem PageSizeElf(PlayRequirements requirements, IReadOnlyList<AndroidNativeLibraryFact> libraries)
    {
        const string title = "16 KB ページ（.so の LOAD）";
        if (libraries.Count == 0)
        {
            return new(AndroidRequirementIds.PageSizeElf, title, AndroidRequirementSeverity.Failure,
                "配布物に .so がありません（エンジンの libSEED.so が入っていません）。");
        }
        var unreadable = libraries.Where(lib => lib.Alignment is null).ToList();
        var misaligned = libraries.Where(lib => lib.Alignment is { } a && (a.LoadSegments == 0 || a.MinLoadAlignment < (ulong)requirements.PageSizeBytes)).ToList();
        if (unreadable.Count > 0 || misaligned.Count > 0)
        {
            var parts = misaligned.Select(lib => $"{lib.EntryName}（最小 0x{lib.Alignment!.MinLoadAlignment:X}）")
                .Concat(unreadable.Select(lib => $"{lib.EntryName}（読めない: {lib.Error}）"));
            return new(AndroidRequirementIds.PageSizeElf, title, AndroidRequirementSeverity.Failure,
                $"LOAD セグメントが 0x{requirements.PageSizeBytes:X} で整列していない .so があります: {string.Join(", ", parts)}。" +
                "NDK r28 以降でビルドし直すか、リンカに -Wl,-z,max-page-size=16384 を渡してください。");
        }
        var minimum = libraries.Min(lib => lib.Alignment!.MinLoadAlignment);
        return new(AndroidRequirementIds.PageSizeElf, title, AndroidRequirementSeverity.Pass,
            $"{libraries.Count} 個の .so の LOAD はすべて 0x{minimum:X} 以上で整列");
    }

    /// <summary>非圧縮の .so の zip の中の整列。</summary>
    public static AndroidRequirementItem PageSizeZip(AndroidArtifactFacts facts)
    {
        const string title = "16 KB ページ（zip の整列）";
        var compressed = facts.NativeLibraries.Count > 0 && facts.NativeLibraries.All(lib => lib.Compressed);
        if (facts.Format == AndroidPackageFormat.Aab)
        {
            return new(AndroidRequirementIds.PageSizeZip, title, AndroidRequirementSeverity.Pass,
                "AAB は Google Play が端末ごとの APK を作るときに整列する（.so は圧縮して入れる設定〈useLegacyPackaging〉なので、端末はインストール時にファイルへ展開する）");
        }
        return facts.ZipAligned switch
        {
            true => new(AndroidRequirementIds.PageSizeZip, title, AndroidRequirementSeverity.Pass,
                compressed ? "zipalign -c -P 16 で問題なし（.so は圧縮。インストール時に展開されるので zip の中の位置は問われない）"
                           : "zipalign -c -P 16 で問題なし（非圧縮の .so は 16 KB 境界）"),
            false => new(AndroidRequirementIds.PageSizeZip, title, AndroidRequirementSeverity.Failure,
                $"zipalign -c -P 16 が整列していない項目を見つけました: {facts.ZipAlignDetail ?? "（詳細なし）"}"),
            _ => new(AndroidRequirementIds.PageSizeZip, title, AndroidRequirementSeverity.Failure, "zipalign で確かめられませんでした。"),
        };
    }

    /// <summary>署名の証明書（デバッグ用の鍵でないこと・指定の鍵であること）。</summary>
    public static AndroidRequirementItem Certificate(
        PlayRequirements requirements, SignerCertificate? signer, string? expectedSha256, AndroidPackageFormat format)
    {
        const string title = "署名";
        if (signer is null || !signer.Verified)
        {
            return new(AndroidRequirementIds.Certificate, title, AndroidRequirementSeverity.Failure,
                format == AndroidPackageFormat.Aab ? "AAB に署名がありません（keytool -printcert -jarfile）。" : "APK の署名を確かめられません（apksigner verify）。");
        }
        if (!string.IsNullOrEmpty(requirements.DebugCertificateSubjectMarker) && signer.Subject is { } subject
            && subject.Contains(requirements.DebugCertificateSubjectMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new(AndroidRequirementIds.Certificate, title, AndroidRequirementSeverity.Failure,
                $"デバッグ用の鍵（{subject}）で署名されています。Google Play へは出せません。");
        }
        if (expectedSha256 is not null && !string.Equals(expectedSha256, signer.Sha256, StringComparison.Ordinal))
        {
            return new(AndroidRequirementIds.Certificate, title, AndroidRequirementSeverity.Failure,
                $"指定の鍵と違う証明書で署名されています（配布物 {signer.Sha256}・指定の鍵 {expectedSha256}）。");
        }
        var schemes = signer.Schemes.Count == 0 ? string.Empty : $"・方式 {string.Join(", ", signer.Schemes)}";
        return new(AndroidRequirementIds.Certificate, title, AndroidRequirementSeverity.Pass,
            $"{signer.Subject}・SHA-256 {signer.Sha256}{schemes}");
    }
}
