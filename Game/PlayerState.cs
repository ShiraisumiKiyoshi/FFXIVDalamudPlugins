using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace FreeAction.Game;

/// <summary>
/// 玩家状态读取与条件判定。所有读取都是只读的，不会修改游戏状态。
/// </summary>
public sealed class PlayerState
{
    private readonly IObjectTable _objectTable;

    public PlayerState(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public IPlayerCharacter? LocalPlayer => _objectTable.LocalPlayer as IPlayerCharacter;

    public bool IsLoggedIn => LocalPlayer != null;

    public bool IsInCombat
    {
        get
        {
            var p = LocalPlayer;
            return p != null && (p.StatusFlags & StatusFlags.InCombat) != 0;
        }
    }

    public uint JobId
    {
        get
        {
            var p = LocalPlayer;
            return p?.ClassJob.RowId ?? 0;
        }
    }

    public byte Level
    {
        get
        {
            var p = LocalPlayer;
            return p?.Level ?? 0;
        }
    }

    public float HpPercent
    {
        get
        {
            var p = LocalPlayer;
            if (p == null || p.MaxHp <= 0) return 0f;
            return 100f * p.CurrentHp / (float)p.MaxHp;
        }
    }

    public float MpPercent
    {
        get
        {
            var p = LocalPlayer;
            if (p == null || p.MaxMp <= 0) return 0f;
            return 100f * p.CurrentMp / (float)p.MaxMp;
        }
    }

    public Vector3 Position
    {
        get
        {
            var p = LocalPlayer;
            return p?.Position ?? Vector3.Zero;
        }
    }

    /// <summary>玩家面向（弧度，Y 轴绕向）。0 = +Z 方向。</summary>
    public float Rotation
    {
        get
        {
            var p = LocalPlayer;
            return p?.Rotation ?? 0f;
        }
    }

    public IGameObject? Target => LocalPlayer?.TargetObject;

    public IBattleChara? BattleTarget => Target as IBattleChara;

    /// <summary>读取自身状态列表。失败时返回 null。</summary>
    public StatusList? SelfStatuses => LocalPlayer?.StatusList;

    /// <summary>读取目标状态列表。失败时返回 null。</summary>
    public StatusList? TargetStatuses => (Target as IBattleChara)?.StatusList;

    /// <summary>自身是否存在指定 StatusId。</summary>
    public bool HasStatus(uint statusId)
    {
        var list = SelfStatuses;
        if (list == null) return false;
        foreach (var s in list)
        {
            if (s != null && s.StatusId == statusId) return true;
        }
        return false;
    }

    /// <summary>目标是否存在指定 StatusId。</summary>
    public bool TargetHasStatus(uint statusId)
    {
        var list = TargetStatuses;
        if (list == null) return false;
        foreach (var s in list)
        {
            if (s != null && s.StatusId == statusId) return true;
        }
        return false;
    }

    /// <summary>技能剩余冷却时间（毫秒）。0 表示就绪。</summary>
    public unsafe float GetCooldownRemainingMs(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null) return float.MaxValue;
        try
        {
            // GetRecastTime / GetRecastTimeElapsed 接受 (ActionType, uint actionId)，返回秒
            float total = am->GetRecastTime(ActionType.Action, actionId);
            float elapsed = am->GetRecastTimeElapsed(ActionType.Action, actionId);
            float remain = total - elapsed;
            return remain > 0f ? remain * 1000f : 0f;
        }
        catch
        {
            return 0f;
        }
    }
}
