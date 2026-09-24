// ============================================================
//  input/key_remap.rs — OS 固有のキーをエンジンのキー（KeyCode）へ置き換える表
//
//  【役割】
//  winit が「エンジンの KeyCode に当てはまらない物理キー」（PhysicalKey::Unidentified）として
//  届けるプラットフォーム固有のキーを、スクリプトや既存の入力処理が扱える KeyCode へ置き換える。
//  置き換えの中身は表（データ）で持ち、どの表を使うかはプラットフォームの特性表
//  （platform::PlatformTraits::key_remap）が決める。コードに機種ごとの分岐を書かない。
//
//  【Android の戻るキー】
//  GameActivity は戻るキー（ナビゲーションバーの戻る・戻るジェスチャ）をネイティブへ渡し、winit は
//  physical_key = Unidentified(NativeKeyCode::Android(KEYCODE_BACK)) / logical_key = Named(BrowserBack)
//  として届ける（エンジンの KeyCode には無いので、これまでは入力状態に入らず捨てられていた）。
//  Unity と同じく Escape として扱い、スクリプトは `Input.GetKeyDown(KeyCode.Escape)` で拾える。
//  アプリを終了させるか・ポーズメニューを出すかはスクリプトが決める（エンジンは自動で終了しない）。
//
//  【デスクトップ】表は空（何も置き換えない）。PC のキーボードの「ブラウザの戻る」キーは
//  physical_key が KeyCode::BrowserBack で届くので、Android の表とも一致しない。
// ============================================================

use winit::keyboard::{KeyCode, NativeKeyCode, PhysicalKey};

/// Android の戻るキーのキーコード（android.view.KeyEvent.KEYCODE_BACK）。
///
/// Android SDK の公開定数（値は API 1 から不変）。winit は Android のキーコードを
/// `NativeKeyCode::Android(u32)` にそのまま入れて届ける。
pub const ANDROID_KEYCODE_BACK: u32 = 4;

/// 置き換え規則 1 件（届いた物理キー → エンジンが扱うキー）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KeyRemapRule {
    /// 置き換え元（winit が届けた物理キー。OS 固有のキーは Unidentified(NativeKeyCode::…) で届く）。
    pub from: PhysicalKey,
    /// 置き換え先（入力状態・スクリプト・エディタ操作が見るキー）。
    pub to: KeyCode,
}

/// Android の置き換え表。
///
/// | 置き換え元 | 置き換え先 | 理由 |
/// |---|---|---|
/// | 戻るキー（KEYCODE_BACK。戻るジェスチャも同じ） | Escape | Unity と同じ対応。スクリプトの `Input.GetKeyDown(KeyCode.Escape)` で拾う |
pub const ANDROID_KEY_REMAP: &[KeyRemapRule] = &[KeyRemapRule {
    from: PhysicalKey::Unidentified(NativeKeyCode::Android(ANDROID_KEYCODE_BACK)),
    to: KeyCode::Escape,
}];

/// デスクトップの置き換え表（空＝届いたキーをそのまま使う。従来の振る舞い）。
pub const DESKTOP_KEY_REMAP: &[KeyRemapRule] = &[];

/// 表に従って物理キーを置き換える【純関数】。
///
/// # 引数
/// * `table` - 置き換え表（通常は `platform::CURRENT.key_remap`）
/// * `key`   - winit から届いた物理キー
///
/// # 戻り値
/// 表に一致する規則があれば `PhysicalKey::Code(置き換え先)`、無ければ `key` のまま。
/// 規則が重複していれば表の先頭側が勝つ。
pub fn remap_physical_key(table: &[KeyRemapRule], key: PhysicalKey) -> PhysicalKey {
    table
        .iter()
        .find(|rule| rule.from == key)
        .map_or(key, |rule| PhysicalKey::Code(rule.to))
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// Android の戻るキー（KEYCODE_BACK）は Escape になる。
    #[test]
    fn android_back_key_becomes_escape() {
        let back = PhysicalKey::Unidentified(NativeKeyCode::Android(ANDROID_KEYCODE_BACK));
        assert_eq!(
            remap_physical_key(ANDROID_KEY_REMAP, back),
            PhysicalKey::Code(KeyCode::Escape)
        );
    }

    /// 表に無いキーはそのまま（通常のキー・他の Android 固有キー）。
    #[test]
    fn unlisted_keys_pass_through() {
        let space = PhysicalKey::Code(KeyCode::Space);
        assert_eq!(remap_physical_key(ANDROID_KEY_REMAP, space), space);

        // KEYCODE_MENU（82）は表に無いので置き換えない。
        let menu = PhysicalKey::Unidentified(NativeKeyCode::Android(82));
        assert_eq!(remap_physical_key(ANDROID_KEY_REMAP, menu), menu);
    }

    /// デスクトップの表は空: Android の戻るキーと同じ値が来ても、PC の「ブラウザの戻る」キーでも何も変えない。
    #[test]
    fn desktop_table_changes_nothing() {
        assert!(DESKTOP_KEY_REMAP.is_empty());
        let back = PhysicalKey::Unidentified(NativeKeyCode::Android(ANDROID_KEYCODE_BACK));
        assert_eq!(remap_physical_key(DESKTOP_KEY_REMAP, back), back);
        let browser_back = PhysicalKey::Code(KeyCode::BrowserBack);
        assert_eq!(remap_physical_key(DESKTOP_KEY_REMAP, browser_back), browser_back);
    }

    /// PC のキーボードの「ブラウザの戻る」キー（物理キーが KeyCode::BrowserBack）は Android の表にも一致しない。
    /// 置き換え元を論理キー（Named(BrowserBack)）ではなく Android のキーコードで持つ理由の回帰防止。
    #[test]
    fn android_table_ignores_pc_browser_back_key() {
        let browser_back = PhysicalKey::Code(KeyCode::BrowserBack);
        assert_eq!(remap_physical_key(ANDROID_KEY_REMAP, browser_back), browser_back);
    }

    /// 規則が重複していれば表の先頭が勝つ（表を上から読めば結果が分かるように）。
    #[test]
    fn first_matching_rule_wins() {
        let from = PhysicalKey::Unidentified(NativeKeyCode::Android(ANDROID_KEYCODE_BACK));
        let table = [
            KeyRemapRule { from, to: KeyCode::Escape },
            KeyRemapRule { from, to: KeyCode::Backspace },
        ];
        assert_eq!(remap_physical_key(&table, from), PhysicalKey::Code(KeyCode::Escape));
    }
}
