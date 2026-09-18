// ============================================================
//  LoreMergeOriginStore.cs — 進行中のマージの出どころを作業コピーへ記録する
//
//  【なぜ必要なのか】
//  「自分の変更を残す」を Lore の mine / theirs のどちらへ対応付けるかは、
//  進行中のマージが sync 由来か branch merge 由来かで **入れ替わる**
//  （実機で確認。LoreConflictResolutionMap のコメント参照）。
//  取り違えると利用者の変更が黙って消える。
//
//  【なぜメモリではなくファイルなのか】
//  マージは未解決のまま残せる。エディタを閉じて開き直してから解決する
//  （あるいは別のプロジェクトを開いて戻る）ことは普通に起こる。
//  プロセス内の変数で覚えていると、その場合に **黙って逆の側を採る**。
//  マージの状態そのものが作業コピーに残っているのだから、
//  その出どころも作業コピーと一緒に残すのが正しい置き場所になる。
//
//  【なぜ `.lore/` の中なのか】
//  ・作業コピーと 1 対 1 で、コピーや移動でも一緒に付いて回る
//  ・Lore のメタデータ用フォルダなので、status の走査に出てこない
//    （出てくるなら `config.toml` が毎回「変更あり」になるはず）
//  ・作業コピーを消せば一緒に消える（後始末が要らない）
//
//  【記録が無いときは sync 扱い】
//  sync のマージは従来どおりの対応表で動いていて実績がある。
//  「印が無い ＝ 今までどおり」にしておけば、この仕組みが何らかの理由で
//  書けなかった場合でも、既存の（検証済みの）挙動へ落ちるだけで済む。
//  逆に branch merge を既定にすると、ありふれた sync の競合を壊す。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.IO;
using System.Linq;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// 進行中のマージの出どころを、作業コピーの `cache/editor/vcs/` へ記録する
/// （SEED のユーザー別状態。`.lore/` は Lore のメタデータ置き場なので使わない）。
/// </summary>
public sealed class LoreMergeOriginStore
{
    /// <summary>印のファイル名（`cache/editor/vcs/` 直下）。</summary>
    public const string FILE_NAME = "merge_origin";

    /// <summary>ブランチのマージが進行中であることを表す中身。</summary>
    private const string VALUE_BRANCH_MERGE = "branch-merge";

    /// <summary>印のファイルの絶対パス（作業コピーが分からないときは空文字）。</summary>
    private readonly string _filePath;

    /// <summary>作業コピーの `.lore/` の絶対パス（本当に Lore の作業コピーかを確かめるために使う）。</summary>
    private readonly string _loreDir;

    /// <summary>
    /// 作業コピーのルートを指定して生成する。
    /// </summary>
    /// <param name="workingCopyRoot">作業コピーのルート（プロジェクトのフォルダ）。</param>
    public LoreMergeOriginStore(string? workingCopyRoot)
    {
        if (string.IsNullOrWhiteSpace(workingCopyRoot))
        {
            _filePath = string.Empty;
            _loreDir  = string.Empty;
            return;
        }
        _loreDir = Path.Combine(workingCopyRoot, VersionControlSettings.LORE_METADATA_DIR_NAME);
        var dir = Path.Combine(
            new[] { workingCopyRoot }.Concat(VersionControlSettings.EDITOR_VCS_STATE_DIR_SEGMENTS).ToArray());
        _filePath = Path.Combine(dir, FILE_NAME);
    }

    /// <summary>
    /// 進行中のマージの出どころを読む。
    /// 印が無い・読めない場合は <see cref="MergeOrigin.Sync"/>（従来どおり）。
    /// </summary>
    public MergeOrigin Read()
    {
        if (_filePath.Length == 0) return MergeOrigin.Sync;

        try
        {
            if (!File.Exists(_filePath)) return MergeOrigin.Sync;

            var text = File.ReadAllText(_filePath).Trim();
            return string.Equals(text, VALUE_BRANCH_MERGE, StringComparison.OrdinalIgnoreCase)
                ? MergeOrigin.BranchMerge
                : MergeOrigin.Sync;
        }
        catch (Exception)
        {
            // 読めないなら「今までどおり」へ倒す（ここで例外を上げて
            // 競合の解決そのものを止める方が利用者にとって困る）。
            return MergeOrigin.Sync;
        }
    }

    /// <summary>
    /// 「ブランチのマージが進行中」と記録する。
    /// 書けなかった場合は黙って諦める（読み出しが Sync へ倒れるだけ）。
    /// </summary>
    public void MarkBranchMerge()
    {
        if (_filePath.Length == 0) return;

        try
        {
            // Lore の作業コピーでなければ書かない（見当違いの場所に cache/ を作らない）
            if (!Directory.Exists(_loreDir)) return;

            // 印のフォルダは初回に作る（cache/ はランタイムが作るとは限らない）
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, VALUE_BRANCH_MERGE);
        }
        catch (Exception)
        {
            // 印が残らないだけ。競合の解決は従来どおりの対応表で行われる。
        }
    }

    /// <summary>
    /// 印を消す（マージが片付いた、または sync のマージへ入れ替わった）。
    /// </summary>
    public void Clear()
    {
        if (_filePath.Length == 0) return;

        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        catch (Exception)
        {
            // 消せなくても、次のマージの開始時に上書きされる。
        }
    }
}
