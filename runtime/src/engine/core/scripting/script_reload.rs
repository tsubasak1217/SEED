// ============================================================
//  scripting/script_reload.rs — 同梱 .NET（Android）で、実行中にユーザースクリプトの DLL を読み直す（RELOAD_SCRIPTS）
//
//  【PC との違い】
//  PC の RELOAD_SCRIPTS はアセットルートの .cs をその場で再コンパイルする（Roslyn。ScriptingHost::compile_scripts）。
//  Android には .cs も Roslyn の参照アセンブリも無いので、エディタ／SeedAndroid が SeedPak --scripts-only で作った
//  SEEDUserScripts.dll を run-as で files/bin/ へ送り、それを読み直す（C# の collectible AssemblyLoadContext を入れ替える。
//  ScriptingHost::load_precompiled_scripts_from_bytes）。
//
//  【どこから読むか】
//  起動のときと同じ候補（files/bin/ → 外部の files/bin/ → APK の bin/。Android の糊が並べる）から、
//  「SEEDScripting.dll がある最初の置き場」を選び直す（script_binaries::choose_binaries）。起動の後で files/bin/ へ
//  送られたなら、そちらが選ばれる。
//
//  【スクリプトホストが変わっていたら読まない】
//  スクリプトホスト（SEEDScripting.dll）は起動時に Default の AssemblyLoadContext へ読み、差し替えられない。
//  ユーザースクリプトは同じビルドのホストの API に対してコンパイルされるので、選んだ置き場の SEEDScripting.dll が
//  起動時に読んだものと中身が違う（scripting/ を変えた後の push 等）ときは読まずに理由を返す（アプリを起動し直す）。
//  中身の比較は FNV-1a 64bit（prefab_hash::content_hash_bytes。取り違えても「差し替えを断る／通す」だけで壊れない）。
// ============================================================

use std::sync::Arc;

use crate::engine::core::app_base::prefab_hash::content_hash_bytes;

use super::script_binaries::{choose_binaries, ScriptBinarySource};
use super::{PRECOMPILED_SCRIPTS_DLL_NAME, SCRIPTING_HOST_DLL_NAME};

/// 読み直すユーザースクリプトの DLL（中身と、どこから読んだか）。
pub struct ReloadableScripts {
    /// SEEDUserScripts.dll の中身。
    pub assembly: Vec<u8>,
    /// 読んだ場所（ログ・応答用）。
    pub location: String,
}

/// 実行中にユーザースクリプトの DLL を読み直すための置き場と、起動時のスクリプトホストの指紋。
pub struct ScriptReloadSource {
    /// 置き場の候補（起動のときと同じ順。files/bin/ → 外部の files/bin/ → APK の bin/）。
    candidates: Vec<Arc<dyn ScriptBinarySource>>,
    /// 起動時に読んだ SEEDScripting.dll の内容ハッシュ。
    boot_host_digest: String,
    /// 起動時に読んだ SEEDScripting.dll の場所（理由の文言用）。
    boot_host_location: String,
}

impl ScriptReloadSource {
    /// 起動時の置き場の候補と、起動時に読んだスクリプトホストの中身から作る。
    ///
    /// # 引数
    /// * `candidates`         - 置き場の候補（優先順）
    /// * `boot_host`          - 起動時に読んだ SEEDScripting.dll の中身
    /// * `boot_host_location` - その場所（ログ用）
    pub fn new(candidates: Vec<Arc<dyn ScriptBinarySource>>, boot_host: &[u8], boot_host_location: String) -> Self {
        Self { candidates, boot_host_digest: content_hash_bytes(boot_host), boot_host_location }
    }

    /// いまの置き場から、読み直すユーザースクリプトの DLL を読む（まだ C# には渡さない）。
    ///
    /// スクリプトのインスタンスを捨てる前に呼ぶ（読めない・ホストが違うときは、今のスクリプトのまま続けられるように）。
    ///
    /// # 戻り値
    /// 読んだ DLL。読めない・ホストが起動時と違うときは理由。
    pub fn read_user_scripts(&self) -> Result<ReloadableScripts, String> {
        let index = choose_binaries(&self.candidates)
            .ok_or_else(|| format!("どの置き場にも {SCRIPTING_HOST_DLL_NAME} がありません"))?;
        let source = &self.candidates[index];
        let host = source
            .read(SCRIPTING_HOST_DLL_NAME)
            .map_err(|err| format!("{} を読めません: {err}", source.describe(SCRIPTING_HOST_DLL_NAME)))?;
        if content_hash_bytes(&host) != self.boot_host_digest {
            return Err(format!(
                "スクリプトホスト（{SCRIPTING_HOST_DLL_NAME}）が起動時と違います（いまの置き場 {} ・起動時 {}）。\
                 scripting/ を変えたときは実行中に差し替えられません。アプリを起動し直してください（run / push）",
                source.describe(SCRIPTING_HOST_DLL_NAME),
                self.boot_host_location
            ));
        }
        let location = source.describe(PRECOMPILED_SCRIPTS_DLL_NAME);
        let assembly = source
            .read(PRECOMPILED_SCRIPTS_DLL_NAME)
            .map_err(|err| format!("{location} を読めません: {err}"))?;
        Ok(ReloadableScripts { assembly, location })
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use std::io;
    use std::sync::Mutex;

    /// 中身を後から書き換えられるメモリ上の置き場（push で files/bin/ が現れる様子を模す）。
    struct MemoryBinaries {
        name: &'static str,
        files: Mutex<HashMap<String, Vec<u8>>>,
    }

    impl MemoryBinaries {
        fn new(name: &'static str, files: &[(&str, &[u8])]) -> Arc<Self> {
            Arc::new(Self {
                name,
                files: Mutex::new(files.iter().map(|(n, b)| (n.to_string(), b.to_vec())).collect()),
            })
        }
        fn put(&self, file: &str, bytes: &[u8]) {
            self.files.lock().unwrap().insert(file.to_string(), bytes.to_vec());
        }
    }

    impl ScriptBinarySource for MemoryBinaries {
        fn read(&self, file_name: &str) -> io::Result<Vec<u8>> {
            self.files.lock().unwrap().get(file_name).cloned().ok_or_else(|| io::Error::from(io::ErrorKind::NotFound))
        }
        fn contains(&self, file_name: &str) -> bool {
            self.files.lock().unwrap().contains_key(file_name)
        }
        fn describe(&self, file_name: &str) -> String {
            format!("{}:{file_name}", self.name)
        }
    }

    /// 起動は APK の bin/ から。後から files/bin/ へ送られたら、そちらのユーザースクリプトを読む。
    #[test]
    fn reads_pushed_user_scripts_after_boot() {
        let pushed = MemoryBinaries::new("files/bin", &[]);
        let apk = MemoryBinaries::new("apk", &[(SCRIPTING_HOST_DLL_NAME, b"host-v1"), (PRECOMPILED_SCRIPTS_DLL_NAME, b"user-v1")]);
        let candidates: Vec<Arc<dyn ScriptBinarySource>> = vec![pushed.clone(), apk.clone()];
        let source = ScriptReloadSource::new(candidates, b"host-v1", "apk:SEEDScripting.dll".to_string());

        // まだ送っていない: APK の bin/ を読み直す
        let first = source.read_user_scripts().unwrap();
        assert_eq!(first.assembly, b"user-v1");
        assert_eq!(first.location, "apk:SEEDUserScripts.dll");

        // files/bin/ へ同じホストと新しいユーザースクリプトを送った: そちらを読む
        pushed.put(SCRIPTING_HOST_DLL_NAME, b"host-v1");
        pushed.put(PRECOMPILED_SCRIPTS_DLL_NAME, b"user-v2");
        let second = source.read_user_scripts().unwrap();
        assert_eq!(second.assembly, b"user-v2");
        assert_eq!(second.location, "files/bin:SEEDUserScripts.dll");
    }

    /// 選んだ置き場のスクリプトホストが起動時と違えば、読まずに理由を返す（アプリの起動し直しを促す）。
    #[test]
    fn refuses_when_host_changed() {
        let pushed = MemoryBinaries::new("files/bin", &[(SCRIPTING_HOST_DLL_NAME, b"host-v2"), (PRECOMPILED_SCRIPTS_DLL_NAME, b"user")]);
        let candidates: Vec<Arc<dyn ScriptBinarySource>> = vec![pushed];
        let source = ScriptReloadSource::new(candidates, b"host-v1", "apk:SEEDScripting.dll".to_string());
        let reason = source.read_user_scripts().err().expect("ホストが違うので断る");
        assert!(reason.contains("起動時と違います"), "{reason}");
    }

    /// ユーザースクリプトの DLL が無い・どこにもホストが無いときは理由を返す。
    #[test]
    fn reports_missing_files() {
        let host_only = MemoryBinaries::new("files/bin", &[(SCRIPTING_HOST_DLL_NAME, b"h")]);
        let source = ScriptReloadSource::new(vec![host_only], b"h", "x".to_string());
        assert!(source.read_user_scripts().err().unwrap().contains("SEEDUserScripts.dll"));

        let empty = MemoryBinaries::new("files/bin", &[]);
        let nowhere = ScriptReloadSource::new(vec![empty], b"h", "x".to_string());
        assert!(nowhere.read_user_scripts().err().unwrap().contains("SEEDScripting.dll"));
    }
}
