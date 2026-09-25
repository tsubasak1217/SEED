// ============================================================
//  AndroidIpcToken.cs — 端末のアプリとの IPC の接続トークン（起動ごとの使い捨て。段階D-1 の追加）
//
//  【流れ】
//    起動の工程（Steps/LaunchStep）… 起動のたびに新しいトークンを使い（エディタは実行ごとに作って指定に入れる。
//                                   無ければ中核が作る）、am start の extra seed.ipc_token で端末へ渡す。
//                                   同じトークンをプロジェクトの cache/android/run_state.json（ipc_launches）へ記録する
//    端末のランタイム              … 接続の最初の行 HELLO:<トークン> が一致した接続だけを受け付ける
//                                   （runtime/src/engine/core/app_base/ipc_transport/auth.rs）
//    エディタ／SeedAndroid         … つないだら最初に HELLO:<トークン> を送る（Ipc/AndroidIpcConnector）。
//                                   SeedAndroid の pause / resume / screenshot は run_state.json の記録を使う
//  同じ端末の他のアプリは 127.0.0.1 のポートへつなげても、トークンを知らないので命令を送れない。
//  トークンは Output・logcat に出さない（am start の表示は伏せる。ランタイムも伏せて出す）。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;
using System.Linq;
using System.Security.Cryptography;

namespace SEEDEditor.Android.Ipc;

/// <summary>端末のアプリとの IPC の接続トークン。</summary>
public static class AndroidIpcToken
{
    /// <summary>作るトークンの乱数のバイト数（128 ビット。16 進で 32 文字）。</summary>
    public const int RandomByteCount = 16;

    /// <summary>
    /// 受け付けるトークンの最短の長さ（runtime/src/engine/platform/launch_options.rs の MIN_IPC_TOKEN_LENGTH と一致させる）。
    /// </summary>
    public const int MinLength = 16;

    /// <summary>
    /// 受け付けるトークンの最長の長さ（runtime/src/engine/platform/launch_options.rs の MAX_IPC_TOKEN_LENGTH と一致させる）。
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>ログ・Output に出すときに伏せた値の代わりに書く文字列（ランタイムの MASKED_VALUE と同じ）。</summary>
    public const string MaskedValue = "***";

    /// <summary>新しいトークンを作る（暗号用の乱数・16 進の小文字 32 文字）。</summary>
    /// <returns>トークン。</returns>
    public static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(RandomByteCount)).ToLowerInvariant();

    /// <summary>
    /// トークンの書式を確かめる（純粋な処理。16〜128 文字の英数字・_・-。ランタイムの parse_ipc_token と同じ規則）。
    /// </summary>
    /// <param name="token">トークン。</param>
    /// <returns>使えるなら true。</returns>
    public static bool IsValid(string? token) =>
        token is { Length: >= MinLength and <= MaxLength }
        && token.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or '-');

    /// <summary>
    /// 指定のトークンを確かめる（null は「中核が作る」なので正しい）。
    /// </summary>
    /// <param name="token">指定のトークン。</param>
    /// <returns>誤りの説明（正しければ null。値そのものは含めない）。</returns>
    public static string? Validate(string? token) =>
        token is null || IsValid(token)
            ? null
            : $"IPC の接続トークンの書式が違います（{MinLength}〜{MaxLength} 文字の英数字・_・-）。";
}
