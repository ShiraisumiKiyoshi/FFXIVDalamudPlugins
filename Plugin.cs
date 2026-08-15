using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FreeAction.Game;
using FreeAction.Rotation;
using FreeAction.UI;

namespace FreeAction;

/// <summary>
/// FreeAction 插件主入口。实现 <see cref="IDalamudPlugin"/>。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "FreeAction";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commands;
    private readonly IFramework _framework;
    private readonly IChatGui _chat;
    private readonly IObjectTable _objectTable;
    private readonly WindowSystem _windowSystem;

    public Configuration Config { get; }
    public PlayerState Player { get; }
    public ActionExecutor Executor { get; }
    public RotationEngine Engine { get; }
    public MainWindow MainWindow => _mainWindow;
    public FflogsImportWindow FflogsImportWindow => _fflogsWindow;

    private readonly MainWindow _mainWindow;
    private readonly FflogsImportWindow _fflogsWindow;

    // boss 施法跟踪：记录每个对象上一帧的施法 ID，仅在从"未施法"切换到"施法"时触发
    private readonly Dictionary<uint, uint> _lastCastByEntity = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IChatGui chat,
        IClientState clientState,
        IObjectTable objectTable)
    {
        _pluginInterface = pluginInterface;
        _commands = commands;
        _framework = framework;
        _chat = chat;
        _objectTable = objectTable;

        // 加载配置
        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(pluginInterface);

        // 子系统
        Player = new PlayerState(objectTable);
        Executor = new ActionExecutor();
        Engine = new RotationEngine(Config, Player, Executor);
        Engine.AttachDebugChat(_chat);

        // UI
        _windowSystem = new WindowSystem(Name);
        _mainWindow = new MainWindow(this);
        _fflogsWindow = new FflogsImportWindow(this);
        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_fflogsWindow);

        // 应用窗口可见性
        _mainWindow.IsOpen = Config.IsMainWindowVisible;

        // 事件
        _framework.Update += OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw += OnUiDraw;
        pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;

        // 命令
        RegisterCommand(Config.ToggleCommand, OnToggleCommand, "切换 FreeAction 自动循环");
        RegisterCommand(Config.WindowCommand, OnWindowCommand, "打开 FreeAction 主窗口");
        RegisterCommand("/factioncfg", OnConfigCommand, "打开 FreeAction 设置");
        RegisterCommand("/factionstart", OnStartCommand, "手动启动时间轴");
        RegisterCommand("/factionstop", OnStopCommand, "手动停止时间轴");
        RegisterCommand("/factionfflogs", OnFflogsCommand, "打开 FFLogs 导入窗口");

        _chat.Print($"[FreeAction] 已加载。用 {Config.ToggleCommand} 切换，{Config.WindowCommand} 打开窗口。");
        _chat.Print($"[FreeAction] 时间轴模式: 战斗开始时自动启动，boss 施法时自动校正。");
    }

    private void RegisterCommand(string name, Action<string, string> handler, string help)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var ci = new CommandInfo(new Dalamud.Game.Command.IReadOnlyCommandInfo.HandlerDelegate(handler)) { HelpMessage = help };
        _commands.AddHandler(name, ci);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            // 引擎 Tick（内部自带节流）
            Engine.Tick();
            // 扫描 boss 施法
            ScanBossCasts();
        }
        catch (Exception ex) { _chat.PrintError($"[FreeAction] Tick 异常: {ex.Message}"); }
    }

    /// <summary>扫描 ObjectTable 中的战斗对象，检测 boss 开始施法的事件。</summary>
    private void ScanBossCasts()
    {
        // 仅在时间轴运行时扫描
        if (!Engine.IsTimelineRunning) return;

        var profile = Engine.ActiveProfile;
        if (profile == null || profile.SyncPoints.Count == 0) return;

        // 清理本轮中已经触发过校正的施法 ID（避免无限缓存）
        // _seenCasts 在 StartTimeline 时已清空，这里无需处理

        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleChara bc) continue;
            if (bc.EntityId == Player.LocalPlayer?.EntityId) continue; // 跳过玩家自己

            uint entityId = bc.EntityId;
            uint currentCastId = 0;

            try
            {
                // IBattleChara.IsCasting / CastActionId
                if (bc.IsCasting)
                {
                    currentCastId = bc.CastActionId;
                }
            }
            catch { continue; }

            // 比较上一帧的施法状态
            _lastCastByEntity.TryGetValue(entityId, out uint lastCast);
            if (currentCastId != 0 && currentCastId != lastCast)
            {
                // 开始新的施法 → 通知引擎
                Engine.NotifyBossCast(currentCastId, entityId);
            }
            _lastCastByEntity[entityId] = currentCastId;
        }
    }

    private void OnUiDraw()
    {
        _windowSystem.Draw();
    }

    private void OnOpenConfig()
    {
        _mainWindow.IsOpen = true;
        Config.IsMainWindowVisible = true;
        Config.Save();
    }

    private void OnToggleCommand(string _, string __)
    {
        Config.IsAutoRotationEnabled = !Config.IsAutoRotationEnabled;
        Config.Save();
        _chat.Print($"[FreeAction] 自动循环: {(Config.IsAutoRotationEnabled ? "开" : "关")}");
    }

    private void OnWindowCommand(string _, string __)
    {
        _mainWindow.Toggle();
        Config.IsMainWindowVisible = _mainWindow.IsOpen;
        Config.Save();
    }

    private void OnConfigCommand(string _, string __)
    {
        _mainWindow.Toggle();
        Config.IsMainWindowVisible = _mainWindow.IsOpen;
        Config.Save();
    }

    private void OnStartCommand(string _, string __)
    {
        Engine.StartTimeline();
    }

    private void OnStopCommand(string _, string __)
    {
        Engine.StopTimeline();
    }

    private void OnFflogsCommand(string _, string __)
    {
        _fflogsWindow.Toggle();
    }

    /// <summary>向游戏聊天框打印一行消息（供子模块调用）。</summary>
    public void PrintChat(string msg) => _chat.Print(msg);

    /// <summary>向游戏聊天框打印一行错误消息。</summary>
    public void PrintChatError(string msg) => _chat.PrintError(msg);

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _pluginInterface.UiBuilder.Draw -= OnUiDraw;
        _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;

        foreach (var cmd in new[] { Config.ToggleCommand, Config.WindowCommand, "/factioncfg", "/factionstart", "/factionstop", "/factionfflogs" })
        {
            if (!string.IsNullOrWhiteSpace(cmd))
                _commands.RemoveHandler(cmd);
        }

        _mainWindow.Dispose();
        _fflogsWindow.Dispose();
    }
}
