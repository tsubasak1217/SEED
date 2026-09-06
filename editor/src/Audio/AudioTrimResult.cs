namespace SEEDEditor.Audio;

/// <summary>
/// 無音カット処理の結果種別。UI 側はこれを見てメッセージの出し分け（成功／注意／失敗）を行う。
/// </summary>
public enum AudioTrimStatus
{
    /// <summary>カットして保存した。</summary>
    Trimmed,

    /// <summary>カット対象の無音が無かったため、ファイルは変更していない。</summary>
    NothingToTrim,

    /// <summary>全体がしきい値以下（＝実質無音）だったため、ファイルは変更していない。</summary>
    AllSilent,

    /// <summary>拡張子が未対応（ogg/flac 等）のため何もしていない。</summary>
    UnsupportedFormat,

    /// <summary>ファイルが他プロセス（実行中のランタイム等）に掴まれていて置き換えられなかった。</summary>
    FileLocked,

    /// <summary>その他の失敗（デコード失敗・エンコード失敗など）。</summary>
    Failed,
}

/// <summary>
/// 無音カット処理の結果。処理側は例外を投げずにこの構造体で成否を返し、
/// UI 側は <see cref="Status"/> と <see cref="Message"/> をそのまま提示できるようにする。
/// </summary>
public sealed class AudioTrimResult
{
    /// <summary>結果種別。</summary>
    public AudioTrimStatus Status { get; init; }

    /// <summary>ユーザーへ提示する説明文（日本語）。</summary>
    public string Message { get; init; } = "";

    /// <summary>先頭からカットした長さ（ミリ秒）。</summary>
    public double RemovedLeadingMs { get; init; }

    /// <summary>末尾からカットした長さ（ミリ秒）。末尾カットが無効なら 0。</summary>
    public double RemovedTrailingMs { get; init; }

    /// <summary>出力後の長さ（ミリ秒）。</summary>
    public double OutputDurationMs { get; init; }

    /// <summary>出力先の絶対パス。書き込みを行わなかった場合は null。</summary>
    public string? OutputPath { get; init; }

    /// <summary>退避した元ファイル（.bak）の絶対パス。上書き保存時のみ設定される。</summary>
    public string? BackupPath { get; init; }

    /// <summary>処理が成功して実際にファイルを書き出したかどうか。</summary>
    public bool Saved => Status == AudioTrimStatus.Trimmed;
}
