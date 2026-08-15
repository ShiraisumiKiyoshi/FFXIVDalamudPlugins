using System;
using System.Collections.Generic;
using FreeAction.Game;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;

namespace FreeAction.Rotation;

/// <summary>
/// 时间轴循环引擎。
/// - 战斗开始时（倒计时到 0 / 进入战斗）启动时间轴
/// - 按编辑好的时间顺序释放玩家技能
/// - 监听 boss 释放技能，根据校正点同步时间轴
/// </summary>
public sealed class RotationEngine
{
    private readonly Configuration _config;
    private readonly PlayerState _player;
    private readonly ActionExecutor _executor;

    // ---- debug diagnostics ----
    private IChatGui? _debugChat;
    private int _debugCounter;
    private const int DebugPrintEveryN = 120; // ≈ 2 秒一次（60fps 下）

    public void AttachDebugChat(IChatGui? chat) => _debugChat = chat;

    private void DebugLog(string msg)
    {
        if (!_config.IsDebugLogEnabled) return;  // 调试开关关闭时不打印
        _debugCounter++;
        if (_debugCounter % DebugPrintEveryN != 0) return;
        try { _debugChat?.Print($"[FreeAction-Dbg] {msg}"); } catch { }
    }

    /// <summary>显式打印一次（用于重要事件，不受节流影响，但受调试开关控制）。</summary>
    private void PrintOnce(string msg)
    {
        try { _debugChat?.Print($"[FreeAction] {msg}"); } catch { }
    }

    /// <summary>打印调试详情（仅调试开关开启时打印）。</summary>
    private void PrintDebug(string msg)
    {
        if (!_config.IsDebugLogEnabled) return;
        try { _debugChat?.Print($"[FreeAction] {msg}"); } catch { }
    }

    // ---- 时间轴状态 ----
    /// <summary>时间轴起点（UTC）。null 表示未启动。</summary>
    private DateTime? _timelineStartUtc;
    /// <summary>当前时间轴已运行毫秒数（考虑过校正）。每帧更新。</summary>
    public float CurrentTimelineMs { get; private set; } = 0f;
    /// <summary>上次进入战斗状态（用于检测战斗开始）。</summary>
    private bool _wasInCombat;
    /// <summary>已触发过的校正点 ID 集合（本轮）。</summary>
    private readonly HashSet<int> _firedSyncPoints = new();
    /// <summary>本轮中已经见过的 boss 施法 ID（避免一帧内重复触发）。</summary>
    private readonly HashSet<uint> _seenCasts = new();
    /// <summary>boss 施法事件外部注入：由 Plugin 注入回调，传入 (actionId, casterEntityId)。</summary>
    public Action<uint, uint>? OnBossCastObserved;

    public RotationEngine(Configuration config, PlayerState player, ActionExecutor executor)
    {
        _config = config;
        _player = player;
        _executor = executor;
    }

    /// <summary>当前激活的技能轴配置。</summary>
    public RotationProfile? ActiveProfile
    {
        get
        {
            if (_config.Profiles.Count == 0) return null;
            if (!string.IsNullOrEmpty(_config.ActiveProfileName))
            {
                var p = _config.Profiles.Find(x => x.Name == _config.ActiveProfileName);
                if (p != null) return p;
            }
            return _config.Profiles[0];
        }
    }

    /// <summary>是否正在运行时间轴（已启动且未停止）。</summary>
    public bool IsTimelineRunning => _timelineStartUtc.HasValue;

    /// <summary>手动启动时间轴（例如倒计时归零或命令调用）。</summary>
    public void StartTimeline()
    {
        _timelineStartUtc = DateTime.UtcNow;
        CurrentTimelineMs = 0f;
        _firedSyncPoints.Clear();
        _seenCasts.Clear();
        // 重置所有条目的 Fired 标记
        var profile = ActiveProfile;
        if (profile != null)
        {
            foreach (var e in profile.Entries) e.Fired = false;
            foreach (var s in profile.SyncPoints) s.Fired = false;
        }
        PrintOnce($"时间轴已启动: {profile?.Name ?? "无配置"}");
    }

    /// <summary>停止时间轴（战斗结束）。</summary>
    public void StopTimeline()
    {
        if (_timelineStartUtc.HasValue)
        {
            PrintOnce($"时间轴已停止 (运行至 {TimelineEntry.FormatTime(CurrentTimelineMs)})");
        }
        _timelineStartUtc = null;
        CurrentTimelineMs = 0f;
        _firedSyncPoints.Clear();
        _seenCasts.Clear();
    }

    /// <summary>由 Plugin.Framework.Update 每帧调用。</summary>
    public void Tick()
    {
        if (!_config.IsAutoRotationEnabled)
        {
            DebugLog("已跳过: IsAutoRotationEnabled=false");
            return;
        }

        if (!_player.IsLoggedIn)
        {
            DebugLog("已跳过: 玩家未登录");
            return;
        }

        // 检测战斗状态变化
        bool inCombat = _player.IsInCombat;
        if (!_wasInCombat && inCombat)
        {
            // 进入战斗：自动启动时间轴
            StartTimeline();
        }
        else if (_wasInCombat && !inCombat)
        {
            // 退出战斗：停止时间轴
            StopTimeline();
        }
        _wasInCombat = inCombat;

        // 战斗限制
        if (_config.OnlyInCombat && !inCombat)
        {
            DebugLog($"已跳过: OnlyInCombat=true 但 IsInCombat=false (Job={_player.JobId})");
            return;
        }

        var profile = ActiveProfile;
        if (profile == null)
        {
            DebugLog("已跳过: ActiveProfile=null");
            return;
        }
        if (!profile.Enabled)
        {
            DebugLog($"已跳过: Profile[{profile.Name}] Enabled=false");
            return;
        }
        if (profile.JobId != 0 && profile.JobId != _player.JobId)
        {
            DebugLog($"已跳过: Profile[{profile.Name}] JobId={profile.JobId} != 当前职业 {_player.JobId}");
            return;
        }

        // 校正点检测：扫描目标 / 周围对象的当前施法
        DetectBossCasts(profile);

        // 推进时间轴
        if (_timelineStartUtc.HasValue)
        {
            CurrentTimelineMs = (float)(DateTime.UtcNow - _timelineStartUtc.Value).TotalMilliseconds;
        }

        // 触发到达时间的技能
        FireDueEntries(profile);

        DebugLog($"时间轴: {TimelineEntry.FormatTime(CurrentTimelineMs)} 条目={profile.Entries.Count} 校正点={profile.SyncPoints.Count}");
    }

    /// <summary>触发所有已到达时间且尚未释放的技能条目。</summary>
    private void FireDueEntries(RotationProfile profile)
    {
        if (!_timelineStartUtc.HasValue) return;

        foreach (var entry in profile.OrderedEntries)
        {
            if (entry.Fired) continue;
            if (CurrentTimelineMs < entry.TimeMs) continue;

            // 节流：避免连续调用 UseAction
            if (_executor.MsSinceLastAction < _config.ActionThrottleMs)
            {
                DebugLog($"  条目 {entry}: 已到时间但被节流 (MsSinceLastAction={_executor.MsSinceLastAction} < Throttle={_config.ActionThrottleMs})");
                return;
            }

            entry.Fired = true;
            bool ok = _executor.UseAction(entry.ActionId);
            string resultMsg = ok ? "✓ 成功调用" : "✗ UseAction返回false";
            PrintOnce($"[{TimelineEntry.FormatTime(CurrentTimelineMs)}] {entry}: {resultMsg}");
            return; // 每帧最多释放一个技能
        }
    }

    /// <summary>检测 boss 施法并触发校正点。</summary>
    private void DetectBossCasts(RotationProfile profile)
    {
        if (profile.SyncPoints.Count == 0) return;
        if (!_timelineStartUtc.HasValue) return;

        // 遍历当前目标 + 周围可见的战斗对象，检查其 IsCasting / CastActionId
        // 通过外部注入的回调（由 Plugin 扫描 ObjectTable 后调用）
        // 这里只处理已通过回调触发的事件
    }

    /// <summary>
    /// 由 Plugin 在扫描到 boss 开始施法时调用。
    /// 会检查是否匹配某个校正点，匹配则校正时间轴。
    /// </summary>
    /// <param name="actionId">boss 技能 ID</param>
    /// <param name="casterEntityId">施法者 EntityId（用于过滤）</param>
    public void NotifyBossCast(uint actionId, uint casterEntityId)
    {
        if (!_timelineStartUtc.HasValue) return;
        var profile = ActiveProfile;
        if (profile == null) return;

        // 避免一帧内重复
        if (_seenCasts.Contains(actionId)) return;
        _seenCasts.Add(actionId);

        // 打印检测到的 boss 施法（调试信息，受开关控制）
        // 显示十六进制和十进制两种格式
        PrintDebug($"检测到 boss 施法: 0x{actionId:X} ({actionId}) @ {TimelineEntry.FormatTime(CurrentTimelineMs)}");

        // 查找匹配的校正点
        int idx = 0;
        foreach (var sp in profile.SyncPoints)
        {
            if (sp.Fired) { idx++; continue; }
            uint bossId = sp.ParseBossActionId();
            if (bossId == 0) { idx++; continue; }
            if (bossId == actionId)
            {
                // 校正时间轴
                float oldMs = CurrentTimelineMs;
                float newMs = sp.ExpectedTimeMs;
                double deltaSec = (newMs - oldMs) / 1000.0;

                // 重新设置起点，使当前 CurrentTimelineMs = ExpectedTimeMs
                _timelineStartUtc = DateTime.UtcNow.AddMilliseconds(-newMs);
                CurrentTimelineMs = newMs;
                sp.Fired = true;
                _firedSyncPoints.Add(idx);

                // 校正成功是重要事件，始终打印
                PrintOnce($"✓ 校正时间轴: boss#{actionId:X} 触发 {sp}，时间 {TimelineEntry.FormatTime(oldMs)} -> {TimelineEntry.FormatTime(newMs)} (Δ={deltaSec:+0.000;-0.000}s)");

                // 校正后，原本在 oldMs 之前但未触发的条目应补触发？这里采用保守策略：只校正，不回溯触发
                // 但需要重置所有 TimeMs > newMs 的条目的 Fired 标记，以便后续正常触发
                foreach (var e in profile.Entries)
                {
                    if (e.TimeMs > newMs) e.Fired = false;
                }
                return;
            }
            idx++;
        }

        // 未匹配任何校正点：打印当前 profile 的所有 SyncPoint 供对照（调试信息）
        var sb = new System.Text.StringBuilder();
        sb.Append("  未匹配校正点。当前 profile 校正点列表: ");
        bool first = true;
        foreach (var sp in profile.SyncPoints)
        {
            if (!first) sb.Append(", ");
            first = false;
            uint bid = sp.ParseBossActionId();
            sb.Append($"{sp.Alias}[{sp.BossActionId}->0x{bid:X}/{bid}]");
        }
        if (profile.SyncPoints.Count == 0) sb.Append("(空)");
        PrintDebug(sb.ToString());
    }
}
