using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FreeAction.Rotation;

namespace FreeAction.UI;

/// <summary>
/// 主窗口。左侧导航栏（状态 / 时间轴 / 设置）+ 右侧内容区。
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private Configuration C => _plugin.Config;

    // 当前选中的导航项：0=状态, 1=时间轴, 2=设置
    private int _navIdx = 0;
    private static readonly string[] _navItems = { "状态", "时间轴", "设置" };

    // 编辑态
    private int _selectedProfileIdx = 0;
    private int _selectedEntryIdx = -1;
    private int _selectedSyncIdx = -1;
    private int _selectedSkillIdx = -1;
    private string _importExportText = string.Empty;
    private string _timelineText = string.Empty;
    private string _timelineParseError = string.Empty;
    private byte[] _timelineTextBuffer = new byte[16384];

    public MainWindow(Plugin plugin) : base("FreeAction###FreeActionMain")
    {
        _plugin = plugin;
        Size = new Vector2(820, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoScrollbar;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // 左侧导航栏 + 右侧内容区
        // 左侧固定宽度 120，右侧填满剩余
        float navWidth = 120f;
        var contentAvail = ImGui.GetContentRegionAvail();

        // 左侧导航
        ImGui.BeginChild("##Nav", new Vector2(navWidth, contentAvail.Y), true);
        for (int i = 0; i < _navItems.Length; i++)
        {
            bool selected = _navIdx == i;
            if (ImGui.Selectable($"{_navItems[i]}##nav{i}", selected, ImGuiSelectableFlags.None, new Vector2(0, 28)))
                _navIdx = i;
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // 右侧内容
        ImGui.BeginChild("##Content", new Vector2(contentAvail.X - navWidth - 8, contentAvail.Y), true);
        switch (_navIdx)
        {
            case 0: DrawStatus(); break;
            case 1: DrawTimelineEditor(); break;
            case 2: DrawSettings(); break;
        }
        ImGui.EndChild();
    }

    // ---------- 状态 ----------
    private void DrawStatus()
    {
        var enabled = C.IsAutoRotationEnabled;
        if (ImGui.Checkbox("自动循环", ref enabled))
        {
            C.IsAutoRotationEnabled = enabled;
            C.Save();
        }
        ImGui.SameLine();
        var combatOnly = C.OnlyInCombat;
        if (ImGui.Checkbox("仅战斗中", ref combatOnly))
        {
            C.OnlyInCombat = combatOnly;
            C.Save();
        }

        var dbg = C.IsDebugLogEnabled;
        if (ImGui.Checkbox("调试日志（打印时间轴/boss 施法检测详情）", ref dbg))
        {
            C.IsDebugLogEnabled = dbg;
            C.Save();
        }

        ImGui.Separator();
        var p = _plugin.Player;
        ImGui.TextUnformatted($"登录: {p.IsLoggedIn}   职业: {p.JobId}   等级: {p.Level}");
        ImGui.TextUnformatted($"HP: {p.HpPercent:F1}%   MP: {p.MpPercent:F1}%   战斗: {p.IsInCombat}");

        var profile = _plugin.Engine.ActiveProfile;
        ImGui.TextUnformatted($"激活配置: {profile?.Name ?? "无"} (技能 {profile?.Entries.Count ?? 0} / 校正点 {profile?.SyncPoints.Count ?? 0})");

        // 时间轴状态
        ImGui.Separator();
        bool running = _plugin.Engine.IsTimelineRunning;
        ImGui.TextUnformatted($"时间轴状态: {(running ? "运行中" : "未启动")}");
        if (running)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"  当前时间: {TimelineEntry.FormatTime(_plugin.Engine.CurrentTimelineMs)}");
        }

        if (ImGui.Button("手动启动时间轴"))
            _plugin.Engine.StartTimeline();
        ImGui.SameLine();
        if (ImGui.Button("手动停止时间轴"))
            _plugin.Engine.StopTimeline();

        ImGui.Separator();
        ImGui.TextUnformatted($"最近技能: #{_plugin.Executor.LastActionId}  " +
                              $"成功: {_plugin.Executor.LastActionSucceeded}  " +
                              $"距上次: {_plugin.Executor.MsSinceLastAction} ms");
    }

    // ---------- 时间轴编辑 ----------
    private void DrawTimelineEditor()
    {
        // Profile 选择
        var names = GetProfileNames();
        if (names.Length == 0)
        {
            ImGui.TextUnformatted("暂无配置。");
            if (ImGui.Button("新建"))
            {
                C.Profiles.Add(new RotationProfile($"配置 {C.Profiles.Count + 1}"));
                _selectedProfileIdx = C.Profiles.Count - 1;
                C.Save();
            }
            return;
        }

        if (_selectedProfileIdx < 0 || _selectedProfileIdx >= C.Profiles.Count)
            _selectedProfileIdx = 0;

        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("##ProfileSel", ref _selectedProfileIdx, names, names.Length))
        {
            var prof = C.Profiles[_selectedProfileIdx];
            C.ActiveProfileName = prof.Name;
            _selectedEntryIdx = -1;
            _selectedSyncIdx = -1;
            _selectedSkillIdx = -1;
            _timelineText = string.Empty;
            _timelineParseError = string.Empty;
            C.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("新建配置"))
        {
            var np = new RotationProfile($"配置 {C.Profiles.Count + 1}");
            C.Profiles.Add(np);
            _selectedProfileIdx = C.Profiles.Count - 1;
            C.ActiveProfileName = np.Name;
            C.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("删除") && C.Profiles.Count > 1)
        {
            C.Profiles.RemoveAt(_selectedProfileIdx);
            _selectedProfileIdx = Math.Max(0, _selectedProfileIdx - 1);
            C.ActiveProfileName = C.Profiles[_selectedProfileIdx].Name;
            C.Save();
        }

        var cur = C.Profiles[_selectedProfileIdx];

        ImGui.SetNextItemWidth(200);
        var nameBuf = cur.Name;
        if (ImGui.InputText("名称##ProfName", ref nameBuf, 64))
        {
            cur.Name = nameBuf;
            if (C.ActiveProfileName == cur.Name) C.ActiveProfileName = nameBuf;
            C.Save();
        }

        var jobId = (int)cur.JobId;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("职业ID(0=通用)", ref jobId))
        {
            cur.JobId = (uint)Math.Max(0, jobId);
            C.Save();
        }

        var en = cur.Enabled;
        if (ImGui.Checkbox("启用该配置", ref en))
        {
            cur.Enabled = en;
            C.Save();
        }

        // 子标签页：技能池 / 文本编辑 / 条目列表 / 校正点 / 导入导出
        ImGui.Separator();
        if (ImGui.BeginTabBar("###ProfileTabs"))
        {
            if (ImGui.BeginTabItem("技能池"))
            {
                DrawSkillsEditor(cur);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("文本编辑时间轴"))
            {
                DrawTimelineTextEdit(cur);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("条目列表"))
            {
                DrawEntriesList(cur);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("校正点"))
            {
                DrawSyncPointsEditor(cur);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("导入/导出"))
            {
                DrawImportExport(cur);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    // ---------- 技能池 ----------
    private void DrawSkillsEditor(RotationProfile cur)
    {
        ImGui.TextDisabled("技能池：本配置会用到的技能（名字 + ActionId）。文本编辑时间轴时按名字引用。");
        ImGui.Separator();

        // 列表
        for (int i = 0; i < cur.Skills.Count; i++)
        {
            var s = cur.Skills[i];
            string label = $"  {s}";
            bool sel = i == _selectedSkillIdx;
            if (ImGui.Selectable($"{label}##Skill{i}", sel))
                _selectedSkillIdx = i;
        }

        if (ImGui.Button("+ 添加技能"))
        {
            cur.Skills.Add(new SkillDef("新技能", 0));
            _selectedSkillIdx = cur.Skills.Count - 1;
            C.Save();
        }

        if (_selectedSkillIdx >= 0 && _selectedSkillIdx < cur.Skills.Count)
        {
            var s = cur.Skills[_selectedSkillIdx];
            ImGui.Separator();
            ImGui.TextUnformatted($"编辑技能 #{_selectedSkillIdx + 1}");

            var name = s.Name;
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputText("技能名##skname", ref name, 32))
            {
                s.Name = name;
                C.Save();
            }
            ImGui.SameLine();
            var aid = (int)s.ActionId;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("ActionId##skid", ref aid))
            {
                s.ActionId = (uint)Math.Max(0, aid);
                C.Save();
            }

            if (ImGui.Button("删除该技能"))
            {
                cur.Skills.RemoveAt(_selectedSkillIdx);
                _selectedSkillIdx = -1;
                C.Save();
            }
        }
    }

    // ---------- 文本批量编辑时间轴 ----------
    private void DrawTimelineTextEdit(RotationProfile cur)
    {
        ImGui.TextDisabled("每行格式: 00:22.5 \"<月光>~\"  （时间 mm:ss.f，尖括号内为技能池中的技能名）");
        ImGui.Separator();

        // 同步文本框内容：首次打开此标签时把当前条目导出到文本
        if (string.IsNullOrEmpty(_timelineText) && cur.Entries.Count > 0 && _selectedProfileIdx >= 0)
        {
            // 仅当文本框为空时填充，避免覆盖用户编辑中的内容
            _timelineText = cur.ExportTimelineText();
        }

        // 多行文本输入
        ImGui.SetNextItemWidth(-1);
        // 把 _timelineText 写入字节缓冲，InputTextMultine 修改后写回
        if (_timelineText.Length * 3 + 1 > _timelineTextBuffer.Length)
        {
            int newSize = Math.Max(_timelineText.Length * 6, 32768);
            _timelineTextBuffer = new byte[newSize];
        }
        // 清空 buffer，写入字符串，末尾加 null terminator
        Array.Clear(_timelineTextBuffer, 0, _timelineTextBuffer.Length);
        int byteLen = System.Text.Encoding.UTF8.GetBytes(_timelineText, 0, _timelineText.Length, _timelineTextBuffer, 0);
        if (byteLen < _timelineTextBuffer.Length) _timelineTextBuffer[byteLen] = 0;

        ImGui.InputTextMultiline("##tltext", _timelineTextBuffer.AsSpan(), new Vector2(-1, 320),
            ImGuiInputTextFlags.AllowTabInput, (Dalamud.Bindings.ImGui.ImGui.ImGuiInputTextCallbackDelegate?)null);

        // 读回 buffer 到 _timelineText
        int end = 0;
        while (end < _timelineTextBuffer.Length && _timelineTextBuffer[end] != 0) end++;
        _timelineText = System.Text.Encoding.UTF8.GetString(_timelineTextBuffer, 0, end);

        ImGui.Separator();

        if (ImGui.Button("解析并应用为时间轴"))
        {
            var badLines = cur.ParseTimelineText(_timelineText);
            C.Save();
            if (badLines.Count == 0)
            {
                _timelineParseError = $"✓ 解析成功，共 {cur.Entries.Count} 条";
            }
            else
            {
                _timelineParseError = $"⚠ 解析完成 {cur.Entries.Count} 条；失败行: {string.Join(", ", badLines)}";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("从当前条目重新加载"))
        {
            _timelineText = cur.ExportTimelineText();
            _timelineParseError = string.Empty;
        }
        ImGui.SameLine();
        if (ImGui.Button("清空文本"))
        {
            _timelineText = string.Empty;
            _timelineParseError = string.Empty;
        }

        if (!string.IsNullOrEmpty(_timelineParseError))
        {
            bool isErr = _timelineParseError.StartsWith("⚠");
            var col = isErr ? new Vector4(1, 0.7f, 0.3f, 1) : new Vector4(0.4f, 1, 0.4f, 1);
            ImGui.TextColored(col, _timelineParseError);
        }
    }

    // ---------- 条目列表（精简版，主要用于查看 / 微调） ----------
    private void DrawEntriesList(RotationProfile cur)
    {
        ImGui.TextUnformatted($"时间轴条目（共 {cur.Entries.Count}，按时间升序触发）:");
        ImGui.SameLine();
        if (ImGui.Button("按时间排序"))
        {
            cur.Entries.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            C.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("+ 添加条目"))
        {
            cur.Entries.Add(new TimelineEntry(0f, 0, "新技能"));
            _selectedEntryIdx = cur.Entries.Count - 1;
            C.Save();
        }

        ImGui.BeginChild("EntryList", new Vector2(0, 220), true);
        for (int i = 0; i < cur.Entries.Count; i++)
        {
            var e = cur.Entries[i];
            string fired = e.Fired ? "✓" : "  ";
            string label = $"{fired} [{TimelineEntry.FormatTime(e.TimeMs)}] {e}";
            bool sel = i == _selectedEntryIdx;
            if (ImGui.Selectable($"{label}##Entry{i}", sel))
                _selectedEntryIdx = i;
        }
        ImGui.EndChild();

        if (_selectedEntryIdx >= 0 && _selectedEntryIdx < cur.Entries.Count)
        {
            var e = cur.Entries[_selectedEntryIdx];
            ImGui.Separator();
            ImGui.TextUnformatted($"编辑技能条目 #{_selectedEntryIdx + 1}");

            float totalSec = e.TimeMs / 1000f;
            int mm = (int)(totalSec / 60);
            float ss = totalSec - mm * 60;
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("分##em", ref mm, 0))
            {
                mm = Math.Max(0, mm);
                e.TimeMs = (mm * 60 + Math.Max(0, ss)) * 1000f;
                C.Save();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputFloat("秒##es", ref ss, 0, 0, "%.1f"))
            {
                ss = Math.Max(0, ss);
                if (ss >= 60f) { mm += (int)(ss / 60f); ss = ss % 60f; }
                e.TimeMs = (mm * 60 + ss) * 1000f;
                C.Save();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"= {TimelineEntry.FormatTime(e.TimeMs)}");

            var aid = (int)e.ActionId;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("技能ID##eid", ref aid))
            {
                e.ActionId = (uint)Math.Max(0, aid);
                C.Save();
            }
            ImGui.SameLine();
            var alias = e.Alias;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputText("别名##eal", ref alias, 32))
            {
                e.Alias = alias;
                C.Save();
            }

            if (ImGui.Button("删除该条目##edel"))
            {
                cur.Entries.RemoveAt(_selectedEntryIdx);
                _selectedEntryIdx = -1;
                C.Save();
            }
        }
    }

    // ---------- 校正点 ----------
    private void DrawSyncPointsEditor(RotationProfile cur)
    {
        ImGui.TextUnformatted($"Boss 技能校正点（共 {cur.SyncPoints.Count}）");
        ImGui.SameLine();
        if (ImGui.Button("从 FFLogs 导入...##fflogs"))
        {
            _plugin.FflogsImportWindow.IsOpen = true;
        }

        ImGui.Separator();

        for (int i = 0; i < cur.SyncPoints.Count; i++)
        {
            var sp = cur.SyncPoints[i];
            string fired = sp.Fired ? "✓" : "  ";
            string label = $"{fired} [{TimelineEntry.FormatTime(sp.ExpectedTimeMs)}] {sp}";
            bool sel = i == _selectedSyncIdx;
            if (ImGui.Selectable($"{label}##Sync{i}", sel))
                _selectedSyncIdx = i;
        }

        if (ImGui.Button("+ 添加校正点"))
        {
            cur.SyncPoints.Add(new SyncPoint(0f, "", "新校正点"));
            _selectedSyncIdx = cur.SyncPoints.Count - 1;
            C.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("按时间排序##spSort"))
        {
            cur.SyncPoints.Sort((a, b) => a.ExpectedTimeMs.CompareTo(b.ExpectedTimeMs));
            C.Save();
        }

        if (_selectedSyncIdx >= 0 && _selectedSyncIdx < cur.SyncPoints.Count)
        {
            var sp = cur.SyncPoints[_selectedSyncIdx];
            ImGui.Separator();
            ImGui.TextUnformatted($"编辑校正点 #{_selectedSyncIdx + 1}");

            float totalSec = sp.ExpectedTimeMs / 1000f;
            int mm = (int)(totalSec / 60);
            float ss = totalSec - mm * 60;
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("期望分##SyncMm", ref mm, 0))
            {
                mm = Math.Max(0, mm);
                sp.ExpectedTimeMs = (mm * 60 + Math.Max(0, ss)) * 1000f;
                C.Save();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputFloat("期望秒##SyncSs", ref ss, 0, 0, "%.1f"))
            {
                ss = Math.Max(0, ss);
                if (ss >= 60f) { mm += (int)(ss / 60f); ss = ss % 60f; }
                sp.ExpectedTimeMs = (mm * 60 + ss) * 1000f;
                C.Save();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"= {TimelineEntry.FormatTime(sp.ExpectedTimeMs)}");

            var bossId = sp.BossActionId;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputText("Boss技能ID(十六进制)##sbid", ref bossId, 16))
            {
                sp.BossActionId = bossId;
                C.Save();
            }
            ImGui.SameLine();
            var spAlias = sp.Alias;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputText("别名##SyncAlias", ref spAlias, 32))
            {
                sp.Alias = spAlias;
                C.Save();
            }
            uint parsed = sp.ParseBossActionId();
            ImGui.TextUnformatted($"解析后 ID: 0x{parsed:X} ({parsed})");

            if (ImGui.Button("删除该校正点##spdel"))
            {
                cur.SyncPoints.RemoveAt(_selectedSyncIdx);
                _selectedSyncIdx = -1;
                C.Save();
            }
        }
    }

    // ---------- 导入 / 导出 ----------
    private void DrawImportExport(RotationProfile cur)
    {
        ImGui.TextUnformatted("配置导入/导出（完整 RotationProfile JSON，含技能池/时间轴/校正点）:");
        if (ImGui.Button("导出当前配置到剪贴板##ex"))
        {
            _importExportText = JsonSerializer.Serialize(cur, new JsonSerializerOptions { WriteIndented = true });
            ImGui.SetClipboardText(_importExportText);
        }
        ImGui.SameLine();
        if (ImGui.Button("从剪贴板导入为新配置##im"))
        {
            try
            {
                var text = ImGui.GetClipboardText();
                var imported = JsonSerializer.Deserialize<RotationProfile>(text);
                if (imported != null)
                {
                    imported.Name = string.IsNullOrEmpty(imported.Name) ? "导入" : imported.Name + "(导入)";
                    C.Profiles.Add(imported);
                    _selectedProfileIdx = C.Profiles.Count - 1;
                    C.Save();
                }
            }
            catch (Exception ex)
            {
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"导入失败: {ex.Message}");
            }
        }
    }

    // ---------- 设置 ----------
    private void DrawSettings()
    {
        // ===== 节流与频率 =====
        ImGui.TextUnformatted("节流与频率");
        ImGui.Separator();

        int throttle = C.ActionThrottleMs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("技能调用最小间隔 (ms)##throttle", ref throttle))
        {
            C.ActionThrottleMs = Math.Max(0, throttle);
            C.Save();
        }
        ImGui.TextDisabled("  两次技能释放之间的最小等待时间。");
        ImGui.TextDisabled("  - 时间轴触发技能时，若距上次触发不足此毫秒数，会跳过本次（等下一帧再试）。");
        ImGui.TextDisabled("  - 值越大越保守，避免触发频率过高被判定异常；值越小越激进，可能连发。");
        ImGui.TextDisabled("  - 推荐 100~500ms。默认 100ms。");

        ImGui.Spacing();

        int tick = C.TickIntervalMs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("引擎 Tick 间隔 (ms, 0=每帧)##tick", ref tick))
        {
            C.TickIntervalMs = Math.Max(0, tick);
            C.Save();
        }
        ImGui.TextDisabled("  引擎检查时间轴、触发技能的频率。");
        ImGui.TextDisabled("  - 0 = 每帧检查（最精确，CPU 占用略高）。");
        ImGui.TextDisabled("  - 值越大 CPU 占用越低，但技能触发可能延迟最多一个 tick。");
        ImGui.TextDisabled("  - 推荐 0~50ms。默认 50ms。");

        ImGui.Spacing();
        ImGui.Separator();

        // ===== 命令别名 =====
        ImGui.TextUnformatted("命令别名");
        ImGui.TextDisabled("  可编辑的命令修改后需重新加载插件才生效（输入 /xlplugins 找到 FreeAction 重新加载）。");
        ImGui.Separator();

        ImGui.TextUnformatted("可自定义：");
        ImGui.SetNextItemWidth(200);
        var toggle = C.ToggleCommand;
        if (ImGui.InputText("切换自动循环##cmdToggle", ref toggle, 32))
        {
            // 确保以 / 开头
            if (!toggle.StartsWith("/")) toggle = "/" + toggle;
            C.ToggleCommand = toggle;
            C.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"当前: {C.ToggleCommand}");

        ImGui.SetNextItemWidth(200);
        var win = C.WindowCommand;
        if (ImGui.InputText("打开/关闭主窗口##cmdWin", ref win, 32))
        {
            if (!win.StartsWith("/")) win = "/" + win;
            C.WindowCommand = win;
            C.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"当前: {C.WindowCommand}");

        ImGui.Spacing();
        ImGui.TextUnformatted("固定命令（不可修改）：");
        ImGui.BulletText("/factioncfg     打开主窗口（等同 /factionui）");
        ImGui.BulletText("/factionstart   手动启动时间轴");
        ImGui.BulletText("/factionstop    手动停止时间轴");
        ImGui.BulletText("/factionfflogs  打开 FFLogs 导入窗口");
    }

    // ---------- 辅助 ----------
    private string[] GetProfileNames()
    {
        var arr = new string[C.Profiles.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = C.Profiles[i].Name;
        return arr;
    }
}
