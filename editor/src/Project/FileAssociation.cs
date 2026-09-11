// ============================================================
//  FileAssociation.cs — .seedproj をこのエディタに関連付ける（HKCU）
//
//  【役割】
//  エクスプローラーで .seedproj をダブルクリックしたら SEEDEditor.exe が
//  そのプロジェクトを開くようにする。書き込み先は HKEY_CURRENT_USER 配下だけなので
//  管理者権限は要らない（= スタート画面のボタンから実行できる）。
//
//  【値の組み立てとの分離】
//  「どのキーへ何を書くか」は FileAssociationValues が純関数として決める。
//  このクラスはその結果をレジストリへ反映し、現在の状態を読み取るだけに徹する。
// ============================================================

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SEEDEditor.Project;

/// <summary>
/// .seedproj のファイル関連付けの登録・解除・状態確認。
/// </summary>
public static class FileAssociation
{
    // ── シェルへの変更通知（P/Invoke）────────────────────────

    /// <summary>関連付けが変わったことをシェルへ知らせる。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    /// <summary>SHCNE_ASSOCCHANGED — ファイルの関連付けが変わった。</summary>
    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    /// <summary>SHCNF_IDLIST — item1/item2 は PIDL（今回はどちらも未使用なので 0）。</summary>
    private const uint SHCNF_IDLIST = 0x0000;

    // ── 現在の exe パス ─────────────────────────────────────

    /// <summary>
    /// 実行中の SEEDEditor.exe の絶対パス。
    ///
    /// <see cref="Environment.ProcessPath"/> は AppHost（.exe）のパスを返すので、
    /// これをそのまま関連付けに書けばよい（Assembly.Location は .dll を指すので使わない）。
    /// </summary>
    public static string CurrentExePath
        => Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

    // ── 判定 ────────────────────────────────────────────────

    /// <summary>
    /// 指定 exe で .seedproj が関連付け済みかどうかを調べる。
    /// </summary>
    /// <param name="editorExePath">確認する exe の絶対パス。</param>
    /// <returns>拡張子・ProgID・open コマンドがすべて期待値なら true。</returns>
    public static bool IsRegistered(string editorExePath)
    {
        if (string.IsNullOrWhiteSpace(editorExePath)) return false;

        try
        {
            var values = FileAssociationValues.Build(editorExePath);

            // 拡張子 → ProgID の対応
            using var extKey = Registry.CurrentUser.OpenSubKey(values.ExtensionKeyPath);
            if (extKey?.GetValue(null) as string != values.ProgId) return false;

            // ProgID → 起動コマンド
            using var cmdKey = Registry.CurrentUser.OpenSubKey(values.OpenCommandKeyPath);
            return cmdKey?.GetValue(null) as string == values.OpenCommandValue;
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[関連付け] 状態確認に失敗しました: {ex.Message}");
            return false;
        }
    }

    // ── 登録・解除 ──────────────────────────────────────────

    /// <summary>
    /// .seedproj を指定 exe へ関連付ける（HKCU）。
    /// </summary>
    /// <param name="editorExePath">関連付ける exe の絶対パス。</param>
    /// <param name="error">失敗理由（日本語）。成功時は null。</param>
    /// <returns>成功したら true。</returns>
    public static bool Register(string editorExePath, out string? error)
    {
        try
        {
            var values = FileAssociationValues.Build(editorExePath);

            // 1) 拡張子キー: 既定値に ProgID を入れる
            using (var extKey = Registry.CurrentUser.CreateSubKey(values.ExtensionKeyPath))
                extKey.SetValue(null, values.ProgId, RegistryValueKind.String);

            // 2) ProgID キー: 既定値に「種類」の説明を入れる
            using (var progKey = Registry.CurrentUser.CreateSubKey(values.ProgIdKeyPath))
                progKey.SetValue(null, values.Description, RegistryValueKind.String);

            // 3) アイコン（exe のアプリケーションアイコンをそのまま使う）
            using (var iconKey = Registry.CurrentUser.CreateSubKey(values.DefaultIconKeyPath))
                iconKey.SetValue(null, values.DefaultIconValue, RegistryValueKind.String);

            // 4) 起動コマンド
            using (var cmdKey = Registry.CurrentUser.CreateSubKey(values.OpenCommandKeyPath))
                cmdKey.SetValue(null, values.OpenCommandValue, RegistryValueKind.String);

            NotifyShell();
            EditorLog.Write($"[関連付け] .seedproj を登録しました: {editorExePath}");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"関連付けの登録に失敗しました: {ex.Message}";
            EditorLog.Write("[関連付け] " + error);
            return false;
        }
    }

    /// <summary>
    /// .seedproj の関連付けを解除する（HKCU に書いたキーだけを消す）。
    /// </summary>
    /// <param name="error">失敗理由（日本語）。成功時は null。</param>
    /// <returns>成功したら true。</returns>
    public static bool Unregister(out string? error)
    {
        try
        {
            // exe パスは値の生成にしか使わないので、解除では現在の exe で組み立てる
            // （キーのパスは exe に依らないため、これで正しいキーが得られる）。
            var values = FileAssociationValues.Build(
                string.IsNullOrWhiteSpace(CurrentExePath) ? "SEEDEditor.exe" : CurrentExePath);

            Registry.CurrentUser.DeleteSubKeyTree(values.ProgIdKeyPath,    throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(values.ExtensionKeyPath, throwOnMissingSubKey: false);

            NotifyShell();
            EditorLog.Write("[関連付け] .seedproj の関連付けを解除しました");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"関連付けの解除に失敗しました: {ex.Message}";
            EditorLog.Write("[関連付け] " + error);
            return false;
        }
    }

    /// <summary>
    /// 関連付けが変わったことをシェルへ通知する。
    /// これを呼ばないとエクスプローラーが古い関連付けをキャッシュしたままになる。
    /// </summary>
    private static void NotifyShell()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nint.Zero, nint.Zero); }
        catch { /* 通知できなくても登録自体は済んでいる */ }
    }
}
