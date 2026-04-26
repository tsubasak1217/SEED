// ============================================================
//  schedule.rs — システム実行スケジューラ
//
//  Phase 別にシステムを登録し、フレームごとに順番に実行する。
//  Phase の実行順は BeginFrame → EarlyUpdate → Update →
//                   ConstantUpdate → LateUpdate → Render → EndFrame。
// ============================================================

use crate::engine::core::clock::FrameContext;
use super::world::World;
use super::system::System;

// ─── Phase ────────────────────────────────────────────────────────────────────

/// システム実行フェーズ。
/// 各フェーズ内でのシステム実行順は登録順。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum Phase {
    BeginFrame,
    EarlyUpdate,
    Update,
    ConstantUpdate,
    LateUpdate,
    Render,
    EndFrame,
}

/// 全フェーズを実行順に返すユーティリティ。
pub fn all_phases() -> [Phase; 7] {
    use Phase::*;
    [BeginFrame, EarlyUpdate, Update, ConstantUpdate, LateUpdate, Render, EndFrame]
}

// ─── Schedule ─────────────────────────────────────────────────────────────────

/// システムの実行スケジュール。
///
/// 各フェーズに System を登録し、run_phase() で順番に実行する。
/// AppBase はフレームの各タイミングで対応する run_phase() を呼び出す。
pub struct Schedule {
    phases: Vec<(Phase, Vec<Box<dyn System>>)>,
}

impl Schedule {
    pub fn new() -> Self {
        Self {
            phases: all_phases().into_iter().map(|p| (p, Vec::new())).collect(),
        }
    }

    /// 指定フェーズにシステムを追加する（登録順に実行される）。
    pub fn add_system(&mut self, phase: Phase, system: impl System + 'static) {
        if let Some((_, systems)) = self.phases.iter_mut().find(|(p, _)| *p == phase) {
            systems.push(Box::new(system));
        }
    }

    /// 指定フェーズのシステムを順番に実行する。
    pub fn run_phase(&mut self, phase: Phase, world: &mut World, ctx: &FrameContext) {
        if let Some((_, systems)) = self.phases.iter_mut().find(|(p, _)| *p == phase) {
            for sys in systems.iter_mut() {
                sys.run(world, ctx);
            }
        }
    }
}

impl Default for Schedule {
    fn default() -> Self { Self::new() }
}
