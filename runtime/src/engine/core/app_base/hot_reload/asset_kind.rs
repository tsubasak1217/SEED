// ============================================================
//  hot_reload/asset_kind.rs — 差し替えたアセットを「どう取り込み直すか」の表（データ。純粋な処理）
//
//  【反映のしかた】（AssetRefresh）
//    InPlace         … キャッシュを捨てるだけ。描画・再生のたびにキャッシュを引くもの（スプライトの画像・シェーダ・
//                      音声・.postfx・.sprite_mesh 等）は、次に使うときに新しい中身を読む。ゲームの状態はそのまま
//    RebuildScene    … キャッシュを捨ててから今のシーンを読み直す。シーンを組むときに取り込んで持ち続けるもの
//                      （モデル・プレハブ・マテリアル・アニメーション・地形等）。ゲームの状態（スクリプトの変数・位置）は
//                      シーンの開始時へ戻る
//    SceneFile       … シーンファイル。今のシーンならシーンを読み直す。違うシーンなら送っただけ（そのシーンへ遷移したときに反映）
//    RestartRequired … 起動時に 1 回だけ読むもの（プロジェクト設定・フォント）。差し替えられない（アプリを起動し直す）
//    Scripts         … スクリプト（.cs）。RELOAD_ASSET ではなく RELOAD_SCRIPTS で差し替える
//  どの拡張子をどれにするかは下の表（ASSET_KIND_TABLE・FILE_NAME_TABLE）だけを直せば変えられる。表に無い拡張子は
//  RebuildScene（取り込み方が分からないものは、シーンごと読み直すのが確実）。
//  エディタ側の「どの拡張子の変更で何の命令を送るか」の表は editor/src/Android/HotReload/AndroidHotReloadTable.cs
//  （あちらは命令の選び方、こちらはランタイムでの取り込み方）。
// ============================================================

/// 差し替えたアセットの反映のしかた。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AssetRefresh {
    /// キャッシュを捨てるだけ（次に使うときに読み直す。ゲームの状態は保つ）。
    InPlace,
    /// キャッシュを捨ててから今のシーンを読み直す（ゲームの状態はシーンの開始時へ戻る）。
    RebuildScene,
    /// シーンファイル（今のシーンなら読み直す。違えば送っただけ）。
    SceneFile,
    /// 差し替えられない（起動時に 1 回だけ読む。アプリを起動し直す）。
    RestartRequired,
    /// スクリプト（RELOAD_SCRIPTS で差し替える）。
    Scripts,
}

impl AssetRefresh {
    /// 応答の詳細・ログに出す短い名前（エディタの Output に出る）。
    pub fn label(self) -> &'static str {
        match self {
            Self::InPlace => "inplace",
            Self::RebuildScene => "scene",
            Self::SceneFile => "scene_file",
            Self::RestartRequired => "restart",
            Self::Scripts => "scripts",
        }
    }
}

/// 拡張子の組と反映のしかた（表の 1 行）。
struct ExtensionRule {
    /// 対象の拡張子（ドット無し・小文字）。
    extensions: &'static [&'static str],
    /// 反映のしかた。
    refresh: AssetRefresh,
}

/// 拡張子 → 反映のしかたの表（上から順に見る。大文字小文字は問わない）。
const ASSET_KIND_TABLE: &[ExtensionRule] = &[
    // 画像: スプライト・UI・インライン画像のテクスチャはフレームごとにキャッシュを引く。
    // （モデルの中の画像は glb に埋め込まれるのでモデルの差し替えになる。外部の画像を参照するモデルは、モデルかシーンを
    //   差し替え直す＝docs/android.md §23 の制限）
    ExtensionRule {
        extensions: &["png", "jpg", "jpeg", "bmp", "tga", "webp", "gif", "ktx2", "dds"],
        refresh: AssetRefresh::InPlace,
    },
    // シェーディングアセット・水面シェーダ（パスごとの状態を捨てると次のフレームで読み直してビルドする）
    ExtensionRule { extensions: &["wgsl", "shading"], refresh: AssetRefresh::InPlace },
    // スプライトのポストエフェクト・スキンスプライトのメッシュ・インライン画像のアイコン集
    ExtensionRule { extensions: &["postfx", "sprite_mesh", "icons"], refresh: AssetRefresh::InPlace },
    // 音声（再生のたびにキャッシュを引く。鳴っている音は鳴り終わるまで古いまま）
    ExtensionRule { extensions: &["wav", "ogg", "mp3", "flac"], refresh: AssetRefresh::InPlace },
    // モデル（CPU モデル・GPU の統合バッチをシーンを組むときに作る）
    ExtensionRule { extensions: &["glb", "gltf", "obj", "mtl", "bin"], refresh: AssetRefresh::RebuildScene },
    // プレハブ・マテリアル・アニメーション・入力マップ・地形（シーンを組むときに取り込む）
    ExtensionRule {
        extensions: &["actor", "actor2d", "mat", "anim", "inputmap", "tvox", "tscatter", "tcover"],
        refresh: AssetRefresh::RebuildScene,
    },
    // シーン
    ExtensionRule { extensions: &["scene"], refresh: AssetRefresh::SceneFile },
    // フォント（起動時に読み込んでアトラスを作る）
    ExtensionRule { extensions: &["ttf", "otf", "ttc"], refresh: AssetRefresh::RestartRequired },
    // スクリプト（RELOAD_SCRIPTS で差し替える）
    ExtensionRule { extensions: &["cs"], refresh: AssetRefresh::Scripts },
];

/// ファイル名 → 反映のしかたの表（拡張子の表より優先。大文字小文字は問わない）。
const FILE_NAME_TABLE: &[(&str, AssetRefresh)] = &[
    // プロジェクト設定（ウィンドウ・描画品質・シーンの一覧は起動時に 1 回だけ読む）
    ("project_settings.json", AssetRefresh::RestartRequired),
];

/// 表に無いアセットの反映のしかた（取り込み方が分からないので、シーンごと読み直す）。
const DEFAULT_REFRESH: AssetRefresh = AssetRefresh::RebuildScene;

/// パスの区切り。
const PATH_SEPARATOR: char = '/';

/// 拡張子の前の点。
const EXTENSION_DOT: char = '.';

/// 相対パスのアセットの反映のしかたを表から決める【純関数】。
///
/// # 引数
/// * `relative` - アセットルートからの相対パス（区切り /。wire::normalize_relative の結果）
pub fn classify(relative: &str) -> AssetRefresh {
    let file_name = relative.rsplit(PATH_SEPARATOR).next().unwrap_or(relative);
    if let Some((_, refresh)) = FILE_NAME_TABLE.iter().find(|(name, _)| name.eq_ignore_ascii_case(file_name)) {
        return *refresh;
    }
    let Some((_, extension)) = file_name.rsplit_once(EXTENSION_DOT) else {
        return DEFAULT_REFRESH;
    };
    ASSET_KIND_TABLE
        .iter()
        .find(|rule| rule.extensions.iter().any(|candidate| candidate.eq_ignore_ascii_case(extension)))
        .map(|rule| rule.refresh)
        .unwrap_or(DEFAULT_REFRESH)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 画像・シェーダ・音声は InPlace（ゲームの状態を保つ）。大文字小文字は問わない。
    #[test]
    fn frame_cached_assets_are_in_place() {
        for path in ["ui/button.png", "tex/A.JPG", "shaders/water.wgsl", "se/hit.wav", "fx/glow.postfx", "chars/a.sprite_mesh"] {
            assert_eq!(classify(path), AssetRefresh::InPlace, "{path}");
        }
    }

    /// モデル・プレハブ・マテリアル・アニメーション・地形はシーンを読み直す。
    #[test]
    fn scene_built_assets_rebuild_scene() {
        for path in ["models/BrainStem.glb", "models/a.gltf", "prefabs/Fish.actor", "mats/a.mat", "anim/walk.anim", "terrain/t.tvox"] {
            assert_eq!(classify(path), AssetRefresh::RebuildScene, "{path}");
        }
    }

    /// シーン・フォント・スクリプト・プロジェクト設定はそれぞれ専用の扱い。
    #[test]
    fn special_kinds() {
        assert_eq!(classify("scenes/Main.scene"), AssetRefresh::SceneFile);
        assert_eq!(classify("fonts/NotoSans.ttf"), AssetRefresh::RestartRequired);
        assert_eq!(classify("scripts/Player.cs"), AssetRefresh::Scripts);
        assert_eq!(classify("project_settings.json"), AssetRefresh::RestartRequired);
        assert_eq!(classify("Project_Settings.JSON"), AssetRefresh::RestartRequired);
    }

    /// 表に無い拡張子・拡張子の無いファイル・ほかの .json はシーンを読み直す（取り込み方が分からないので確実な方）。
    #[test]
    fn unknown_assets_rebuild_scene() {
        for path in ["data/table.json", "data/README", "misc/a.unknownext", "dir.with.dot/file"] {
            assert_eq!(classify(path), AssetRefresh::RebuildScene, "{path}");
        }
    }
}
