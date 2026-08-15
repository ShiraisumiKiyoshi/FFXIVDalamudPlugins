using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace FreeAction.Game;

/// <summary>
/// 镜头状态读取。通过 FFXIVClientStructs 直接访问当前摄像机。
/// 新版 FFXIVClientStructs 的 Camera 用 Position(Vector3) + Rotation(Quaternion)。
/// </summary>
public sealed class CameraState
{
    /// <summary>摄像机在世界中的位置。</summary>
    public unsafe Vector3 CameraPosition
    {
        get
        {
            var cm = CameraManager.Instance();
            if (cm == null) return Vector3.Zero;
            var cam = cm->CurrentCamera;
            if (cam == null) return Vector3.Zero;
            // FFXIVClientStructs.FFXIV.Common.Math.Vector3 -> System.Numerics.Vector3
            var p = cam->Position;
            return new Vector3(p.X, p.Y, p.Z);
        }
    }

    /// <summary>
    /// 计算「面向镜头方向」所需的玩家面向角度（弧度）。
    /// 玩家面向 = 从镜头位置指向玩家位置的水平方向（即镜头看向的方向）。
    /// FF14 约定：玩家面向 0 = +Z，atan2(x, z) 给出从 +Z 顺时针的角度。
    /// </summary>
    public float DesiredFacingFromCamera(Vector3 playerPos)
    {
        var camPos = CameraPosition;
        return MathF.Atan2(playerPos.X - camPos.X, playerPos.Z - camPos.Z);
    }
}
