// ============================================================
//  AndroidSigningConfig.cs — 配布用（release）の署名に使う鍵（決まった値）と、その決め方の材料
//
//  決め方は AndroidSigningResolver。Gradle へは Gradle/GradleInvocation.cs が
//  seed.signing.storeFile / keyAlias（-P か環境変数）と seed.signing.storePassword / keyPassword（必ず環境変数）にして渡す。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Signing;

/// <summary>決まった署名の鍵。</summary>
/// <param name="KeystorePath">キーストアのファイル（絶対パス。在ることは確かめ済み）。</param>
/// <param name="KeyAlias">キーの別名。</param>
/// <param name="Secrets">パスワード（ToString は伏せ字）。</param>
/// <param name="KeystoreOrigin">キーストアの指定の出どころ（ログ用）。</param>
/// <param name="Warnings">使えるが気を付けること（プロジェクトのフォルダの中にある等）。</param>
public sealed record AndroidSigningConfig(
    string KeystorePath,
    string KeyAlias,
    AndroidSigningSecrets Secrets,
    string KeystoreOrigin,
    IReadOnlyList<string> Warnings)
{
    /// <summary>ログ用の一行（パスワードは出さない）。</summary>
    /// <returns>説明。</returns>
    public string Describe() => $"キーストア {KeystorePath}（{KeystoreOrigin}）・別名 {KeyAlias}・パスワード {Secrets}";
}

/// <summary>署名の鍵を決める材料。</summary>
/// <param name="RequestKeystorePath">指定のキーストア（SeedAndroid の --keystore・エディタの画面。null なら設定ファイルから）。</param>
/// <param name="RequestKeyAlias">指定の別名（--key-alias。null なら設定ファイルから）。</param>
/// <param name="RequestSecrets">指定のパスワード（エディタの保護保存・SeedAndroid の対話の入力。null なら環境変数から）。</param>
/// <param name="ProjectSettings">プロジェクトの packaging_settings.json の android.signing（無ければ null）。</param>
/// <param name="ProjectRoot">プロジェクトのルート（設定ファイルの相対パスの基準。無ければ null）。</param>
/// <param name="AssetsRoot">アセットルート（ここの中のキーストアは使わない。無ければ null）。</param>
public sealed record AndroidSigningInputs(
    string? RequestKeystorePath,
    string? RequestKeyAlias,
    AndroidSigningSecrets? RequestSecrets,
    AndroidSigningSettings? ProjectSettings,
    string? ProjectRoot,
    string? AssetsRoot);
