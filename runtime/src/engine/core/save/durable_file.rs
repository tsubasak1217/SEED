// ============================================================
//  save/durable_file.rs — セーブの本体を「消えない・壊れない」順序で置き換える
//
//  【役割】
//  出来上がった本文（バイト列）を、本体（save.json）へ原子的に・電源断にも耐える順序で書く。
//  本文を作る（codec.rs）・読む（recovery.rs）・いつ書くか（store.rs / batch.rs）は持たない。
//
//  【書き出しの順序】（W1-S。W1-7 で 2 を「本体を動かさずに 1 世代前を作る」形に変えた。docs/app_platform_roadmap.md §2.7）
//    1. 一時ファイル（save.json.tmp）へ全部書き、`File::sync_all` でディスクまで届ける
//    2. 今の本体（save.json）の 1 世代前（save.json.bak）を作る（本体が無ければ飛ばす）:
//       2a. 本体の写しを `save.json.bak.new` に作る（本体は動かさない。前の書き出しの残りの .bak.new は先に消す）。
//           hard link が使える OS では hard link（Windows の NTFS・デスクトップの Linux）、使えなければ**複製**（読んで書いて sync）。
//           Android は hard link を試さず複製: アプリ（untrusted_app）はアプリのデータ（app_data_file）に hard link を作れない
//           （SELinux。W1-7 の実機〈Pixel 6a・Android 17〉で `avc: denied { link }`・`Permission denied (os error 13)`。試すたびに監査のログが出る）
//       2b. `save.json.bak.new` を `save.json.bak` へ rename する（既存の .bak を原子的に置き換える）
//    3. 一時ファイルを本体へ rename する（既存の本体を原子的に置き換える）
//    4. フォルダを sync する（Unix＝Android だけ。rename の結果＝フォルダの中身をディスクまで届ける）
//  2 で本体を動かさないので、**どの瞬間にも本体がある**（W1-S の「本体を .bak へ rename で回す」では 2 と 3 の間に本体が無く、
//  実機の kill -9 × 100 で 1 回そこに当たり .bak から起動した〈RecoveredFrom=Backup〉。W1-7 の AC-10）。
//  hard link も複製もできないとき（容量が足りない等）だけ、W1-S の rename で回す方式へ戻す（警告はプロセスで 1 回だけ）。
//  そのときだけ下の表の「2 と 3 の間（rename）」の行が起こりうる。複製は前の世代をもう 1 回書く分だけ書き出しが重くなる。
//  既存の本体を削除してから rename する手順（W1-S より前）はやめた。`std::fs::rename` は Unix でも
//  Windows でも既存の宛先を置き換える（Rust 1.98 の標準ライブラリの文書と実装: Windows は
//  `MoveFileExW(MOVEFILE_REPLACE_EXISTING)`、だめなら `SetFileInformationByHandle` の
//  `FILE_RENAME_FLAG_REPLACE_IF_EXISTS`）ので、削除は要らず、削除と rename の間に落ちて本体が消える隙間も無い。
//  `std::fs::hard_link` は Unix の link(2)、Windows の CreateHardLinkW（NTFS で使える。FAT は不可）。複製は `fs::copy` を使わず
//  読んで書く（`fs::copy` は Linux で copy_file_range などを使い、Android のアプリの seccomp の許可に頼りたくないため）。
//
//  【落ちる位置ごとに残るもの】（どの行も recovery.rs の「本体 → .bak → 空」の順で読める）
//  | 落ちる位置                                  | 残るファイル                                          | 次の読み込み                          |
//  | 1 の途中（sync の前）                        | 本体＝前の世代・.bak＝2 つ前・半端な .tmp              | 本体（前の世代）。.tmp は読まない        |
//  | 1 と 2a の間                                 | 本体＝前の世代・.bak＝2 つ前・完全な .tmp              | 本体（前の世代）                        |
//  | 2a の途中・2a と 2b の間                     | 本体＝前の世代・.bak＝2 つ前・.bak.new（作りかけ）     | 本体（前の世代）。.bak.new は次で消す    |
//  | 2b と 3 の間                                 | 本体＝前の世代・.bak＝前の世代（同じ中身）             | 本体（前の世代）                        |
//  | 3 と 4 の間                                  | 本体＝新しい世代・.bak＝前の世代                       | 本体（新しい世代）※                    |
//  | 4 の後                                       | 同上（電源断でも残る）                                 | 本体（新しい世代）                      |
//  | （rename へ戻したとき）2 と 3 の間            | 本体は無い・.bak＝前の世代・完全な .tmp                | .bak（前の世代。RecoveredFrom=Backup）  |
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
use std::sync::atomic::{AtomicBool, Ordering};

use super::file_set::SaveFileSet;

/// hard link が使えず複製へ戻したことを、このプロセスで既に知らせたか（知らせは 1 回だけ）。
static LINK_FALLBACK_WARNED: AtomicBool = AtomicBool::new(false);

/// 複製もできず rename へ戻したことを、このプロセスで既に警告したか（警告は 1 回だけ）。
static RENAME_FALLBACK_WARNED: AtomicBool = AtomicBool::new(false);

/// 1 世代前の作りかけ（.bak.new）を本体から作る関数（本番は hard link と複製。単体テストは失敗を注入する）。
///
/// 引数は（本体, 作る名前）。
pub type StageFn = fn(&Path, &Path) -> io::Result<()>;

/// 1 世代前の作りかけの作り方（先に hard link を試し、だめなら複製）。
#[derive(Debug, Clone, Copy)]
pub struct BackupOps {
    /// hard link を作る（`None` なら試さない。Android はアプリの SELinux の方針で作れないので試さない）。
    pub link: Option<StageFn>,
    /// 読んで書き、sync する（hard link が使えないとき）。
    pub copy: StageFn,
}

impl BackupOps {
    /// この OS の本番の作り方（Android は複製だけ。ほかは hard link → 複製）。
    pub fn system() -> Self {
        let link: Option<StageFn> = if cfg!(target_os = "android") { None } else { Some(system_hard_link) };
        Self { link, copy: copy_synced }
    }
}

/// 今の本体から 1 世代前をどう作ったか。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BackupMethod {
    /// 本体を hard link して .bak を置き換えた（本体は動かさない）。
    HardLink,
    /// 本体を複製して .bak を置き換えた（本体は動かさない。Android の既定）。
    Copy,
    /// hard link も複製もできず、本体を .bak へ rename で回した（W1-S の方式。本体が無い隙間がある）。
    Rename,
}

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
    /// 今の本体から 1 世代前（.bak）を作ったか。
    pub rotated: bool,
    /// 1 世代前を作った方法（作っていなければ `None`）。
    pub backup_method: Option<BackupMethod>,
    /// 書き出しは済んだが知らせておくこと（.bak を作れなかった・hard link が使えず複製へ・複製もできず rename へ戻した等）。
    pub warnings: Vec<String>,
}

/// 本文を本体へ書き出す（上の順序。1 世代前は本体を動かさずに作る）。
///
/// # 引数
/// * `files`    - 本体と仲間のファイルのパス
/// * `bytes`    - 書く本文
/// * `handling` - 今の本体から .bak を作るか
///
/// # 戻り値
/// 書けたら `Ok`（注意書き付き）。書けなければ `Err`。`Err` のとき本体は元のまま
/// （rename へ戻したときに 2 の後で 3 が失敗したら、回した .bak を本体へ戻して「本体が無い」状態を残さない）。
pub fn replace_primary(files: &SaveFileSet, bytes: &[u8], handling: PrimaryHandling) -> io::Result<WriteReport> {
    replace_primary_with(files, bytes, handling, BackupOps::system())
}

/// 本番の hard link（`fs::hard_link` は型引数を取るので、`StageFn` の形に包む）。
///
/// # 引数
/// * `original` - 元のファイル（本体）
/// * `link`     - 作る名前（.bak.new）
fn system_hard_link(original: &Path, link: &Path) -> io::Result<()> {
    fs::hard_link(original, link)
}

/// 本番の複製: 本体を読み、作る名前へ書いて sync する（`fs::copy` は使わない。ファイル先頭のコメント）。
///
/// # 引数
/// * `original` - 元のファイル（本体）
/// * `copy`     - 作る名前（.bak.new）
fn copy_synced(original: &Path, copy: &Path) -> io::Result<()> {
    let bytes = fs::read(original)?;
    write_synced(copy, &bytes)
}

/// `replace_primary` の本体（1 世代前の作り方を差し替えられる。単体テストで失敗を注入する）。
///
/// # 引数
/// * `files`    - 本体と仲間のファイルのパス
/// * `bytes`    - 書く本文
/// * `handling` - 今の本体から .bak を作るか
/// * `ops`      - 1 世代前の作りかけの作り方（本番は `BackupOps::system()`）
pub fn replace_primary_with(
    files: &SaveFileSet,
    bytes: &[u8],
    handling: PrimaryHandling,
    ops: BackupOps,
) -> io::Result<WriteReport> {
    let dir = files.dir();
    ensure_dir(&dir)?;

    // 1. 一時ファイルへ書き切ってディスクまで届ける（ここで失敗しても本体・.bak は無傷）
    write_synced(files.temp(), bytes)?;

    // 2. 今の本体から 1 世代前を作る（hard link か複製なら本体は動かない。どちらもだめなら rename で回す）
    let mut report = WriteReport::default();
    if handling == PrimaryHandling::RotateToBackup {
        rotate_backup(files, ops, &mut report);
    }

    // 3. 一時ファイルを本体にする（既存の本体を原子的に置き換える）
    if let Err(e) = fs::rename(files.temp(), files.primary()) {
        if report.backup_method == Some(BackupMethod::Rename) {
            // rename で回したときだけ本体が無い。その状態を残さない（読み込みは .bak からも戻れるが、本体があるほうが素直）。
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

/// 手順 2: 今の本体から 1 世代前（.bak）を作る。作れなくても書き出しは続ける（最新の状態を残すことを優先する）。
///
/// 本体の写しを `.bak.new` に作り（2a。hard link → だめなら複製）、`.bak` へ rename（2b）。どちらも作れなければ
/// 本体を `.bak` へ rename で回す（W1-S の方式）。
///
/// # 引数
/// * `files`  - 本体と仲間のファイルのパス
/// * `ops`    - 作りかけの作り方
/// * `report` - 結果を書き足す先
fn rotate_backup(files: &SaveFileSet, ops: BackupOps, report: &mut WriteReport) {
    // 本体がまだ無い（初めての書き出し・本体を退避した後）。作るものが無い
    if !files.primary().exists() {
        return;
    }
    // 前の書き出しが 2a と 2b の間で落ちた残り（hard link はあると作れない）。無ければ何もしない
    match fs::remove_file(files.backup_staging()) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::NotFound => {}
        Err(e) => report.warnings.push(format!(
            "前の書き出しの残り（{}）を消せませんでした: {e}",
            files.backup_staging().display()
        )),
    }
    // 2a. 本体の写しを .bak.new に作る（本体は動かさない）
    let Some(method) = stage_backup(files, ops, report) else {
        // hard link も複製もできない（容量が足りない等）: W1-S の rename で回す方式へ戻す（警告はプロセスで 1 回）
        if !RENAME_FALLBACK_WARNED.swap(true, Ordering::Relaxed) {
            report.warnings.push(
                "1 世代前の写しを作れないので、本体の rename で作ります（本体が一瞬無くなる隙間が残ります）".to_string(),
            );
        }
        rotate_by_rename(files, report);
        return;
    };
    // 2b. .bak.new を .bak へ（既存の .bak を原子的に置き換える）
    match fs::rename(files.backup_staging(), files.backup()) {
        Ok(()) => {
            report.rotated = true;
            report.backup_method = Some(method);
        }
        Err(e) => {
            // .bak は前の世代のまま。作りかけの .bak.new は消す（消せなくても次の書き出しで消す）
            let _ = fs::remove_file(files.backup_staging());
            report.warnings.push(format!(
                "1 世代前（{}）を置き換えられませんでした（前の .bak を残したまま本体を置き換えます）: {e}",
                files.backup().display()
            ));
        }
    }
}

/// 手順 2a: 本体の写しを `.bak.new` に作る（hard link を試し、だめなら複製）。
///
/// # 引数
/// * `files`  - 本体と仲間のファイルのパス
/// * `ops`    - 作りかけの作り方
/// * `report` - 結果を書き足す先
///
/// # 戻り値
/// 作れた方法。どちらも作れなければ `None`（作りかけは消してある）。
fn stage_backup(files: &SaveFileSet, ops: BackupOps, report: &mut WriteReport) -> Option<BackupMethod> {
    if let Some(link) = ops.link {
        match link(files.primary(), files.backup_staging()) {
            Ok(()) => return Some(BackupMethod::HardLink),
            // hard link が使えない（FAT・exFAT・権限など）: 複製へ（知らせはプロセスで 1 回）
            Err(e) => {
                if !LINK_FALLBACK_WARNED.swap(true, Ordering::Relaxed) {
                    report.warnings.push(format!("hard link が使えないので、1 世代前は複製で作ります: {e}"));
                }
            }
        }
    }
    match (ops.copy)(files.primary(), files.backup_staging()) {
        Ok(()) => Some(BackupMethod::Copy),
        Err(e) => {
            let _ = fs::remove_file(files.backup_staging());
            report.warnings.push(format!("1 世代前の複製（{}）を作れませんでした: {e}", files.backup_staging().display()));
            None
        }
    }
}

/// W1-S の方式: 本体を .bak へ rename で回す（本体が無い間ができる）。
///
/// # 引数
/// * `files`  - 本体と仲間のファイルのパス
/// * `report` - 結果を書き足す先
fn rotate_by_rename(files: &SaveFileSet, report: &mut WriteReport) {
    match fs::rename(files.primary(), files.backup()) {
        Ok(()) => {
            report.rotated = true;
            report.backup_method = Some(BackupMethod::Rename);
        }
        // 本体がまだ無い。回すものが無い
        Err(e) if e.kind() == io::ErrorKind::NotFound => {}
        // 回せなくても書き出しは続ける（3 の rename は既存の本体を原子的に置き換える）。
        // 最新の状態を残すことを 1 世代前の更新より優先する（.bak は 1 つ古い世代のまま残る）。
        Err(e) => report.warnings.push(format!(
            "本体を 1 世代前（{}）へ回せませんでした（前の .bak を残したまま本体を置き換えます）: {e}",
            files.backup().display()
        )),
    }
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

    /// いつも失敗する hard link（FAT などで使えない・Android の SELinux で禁止の代わり）。
    fn failing_link(_: &Path, _: &Path) -> io::Result<()> {
        Err(io::Error::new(io::ErrorKind::PermissionDenied, "hard link は使えません（試験で注入）"))
    }

    /// いつも失敗する複製（容量が足りない等の代わり）。
    fn failing_copy(_: &Path, _: &Path) -> io::Result<()> {
        Err(io::Error::new(io::ErrorKind::StorageFull, "複製できません（試験で注入）"))
    }

    /// hard link を試さず複製だけで作る（Android の本番と同じ形）。
    fn copy_only() -> BackupOps {
        BackupOps { link: None, copy: copy_synced }
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
        // この OS の本番の作り方（テストの PC は hard link が使える。Android は複製）で、本体を動かさずに作る
        assert!(matches!(second.backup_method, Some(BackupMethod::HardLink | BackupMethod::Copy)), "{:?}", second.backup_method);
        assert!(second.warnings.is_empty(), "{:?}", second.warnings);
        assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{\"g\":2}");
        assert_eq!(fs::read_to_string(files.backup()).unwrap(), "{\"g\":1}");
    }

    /// (g) hard link と複製のどちらでも: 3 回書くと本体＝3 世代目・.bak＝2 世代目で、作りかけ（.bak.new）は残らない。
    /// 前の書き出しが 2a と 2b の間で落ちた残り（.bak.new）があっても消して作り直す。
    #[test]
    fn staged_rotation_keeps_generations_and_no_staging_left() {
        for (tag, ops, expected) in [
            ("hard_link", BackupOps { link: Some(system_hard_link), copy: copy_synced }, BackupMethod::HardLink),
            ("copy", copy_only(), BackupMethod::Copy),
        ] {
            let dir = TestDir::new(tag);
            let files = SaveFileSet::new(dir.0.join("save.json"));
            replace_primary_with(&files, b"{\"g\":1}", PrimaryHandling::RotateToBackup, ops).unwrap();
            replace_primary_with(&files, b"{\"g\":2}", PrimaryHandling::RotateToBackup, ops).unwrap();
            // 2a と 2b の間で落ちた残り（中身は関係ない。hard link の宛先にあると作れないので消されるはず）
            fs::write(files.backup_staging(), b"{\"stale\":true}").unwrap();

            let third = replace_primary_with(&files, b"{\"g\":3}", PrimaryHandling::RotateToBackup, ops).unwrap();
            assert_eq!(third.backup_method, Some(expected), "{tag}");
            assert!(third.warnings.is_empty(), "{tag}: {:?}", third.warnings);
            assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{\"g\":3}", "{tag}");
            assert_eq!(fs::read_to_string(files.backup()).unwrap(), "{\"g\":2}", "{tag}");
            assert!(!files.backup_staging().exists(), "{tag}: .bak.new が残った");
            assert!(!files.temp().exists(), "{tag}: 一時ファイルが残った");
        }
    }

    /// (g) 2 と 3 の間（1 世代前を作った直後・一時ファイルを本体にする前）でも本体は消えていない（W1-7 で塞いだ隙間）。
    #[test]
    fn primary_still_exists_between_backup_and_replace() {
        for (tag, ops) in [("between_link", BackupOps { link: Some(system_hard_link), copy: copy_synced }), ("between_copy", copy_only())] {
            let dir = TestDir::new(tag);
            let files = SaveFileSet::new(dir.0.join("save.json"));
            replace_primary_with(&files, b"{\"g\":1}", PrimaryHandling::RotateToBackup, ops).unwrap();

            // 手順 2 だけを行う（ここで落ちた状態）
            let mut report = WriteReport::default();
            rotate_backup(&files, ops, &mut report);
            assert!(report.rotated, "{tag}");
            assert_ne!(report.backup_method, Some(BackupMethod::Rename), "{tag}");
            assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{\"g\":1}", "{tag}: 本体が無い・変わった");
            assert_eq!(fs::read_to_string(files.backup()).unwrap(), "{\"g\":1}", "{tag}");
            assert!(!files.backup_staging().exists(), "{tag}");
        }
    }

    /// (h) hard link が使えない（失敗を注入）ときは複製で作り、本体は動かない。書き出しは成功し、世代も正しい。
    #[test]
    fn falls_back_to_copy_when_hard_link_fails() {
        let ops = BackupOps { link: Some(failing_link), copy: copy_synced };
        let dir = TestDir::new("fallback_copy");
        let files = SaveFileSet::new(dir.0.join("save.json"));
        replace_primary_with(&files, b"{\"g\":1}", PrimaryHandling::RotateToBackup, ops).unwrap();

        let second = replace_primary_with(&files, b"{\"g\":2}", PrimaryHandling::RotateToBackup, ops).unwrap();
        assert!(second.rotated);
        assert_eq!(second.backup_method, Some(BackupMethod::Copy));
        assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{\"g\":2}");
        assert_eq!(fs::read_to_string(files.backup()).unwrap(), "{\"g\":1}");
        assert!(!files.backup_staging().exists());
        assert!(!files.temp().exists());
        // 初めての書き出し（本体が無い）は試さない（作るものが無い。警告も出さない）
        let fresh_dir = TestDir::new("fallback_fresh");
        let fresh = SaveFileSet::new(fresh_dir.0.join("save.json"));
        let first = replace_primary_with(&fresh, b"{}", PrimaryHandling::RotateToBackup, ops).unwrap();
        assert!(!first.rotated);
        assert!(first.warnings.is_empty(), "{:?}", first.warnings);
    }

    /// (h) hard link も複製もできないときだけ rename で回す方式へ戻し、書き出しは成功する（世代も正しい。作りかけは残らない）。
    #[test]
    fn falls_back_to_rename_when_link_and_copy_fail() {
        let ops = BackupOps { link: Some(failing_link), copy: failing_copy };
        let dir = TestDir::new("fallback_rename");
        let files = SaveFileSet::new(dir.0.join("save.json"));
        replace_primary_with(&files, b"{\"g\":1}", PrimaryHandling::RotateToBackup, ops).unwrap();

        let second = replace_primary_with(&files, b"{\"g\":2}", PrimaryHandling::RotateToBackup, ops).unwrap();
        assert!(second.rotated);
        assert_eq!(second.backup_method, Some(BackupMethod::Rename));
        assert!(!second.warnings.is_empty(), "複製できなかったことは毎回知らせる");
        assert_eq!(fs::read_to_string(files.primary()).unwrap(), "{\"g\":2}");
        assert_eq!(fs::read_to_string(files.backup()).unwrap(), "{\"g\":1}");
        assert!(!files.backup_staging().exists());
        assert!(!files.temp().exists());
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
