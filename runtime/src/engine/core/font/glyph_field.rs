// ============================================================
//  font/glyph_field.rs — 焼き上がった 1 グリフの距離場（SDF の R8 / MTSDF の RGBA8）とメトリクス
//
//  【役割】
//  焼く側（rasterizer.rs の 1 チャネルの SDF・msdf/bake.rs の MTSDF）とアトラス（atlas.rs）の間で受け渡す形。
//  メトリクスはすべて **em 単位**（フォントサイズ 1.0 相当）で持ち、描くときにフォントサイズを掛けて拡大縮小する。
// ============================================================

/// 距離場の種類（アトラスのテクスチャの形式と、シェーダーの読み方が決まる）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum DistanceFieldKind {
    /// 1 チャネルの SDF（em 64 で 2 値にしたビットマップから距離変換。R8。2026-10-01 までの方式。A/B と退避に残す）。
    Sdf,
    /// MTSDF（輪郭から作る 3 チャネルの MSDF + 真の SDF。RGBA8。2026-10-02 からの既定）。
    #[default]
    Mtsdf,
}

impl DistanceFieldKind {
    /// 設定の文字列から読む（"sdf" / "mtsdf"・"msdf"。大文字小文字は区別しない。知らなければ None）。
    pub fn parse(s: &str) -> Option<Self> {
        match s.trim().to_ascii_lowercase().as_str() {
            "sdf" => Some(Self::Sdf),
            "mtsdf" | "msdf" => Some(Self::Mtsdf),
            _ => None,
        }
    }

    /// 設定・ログに出す名前。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Sdf => "sdf",
            Self::Mtsdf => "mtsdf",
        }
    }

    /// アトラスの 1 テクセルのバイト数（R8 = 1・RGBA8 = 4）。
    pub fn bytes_per_texel(self) -> usize {
        match self {
            Self::Sdf => 1,
            Self::Mtsdf => super::msdf::params::MTSDF_BYTES_PER_TEXEL,
        }
    }

    /// アトラスのテクスチャの形式（MTSDF は距離そのものなので sRGB にしない）。
    pub fn texture_format(self) -> wgpu::TextureFormat {
        match self {
            Self::Sdf => wgpu::TextureFormat::R8Unorm,
            Self::Mtsdf => wgpu::TextureFormat::Rgba8Unorm,
        }
    }

    /// px → 値の変換の決まり（縁取り・太さ・影のぼかし。em の幅は同じで、上限〈クアッドの端〉だけ違う）。
    pub fn value_spec(self) -> super::sdf::FieldValueSpec {
        use super::msdf::params::{MTSDF_MAX_OUTLINE_VALUE, MTSDF_RANGE_EM};
        match self {
            Self::Sdf => super::sdf::FieldValueSpec::SDF,
            Self::Mtsdf => super::sdf::FieldValueSpec { range_em: MTSDF_RANGE_EM, max_outline_value: MTSDF_MAX_OUTLINE_VALUE },
        }
    }
}

/// 固定の大きさで焼いた 1 グリフぶんの距離場とメトリクス（em 単位）。
pub struct GlyphField {
    /// 距離場（行優先・上の行から。`width * height * 1 テクセルのバイト数`）。
    pub data: Vec<u8>,
    /// 距離場の幅・高さ（余白込み。テクセル）。
    pub width: u32,
    pub height: u32,
    /// 距離場の種類（1 テクセルのバイト数が決まる）。
    pub kind: DistanceFieldKind,
    /// ペン基点 → クアッド左上（Y 下向き、em 単位）。
    pub bearing_em: [f32; 2],
    /// 余白込みのクアッドの大きさ（em 単位）。
    pub size_em: [f32; 2],
    /// 水平アドバンス幅（em 単位）。
    pub advance_em: f32,
    /// 四方の余白（em 単位。文字の実寸 `tight_*` はこれを除く）。
    pub pad_em: f32,
    /// この字の距離場の解像度（em あたりのテクセル数。SDF は 64、MTSDF は 40〜64＝輪郭の長い字ほど大きい）。
    ///
    /// 頂点で運び、シェーダーが「1 テクセルあたりの値の変化」と「画面の上の文字の大きさ」を求めるのに使う。
    pub em_px: f32,
    /// MTSDF の検査（安全弁）に落ちて、真の SDF（アルファ）で描く字か（RGB にアルファを写してある）。
    pub msdf_fallback: bool,
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 設定の文字列の読み取りと名前。
    #[test]
    fn parses_kind_names() {
        assert_eq!(DistanceFieldKind::parse("SDF"), Some(DistanceFieldKind::Sdf));
        assert_eq!(DistanceFieldKind::parse(" mtsdf "), Some(DistanceFieldKind::Mtsdf));
        assert_eq!(DistanceFieldKind::parse("msdf"), Some(DistanceFieldKind::Mtsdf));
        assert_eq!(DistanceFieldKind::parse("bitmap"), None);
        assert_eq!(DistanceFieldKind::default(), DistanceFieldKind::Mtsdf, "既定は MTSDF");
        assert_eq!(DistanceFieldKind::Sdf.bytes_per_texel(), 1);
        assert_eq!(DistanceFieldKind::Mtsdf.bytes_per_texel(), 4);
    }
}
