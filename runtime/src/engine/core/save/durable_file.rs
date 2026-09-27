// ============================================================
//  save/durable_file.rs — セーブの本体を「消えない・壊れない」順序で置き換える
//
//  【役割】
//  出来上がった本文（バイト列）を、本体（save.json）へ原子的に・電源断にも耐える順序で書く。
//  本文を作る（codec.rs）・読む（recovery.rs）・いつ書くか（store.rs / batch.rs）は持たない。
//
//  【書き出しの順序】（W1-S。docs/app_platform_roadmap.md §2.7）
//    1. 一時ファイル（save.json.tmp）へ全部書き、`File::sync_all` でディスクまで届ける
//    2. 今の本体（save.json）を 1 世代前（save.json.bak）へ rename で回す（本体が無ければ飛ばす）
//    3. 一時ファイルを本体へ rename する
//    4. フォルダを sync する（Unix＝Android だけ。rename の結果＝フォルダの中身をディスクまで届ける）
//  既存の本体を削除してから rename する手順（W1-S より前）はやめた。`std::fs::rename` は Unix でも
//  Windows でも既存の宛先を置き換える（Rust 1.98 の標準ライブラリの文書と実装: Windows は
//  `MoveFileExW(MOVEFILE_REPLACE_EXISTING)`、だめなら `SetFileInformationByHandle` の
//  `FILE_RENAME_FLAG_REPLACE_IF_EXISTS`）ので、削除は要らず、削除と rename の間に落ちて本体が消える隙間も無い。
//
//  【落ちる位置ごとに残るもの】（どの行も recovery.rs の「本体 → .bak → 空」の順で読める）
//  | 落ちる位置                                  | 残るファイル                                   | 次の読み込み                          |
//  | 1 の途中（sync の前）                        | 本体＝前の世代・.bak＝2 つ前・半端な .tmp       | 本体（前の世代）。.tmp は読まない        |
//  | 1 と 2 の間                                  | 本体＝前の世代・.bak＝2 つ前・完全な .tmp       | 本体（前の世代）                        |
//  | 2 と 3 の間                                  | 本体は無い・.bak＝前の世代・完全な .tmp         | .bak（前の世代。RecoveredFrom=Backup）  |
//  | 3 と 4 の間                                  | 本体＝新しい世代・.bak＝前の世代                | 本体（新しい世代）※                    |
//  | 4 の後                                       | 同上（電源断でも残る）                          | 本体（新しい世代）                      |
//  ※ 電源断ならフォルダの変更がディスクへ届かず、上の行のどれかに見えることがある（どれでもその行のとおり読める）。
//  初めての書き出し（本体が無い）は 2 を飛ばすので、3 の前に落ちると空で始まる（保存が 1 度も済んでいない）。
//
//  【フォルダの sync】
//  Unix（Android）はフォルダを開いて `sync_all`（fsync）する。Windows にはフォルダの sync が無い
//  （フォルダを `File::open` で開けず、NTFS はメタデータをジャーナルで守る）ので何もしない。
// ============================================================

use std::fs::{self, File};
use std::io::{self, Write};
use std::path::Path;

use super::file_set::SaveFileSet;

/// 本体を置き換える前に、今の本体をどう扱うか。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PrimaryHandling {
    /// 今の本体を 1 世代前（.bak）へ回す（本体が正しい世代＝読めた・自分で書いたとき）。
    RotateToBackup,
    /// 今の本体には触れず、そのまま置き換える。
    /// 本体が壊れていて退避もできなかったとき用（壊れた本体を .bak へ回すと、正しい 1 世代前が消えるため）。
    ReplaceInPlace,
}

/// 書き出しの結果（書けたうえで、呼び出し元がログに残しておくこと）。
#[derive(Debug, Default)]
pub struct WriteReport {
    /// 今の本体を 1 世代前（.bak）へ回したか。
    pub rotated: bool,
    /// 書き出しは済んだが知らせておくこと（.bak へ回せなかった等）。
    pub warnings: Vec<String>,
}

/// 本文を本体へ書き出す（上の順序）。
///
/// # 引数
/// * `files`    - 本体と仲間のファイルのパス
/// * `bytes`    - 書く本文
/// * `handling` - 今の本体を .bak へ回すか
///
/// # 戻り値
/// 書けたら `Ok`（注意書き付き）。書けなければ `Err`。`Err` のとき本体は元のまま
/// （2 の後に 3 が失敗したときは、回した .bak を本体へ戻して「本体が無い」状態を残さない）。
pub fn replace_primary(files: &SaveFileSet, bytes: &[u8], handling: PrimaryHandling) -> io::Result<WriteReport> {
    let dir = files.dir();
    ensure_dir(&dir)?;

    // 1. 一時ファイルへ書き切ってディスクまで届ける（ここで失敗しても本体・.bak は無傷）
    write_synced(files.temp(), bytes)?;

    // 2. 今の本体を 1 世代前へ回す
    let mut report = WriteReport::default();
    if handling == PrimaryHandling::RotateToBackup {
        match fs::rename(files.primary(), files.backup()) {
            Ok(()) => report.rotated = true,
            // 本体がまだ無い（初めての書き出し・本体を退避した後）。回すものが無い。
            Err(e) if e.kind() == io::ErrorKind::NotFound => {}
            // 回せなくても書き出しは続ける（3 の rename は既存の本体を原子的に置き換える）。
            // 最新の状態を残すことを 1 世代前の更新より優先する（.bak は 1 つ古い世代のまま残る）。
            Err(e) => report.warnings.push(format!(
                "本体を 1 世代前（{}）へ回せませんでした（前の .bak を残したまま本体を置き換えます）: {e}",
                files.backup().display()
            )),
        }
    }

    // 3. 一時ファイルを本体にする
    if let Err(e) = fs::rename(files.temp(), files.primary()) {
        if report.rotated {
            // 本体が無い状態を残さない（読み込みは .bak からも戻れるが、本体があるほうが素直）。
            if let Err(undo) = fs::rename(files.backup(), files.primary()) {
                return Err(io::Error::new(
                    e.kind(),
                    format!("一時ファイルを本体にできず（{e}）、本体を .bak から戻すこともできませんでした（{undo}）。次の起動は .bak から読みます"),
                ));
            }
        }
        return Err(e);
    }

    // 4. rename の結果をディスクまで届ける（Unix のみ。Windows は何もしない）
    sync_dir(&dir)?;
    Ok(report)
}

/// フォルダの中身（名前の追加・rename）をディスクまで届ける（Unix＝Android）。
///
/// Windows にはフォルダの sync が無いので何もしない（ファイル先頭のコメント）。
#[cfg(unix)]
pub fn sync_dir(dir: &Path) -> io::Result<()> {
    File::open(dir)?.sync_all()
}

/// フォルダの中身（名前の追加・rename）をディスクまで届ける（Windows では何もしない）。
#[cfg(not(unix))]
pub fn sync_dir(_dir: &Path) -> io::Result<()> {
    Ok(())
}

/// ファイルを作り直して全部書き、ディスクまで届ける。失敗したら作りかけのファイルを消す（容量を返す）。
fn write_synced(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let result = File::create(path).and_then(|mut file| {
        file.write_all(bytes)?;
        file.sync_all()
    });
    if result.is_err() {
        // 半端な一時ファイルは読み込みでは使わないが、容量を食うので消しておく（消せなくても害は無い）
        let _ = fs::remove_file(path);
    }
    result
}

/// フォルダが無ければ作る。新しく作ったときは、その名前を親フォルダのディスクまで届ける（Unix のみ）。
fn ensure_dir(dir: &Path) -> io::Result<()> {
    if dir.is_dir() {
        return Ok(());
    }
    fs::create_dir_all(dir)?;
    match dir.parent() {
        Some(parent) if !parent.as_os_str().is_empty() => sync_dir(parent),
        _ => Ok(()),
    }
}

// ============================================================
//  ユニットテスト（ストアを通した壊れ方ごとの試験は store.rs）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    /// テストごとの一時フォルダ（終わったら消す）。
    struct TestDir(PathBuf);

    impl TestDir {
        /// 他のテスト・他のプロセスと重ならない名前で作る。
        fn new(tag: &str) -> Self {
            let nanos = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0);
            let dir = std::env::temp_dir().join(format!("seed_save_durable_{tag}_{}_{nanos}", std::process::id()));
            let _ = fs::remove_dir_all(&dir);
            Self(dir)
        }
    }

    impl Drop for TestDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    /// フォルダが無くても作って書け、一時ファイルは残らない。2 回目は前の本体が .bak へ回る。
    #[test]
    fn writes_and_rotates_previous_primary() {
        let dir = TestDir::new("rotate");
        let files = SaveFileSet::new(dir.0.join("nested").join("save.json"));

        let first = replace_primary(&files, b"{\"g\":1}", PrimaryHandling::RotateToBackup).unwrap();
        assert!(!first.rotated, "初回は回す本体が無い");
        assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{\"g\":1}");
        assert!(!files.temp().exists(), "一時ファイルが残った");
        assert!(!files.backup().exists());

        let second = replace_primary(&files, b"{\"g\":2}", PrimaryHandling::RotateToBackup).unwrap();
        assert!(second.rotated);
        assert!(second.warnings.is_empty(), "{:?}", second.warnings);
        assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{\"g\":2}");
        assert_eq!(fs::read_to_string(files.backup()).unwrap(), "{\"g\":1}");
    }

    /// ReplaceInPlace は本体を .bak へ回さない（壊れた本体で正しい 1 世代前を消さない）。
    #[test]
    fn replace_in_place_keeps_backup() {
        let dir = TestDir::new("in_place");
        let files = SaveFileSet::new(dir.0.join("save.json"));
        fs::create_dir_all(&dir.0).unwrap();
        fs::write(files.primary(), b"{ broken").unwrap();
        fs::write(files.backup(), b"{\"g\":1}").unwrap();

        let report = replace_primary(&files, b"{\"g\":2}", PrimaryHandling::ReplaceInPlace).unwrap();
        assert!(!report.rotated);
        assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{\"g\":2}");
        assert_eq!(fs::read_to_string(files.backup()).unwrap(), "{\"g\":1}", ".bak が壊れた本体で上書きされた");
    }

    /// 前の書き出しで残った一時ファイルがあっても、作り直して書ける。
    #[test]
    fn stale_temp_is_overwritten() {
        let dir = TestDir::new("stale_temp");
        let files = SaveFileSet::new(dir.0.join("save.json"));
        fs::create_dir_all(&dir.0).unwrap();
        fs::write(files.temp(), b"{ half written garbage that is longer than the new body").unwrap();

        replace_primary(&files, b"{}", PrimaryHandling::RotateToBackup).unwrap();
        assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{}");
        assert!(!files.temp().exists());
    }
}
