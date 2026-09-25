// ============================================================
//  ConsoleSecretPrompt.cs — 配布用の署名のパスワードを対話で聞く（段階D。docs/android.md §24）
//
//  環境変数 SEED_ANDROID_KEYSTORE_PASSWORD が無く、コンソールで対話しているときだけ聞く（入力は画面に出さない）。
//  パスワードはコマンドラインの引数・設定 JSON では受け取らない（プロセスの一覧・履歴・ファイルに残るため）。
//  標準入力をリダイレクトしているとき（CI・パイプ）は聞かずに null（中核が「パスワードがありません」と説明する）。
// ============================================================

using System;
using System.Text;
using SEEDEditor.Android.Signing;

namespace SEEDEditor.Tools.SeedAndroid;

/// <summary>パスワードを対話で聞く。</summary>
public static class ConsoleSecretPrompt
{
    /// <summary>対話の入力から来たパスワードの出どころの表示。</summary>
    public const string PromptOrigin = "対話の入力";

    /// <summary>
    /// 環境変数にパスワードが無く、対話できるなら聞く（既存のキーストアを開くとき）。
    /// </summary>
    /// <param name="keystorePath">キーストア（表示用。分からなければ null）。</param>
    /// <returns>パスワード（環境変数にある・対話できない・空なら null）。</returns>
    public static AndroidSigningSecrets? AskIfNeeded(string? keystorePath)
    {
        if (AndroidSigningSecrets.FromEnvironment(Environment.GetEnvironmentVariable) is not null) return null;
        var password = Read($"キーストア{(keystorePath is null ? string.Empty : $"（{keystorePath}）")}のパスワード（表示しません。{AndroidSigningSecrets.KeystorePasswordVariable} でも渡せます）: ");
        return string.IsNullOrEmpty(password) ? null : new AndroidSigningSecrets(password, null, PromptOrigin);
    }

    /// <summary>
    /// 新しいキーストアのパスワードを 2 回聞いて確かめる（違えば null と理由）。
    /// </summary>
    /// <param name="error">聞けなかった・一致しなかった理由。</param>
    /// <returns>パスワード。</returns>
    public static AndroidSigningSecrets? AskNew(out string? error)
    {
        error = null;
        var first = Read($"新しいキーストアのパスワード（{AndroidKeystoreDefaults.MinPasswordLength} 文字以上。表示しません）: ");
        if (first is null)
        {
            error = $"対話でパスワードを聞けません（標準入力がリダイレクトされています）。環境変数 {AndroidSigningSecrets.KeystorePasswordVariable} で渡してください。";
            return null;
        }
        var second = Read("もう一度: ");
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            error = "2 回の入力が一致しません。";
            return null;
        }
        if (first.Length == 0)
        {
            error = "パスワードが空です。";
            return null;
        }
        return new AndroidSigningSecrets(first, null, PromptOrigin);
    }

    /// <summary>1 行を画面に出さずに読む（対話できなければ null）。</summary>
    /// <param name="prompt">案内。</param>
    /// <returns>入力（Enter まで）。</returns>
    private static string? Read(string prompt)
    {
        if (Console.IsInputRedirected) return null;
        Console.Error.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }
}
