using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;
using FreeAction.Rotation;

namespace FreeAction;

/// <summary>
/// 插件全局配置。序列化为 JSON 持久化。
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>是否启用自动循环（主开关）。</summary>
    public bool IsAutoRotationEnabled { get; set; } = false;

    /// <summary>仅在战斗中触发自动循环。</summary>
    public bool OnlyInCombat { get; set; } = true;

    /// <summary>技能调用前的最小间隔（毫秒），防止刷屏。</summary>
    public int ActionThrottleMs { get; set; } = 100;

    /// <summary>循环引擎每帧检测上限（毫秒），0 表示每帧。</summary>
    public int TickIntervalMs { get; set; } = 50;

    /// <summary>技能轴配置列表。</summary>
    public List<RotationProfile> Profiles { get; set; } = new();

    /// <summary>当前激活的技能轴名称。</summary>
    public string ActiveProfileName { get; set; } = string.Empty;

    /// <summary>是否显示主窗口。</summary>
    public bool IsMainWindowVisible { get; set; } = false;

    /// <summary>是否启用调试日志（周期性时间轴状态、boss 施法检测详情等）。关闭后只打印重要事件。</summary>
    public bool IsDebugLogEnabled { get; set; } = false;

    /// <summary>主窗口位置。</summary>
    public Vector2 MainWindowPos { get; set; } = new(100, 100);

    /// <summary>主窗口大小。</summary>
    public Vector2 MainWindowSize { get; set; } = new(420, 320);

    /// <summary>切换自动循环的斜杠命令别名。</summary>
    public string ToggleCommand { get; set; } = "/faction";

    /// <summary>打开主窗口的斜杠命令别名。</summary>
    public string WindowCommand { get; set; } = "/factionui";

    // 运行时字段（不序列化）
    [NonSerialized] private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
        if (Profiles.Count == 0)
        {
            Profiles.Add(RotationProfile.CreateDefault());
            ActiveProfileName = Profiles[0].Name;
        }
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}
