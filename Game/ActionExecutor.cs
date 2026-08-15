using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FreeAction.Game;

/// <summary>
/// 技能执行器。通过 ActionManager.UseAction 调用技能。
/// 所有写入操作都在游戏主线程上下文调用（由 Plugin 框架保证）。
/// </summary>
public sealed class ActionExecutor
{
    /// <summary>最近一次成功调用 UseAction 的 UTC 时间。</summary>
    public DateTime LastActionTime { get; private set; } = DateTime.MinValue;

    /// <summary>最近一次调用的技能 ID。</summary>
    public uint LastActionId { get; private set; }

    /// <summary>最近一次调用是否成功。</summary>
    public bool LastActionSucceeded { get; private set; }

    /// <summary>
    /// 释放技能。
    /// </summary>
    /// <param name="actionId">技能 ID</param>
    /// <param name="targetObjectId">目标 ObjectID，0 表示使用当前目标/自身默认</param>
    /// <returns>是否成功排入 ActionManager</returns>
    public unsafe bool UseAction(uint actionId, ulong targetObjectId = 0)
    {
        var am = ActionManager.Instance();
        if (am == null) return false;

        const ActionType actionType = ActionType.Action;
        ulong target = targetObjectId == 0 ? 0xE0000000u : targetObjectId;

        bool ok;
        try
        {
            // UseAction(ActionType, uint actionId, ulong targetId, uint extraParam, UseActionMode, uint comboRouteId, bool* outOptAreaTargeted)
            ok = am->UseAction(actionType, actionId, target, 0, (ActionManager.UseActionMode)0, 0, null);
        }
        catch
        {
            ok = false;
        }

        LastActionId = actionId;
        LastActionSucceeded = ok;
        if (ok) LastActionTime = DateTime.UtcNow;
        return ok;
    }

    /// <summary>距上次成功调用经过的毫秒数。</summary>
    public int MsSinceLastAction
    {
        get
        {
            if (LastActionTime == DateTime.MinValue) return int.MaxValue;
            return (int)(DateTime.UtcNow - LastActionTime).TotalMilliseconds;
        }
    }

    /// <summary>检查技能是否就绪（冷却结束 + 状态允许）。简化实现。</summary>
    public unsafe bool IsReady(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        try
        {
            // GetActionStatus(ActionType, uint actionId, ulong targetId, bool, bool, uint* outOptExtraInfo) -> 0 表示可用
            var status = am->GetActionStatus(ActionType.Action, actionId, 0xE0000000u, false, false, null);
            return status == 0;
        }
        catch
        {
            return false;
        }
    }
}
