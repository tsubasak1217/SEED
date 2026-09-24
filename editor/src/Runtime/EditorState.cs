// ============================================================
//  EditorState.cs — エディタの実行状態（PC のランタイム。RuntimeManager の状態遷移の値）
//
//  【なぜ独立ファイルか】
//  もとは RuntimeManager.cs の先頭にあった。段階C-2 でプレイバーの判断（AndroidRun/PlayBarPolicy.cs）を
//  WPF 非依存のクラスに切り出し、単体テスト（editor/tests/AndroidRunUiTests）から使えるようにするため、
//  値の定義だけをこのファイルへ移した（RuntimeManager.cs は WPF・プロセス・IPC に依存するためリンクできない）。
//
//  状態遷移そのものは RuntimeManager（クラスコメントの図）が正典。
//
//  WPF に依存しない。
// ============================================================

namespace SEEDEditor.Runtime;

/// <summary>エディタの実行状態（PC のランタイム）。</summary>
public enum EditorState
{
    /// <summary>ランタイム未起動・再起動待ち（アセット不在で見送っている・終了した直後など）。</summary>
    Idle,

    /// <summary>ランタイム（SEED.exe）をビルド中。</summary>
    Building,

    /// <summary>
    /// Play ボタン押下後、Play ランタイムの起動シーケンス（プロセス起動〜ウィンドウ／パイプ準備）が進行中の過渡状態。
    /// この間に Stop を押せるようにする（起動キャンセル用）とともに、Play ボタンの再入をブロックする。
    /// </summary>
    Launching,

    /// <summary>編集中（Edit ランタイムが動いている）。</summary>
    Edit,

    /// <summary>PC で実行中（Play）。</summary>
    Play,

    /// <summary>PC の実行を一時停止中。</summary>
    Pause,
}
