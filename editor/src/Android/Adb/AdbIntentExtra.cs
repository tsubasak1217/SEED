// ============================================================
//  AdbIntentExtra.cs — am start に付ける文字列の extra（--es キー 値）1 つ
//
//  起動オプション（起動するシーン等）を端末のアプリへ渡すのに使う（段階C-3）。キーの名前は端末側と一致させる
//  （Common/AndroidRuntimeContract.cs の LaunchOption*。MainActivity が「seed.」で始まる文字列の extra を
//  JSON にまとめてネイティブへ渡す）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

namespace SEEDEditor.Android.Adb;

/// <summary>am start の文字列の extra。</summary>
/// <param name="Key">キー（英数字・.・_ だけ。端末のシェルへそのまま渡す）。</param>
/// <param name="Value">値（任意の文字列。端末のシェルへ渡すときに単一引用符で囲む）。</param>
public sealed record AdbIntentExtra(string Key, string Value);
