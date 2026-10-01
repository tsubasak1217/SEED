// ============================================================
//  AndroidArtifactFacts.cs — できた配布物（APK / AAB）から読み直した事実（段階D）
//
//  ビルドの設定ではなく「できたもの」を確かめるための材料。読み方は AndroidArtifactInspector（aapt2・zipalign・apksigner・
//  keytool・zip の中の .so の ELF・zip の中の pak のエントリ表〈開発用のビルドの印〉）、判定は AndroidArtifactChecks（純粋な処理）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Release;

/// <summary>配布物の中の .so 1 つ。</summary>
/// <param name="EntryName">zip の中の名前（lib/arm64-v8a/libSEED.so・AAB は base/lib/…）。</param>
/// <param name="Abi">ABI（lib/ の下のフォルダ名）。</param>
/// <param name="Compressed">zip の中で圧縮されているか（useLegacyPackaging。端末はインストール時に展開する）。</param>
/// <param name="Alignment">ELF の LOAD セグメントの整列（読めなければ null）。</param>
/// <param name="Error">読めなかった理由（読めたら null）。</param>
public sealed record AndroidNativeLibraryFact(string EntryName, string Abi, bool Compressed, ElfLoadAlignment? Alignment, string? Error);

/// <summary>
/// 配布物の pak の開発用のビルドの印（配布前の安全弁。印の正典は PackageLayout.BuildManifestEntryPath・Packaging/Pak/PakBuildManifest.cs）。
/// </summary>
/// <param name="PakEntryName">zip の中の pak の名前（APK: assets/seed/assets.pak・AAB: base/assets/seed/assets.pak）。</param>
/// <param name="PakFound">配布物に pak があるか。</param>
/// <param name="MarkPresent">pak のエントリ表に開発用のビルドの印のエントリがあるか。</param>
/// <param name="Error">配布物・pak を読めなかった理由（読めたら null）。</param>
public sealed record AndroidDebugBuildMarkFact(string PakEntryName, bool PakFound, bool MarkPresent, string? Error);

/// <summary>配布物から読み直した事実。</summary>
public sealed record AndroidArtifactFacts
{
    /// <summary>配布物のパス。</summary>
    public required string ArtifactPath { get; init; }

    /// <summary>形式。</summary>
    public required AndroidPackageFormat Format { get; init; }

    /// <summary>マニフェストの要点（読めなければ null）。</summary>
    public AaptBadging? Manifest { get; init; }

    /// <summary>入っている .so（zip の中の lib/）。</summary>
    public IReadOnlyList<AndroidNativeLibraryFact> NativeLibraries { get; init; } = Array.Empty<AndroidNativeLibraryFact>();

    /// <summary>zipalign -c -P 16 の結果（APK だけ。調べなかったら null）。</summary>
    public bool? ZipAligned { get; init; }

    /// <summary>zipalign の出力の要点（整列していない項目。表示用）。</summary>
    public string? ZipAlignDetail { get; init; }

    /// <summary>署名（読めなければ null）。</summary>
    public SignerCertificate? Signer { get; init; }

    /// <summary>
    /// pak の開発用のビルドの印（調べなかったら null。判定では「調べていない」として不合格にする）。
    /// </summary>
    public AndroidDebugBuildMarkFact? DebugBuildMark { get; init; }

    /// <summary>道具を動かせなかった・出力を読めなかった理由（判定に「調べられなかった」として出す）。</summary>
    public IReadOnlyList<string> ToolProblems { get; init; } = Array.Empty<string>();
}
