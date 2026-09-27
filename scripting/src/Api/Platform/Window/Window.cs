using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// アプリの画面（窓）の振る舞い（W1-4a は <see cref="SetShowWhenLocked"/> だけ。画面を点けたまま・システムバーは W1-6）。
/// Android ではメインプロセスが Activity を操作して答える（IPC なし）。デスクトップの模擬は受け付けてログに残すだけ。
/// </summary>
public static class Window
{
    /// <summary>画面のモジュール。</summary>
    private const string Module = "window";

    /// <summary>ロック画面の上に出す＋画面を点ける の切り替え。</summary>
    private const string MethodSetShowWhenLocked = "set_show_when_locked";

    /// <summary>set_show_when_locked の引数: 上げるか。</summary>
    private const string KeyOn = "on";

    /// <summary><see cref="Platform.LastError"/>: 操作する画面（Activity）が無い（Android）。</summary>
    public const string ErrorNoActivity = "no_activity";

    /// <summary>
    /// ロック画面の上に出す＋画面を点ける（Android の setShowWhenLocked・setTurnScreenOn）を切り替える。ロックは解除しない。
    /// 目覚ましの鳴動で起動したとき（<see cref="LaunchKind.Alarm"/>）はエンジンが上げているので、鳴動を片付けたら false で下ろす
    /// （下ろさないと、アプリを開いたまま電源ボタンを押してもロック画面が出ない）。すぐ返る（切り替えは UI スレッドで少し後）。
    /// </summary>
    /// <param name="on">上げるなら true。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool SetShowWhenLocked(bool on) =>
        Platform.TryInvoke(Module, MethodSetShowWhenLocked, BoolObject(KeyOn, on), out _);

    /// <summary>真偽 1 つだけのオブジェクトの JSON を作る（例 {"on":true}）。</summary>
    private static string BoolObject(string key, bool value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean(key, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
