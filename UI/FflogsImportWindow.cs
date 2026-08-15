using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FreeAction.Rotation;

namespace FreeAction.UI;

/// <summary>
/// FFLogs 一键导入校正点窗口。
/// 用户输入 API client_id / client_secret + report code，
/// 选择战斗 → 查询 boss 敌方施法事件 → 多选 → 导入为 SyncPoint。
/// </summary>
public sealed class FflogsImportWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private Configuration C => _plugin.Config;

    // 输入字段
    private string _apiKey = string.Empty;
    private string _reportUrl = string.Empty;
    private int _presetFightId = -1; // 从链接 #fight=N 解析出来的，用于自动选中
    private string _reportCode = string.Empty; // 从链接解析后的 code，供后续查询施法事件用
    // 0 = cn.fflogs.com, 1 = www.fflogs.com
    private int _domainIdx = 0;
    private static readonly string[] _domainOptions = { "CN (cn.fflogs.com)", "国际 (www.fflogs.com)" };

    // 缓存的客户端
    private FflogsClient? _client;

    // 拉取结果
    private List<FflogsFight> _fights = new();
    private int _selectedFightIdx = -1;
    private List<FflogsCastEvent> _casts = new();
    private readonly HashSet<int> _selectedCastIdx = new();

    // 状态
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;
    private bool _busy;

    public FflogsImportWindow(Plugin plugin) : base("FFLogs 导入校正点###FreeActionFflogsImport")
    {
        _plugin = plugin;
        Size = new Vector2(720, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoScrollbar;
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    public override void OnOpen()
    {
        _errorText = string.Empty;
        _statusText = string.Empty;
    }

    public override void Draw()
    {
        // 顶部说明
        ImGui.TextDisabled("说明: 使用 FFLogs V1 API（REST + API key，比 V2 的 OAuth2 简单）");
        ImGui.TextDisabled("      登录 fflogs 后在个人资料页生成 API key:");
        ImGui.TextDisabled("        国服: https://cn.fflogs.com/profile/");
        ImGui.TextDisabled("        国际: https://www.fflogs.com/profile/");
        ImGui.Separator();

        // ---- 凭据区 ----
        ImGui.TextUnformatted("1. 输入 FFLogs API Key");
        ImGui.SetNextItemWidth(360);
        var key = _apiKey;
        if (ImGui.InputText("API Key##apikey", ref key, 128, ImGuiInputTextFlags.Password))
        {
            _apiKey = key;
            _client = null; // 凭据变更，丢弃缓存的 client
        }
        ImGui.SameLine();
        ImGui.TextDisabled("（在 fflogs 个人资料页生成）");

        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("API 域名##domain", ref _domainIdx, _domainOptions, _domainOptions.Length))
        {
            _client = null; // 域名变更，丢弃缓存的 client
        }
        ImGui.SameLine();
        ImGui.TextDisabled("（用哪个域名上传日志就选哪个）");

        ImGui.SetNextItemWidth(560);
        var url = _reportUrl;
        if (ImGui.InputText("FFLogs 报告链接##url", ref url, 256))
            _reportUrl = url;
        ImGui.SameLine();
        ImGui.TextDisabled("（粘贴整段链接，会自动解析 report code 和战斗 ID）");

        ImGui.Separator();

        // ---- 战斗列表区 ----
        ImGui.TextUnformatted("2. 拉取战斗列表");
        ImGui.SameLine();
        if (ImGui.Button("拉取战斗##btnFights"))
            _ = FetchFightsAsync();
        ImGui.SameLine();
        if (ImGui.Button("清空"))
        {
            _apiKey = _reportUrl = string.Empty;
            _presetFightId = -1;
            _client = null;
            _fights.Clear();
            _casts.Clear();
            _selectedCastIdx.Clear();
            _selectedFightIdx = -1;
        }

        if (_fights.Count > 0)
        {
            var names = _fights.Select(f => f.DisplayLabel).ToArray();
            int sel = Math.Max(0, _selectedFightIdx);
            ImGui.SetNextItemWidth(560);
            if (ImGui.Combo("##FightSel", ref sel, names, names.Length))
            {
                _selectedFightIdx = sel;
                _casts.Clear();
                _selectedCastIdx.Clear();
            }
            ImGui.SameLine();
            if (ImGui.Button("查询 boss 施法##btnCasts"))
                _ = FetchCastsAsync();
        }

        ImGui.Separator();

        // ---- 事件列表区 ----
        ImGui.TextUnformatted($"3. 选择要导入为校正点的 boss 技能 (已选 {_selectedCastIdx.Count}/{_casts.Count})");
        ImGui.SameLine();
        if (ImGui.Button("全选##selAll") && _casts.Count > 0)
        {
            for (int i = 0; i < _casts.Count; i++) _selectedCastIdx.Add(i);
        }
        ImGui.SameLine();
        if (ImGui.Button("全不选##selNone"))
        {
            _selectedCastIdx.Clear();
        }
        ImGui.SameLine();
        if (ImGui.Button("从筛选"))
        {
            // 把含相同技能名的事件都标记为选中
        }

        // 筛选输入框
        ImGui.SetNextItemWidth(280);
        var filter = _filterText;
        if (ImGui.InputText("筛选技能名##filter", ref filter, 64))
            _filterText = filter;

        // 列表
        ImGui.BeginChild("CastList", new Vector2(0, 240), true);
        for (int i = 0; i < _casts.Count; i++)
        {
            var c = _casts[i];
            string label = c.DisplayLabel;
            if (!string.IsNullOrEmpty(_filterText) &&
                !label.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                continue;

            bool sel = _selectedCastIdx.Contains(i);
            if (ImGui.Checkbox($"##cb{i}", ref sel))
            {
                if (sel) _selectedCastIdx.Add(i);
                else _selectedCastIdx.Remove(i);
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(label);
        }
        ImGui.EndChild();

        // ---- 导入按钮 ----
        var profile = _plugin.Engine.ActiveProfile;
        ImGui.TextUnformatted($"目标配置: {profile?.Name ?? "无"} (当前校正点数 {profile?.SyncPoints.Count ?? 0})");

        bool canImport = _selectedCastIdx.Count > 0 && profile != null && !_busy;
        ImGui.BeginDisabled(!canImport);
        if (ImGui.Button($"导入选中 ({_selectedCastIdx.Count}) 为校正点"))
        {
            ImportSelectedToSyncPoints(profile!);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("关闭"))
            IsOpen = false;

        // 状态
        ImGui.Separator();
        if (_busy)
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), $"工作中: {_statusText}");
        }
        else if (!string.IsNullOrEmpty(_errorText))
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), _errorText);
        }
        else if (!string.IsNullOrEmpty(_statusText))
        {
            ImGui.TextColored(new Vector4(0.4f, 1, 0.4f, 1), _statusText);
        }
    }

    private string _filterText = string.Empty;

    /// <summary>
    /// 从 fflogs 链接解析 (reportCode, fightId)。
    /// 支持格式：
    ///   https://cn.fflogs.com/reports/ABCD1234
    ///   https://cn.fflogs.com/reports/ABCD1234#fight=5
    ///   https://cn.fflogs.com/reports/ABCD1234/?fight=5
    ///   https://www.fflogs.com/reports/ABCD1234#fight=5&type=...
    /// 也接受纯 report code（16 位字母数字）。
    /// </summary>
    private static (string code, int fightId) ParseReportUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return ("", -1);
        string s = input.Trim();

        // 先尝试从 URL 提取 fight ID
        int fightId = -1;
        int hashIdx = s.IndexOf('#');
        if (hashIdx >= 0)
        {
            string frag = s.Substring(hashIdx + 1);
            // 在 fragment 中找 fight=数字
            var m = System.Text.RegularExpressions.Regex.Match(frag, @"fight=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int fid)) fightId = fid;
        }
        // 也支持 ?fight= 查询参数
        int qIdx = s.IndexOf('?');
        if (qIdx >= 0 && fightId < 0)
        {
            string qs = s.Substring(qIdx + 1);
            var m = System.Text.RegularExpressions.Regex.Match(qs, @"fight=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int fid)) fightId = fid;
        }

        // 提取 report code：/reports/<code> 之后到 ?# 或结尾
        // 用正则匹配 /reports/ 后的字母数字段
        var mCode = System.Text.RegularExpressions.Regex.Match(s, @"/reports/([A-Za-z0-9]+)");
        if (mCode.Success) return (mCode.Groups[1].Value, fightId);

        // 兜底：如果输入本身就是 16 位字母数字串，当作 code
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z0-9]{16}$")) return (s, fightId);

        return ("", fightId);
    }

    private FflogsClient EnsureClient()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("请先填写 API Key");

        if (_client == null)
            _client = new FflogsClient(_apiKey, useCnDomain: _domainIdx == 0);
        return _client;
    }

    private async Task FetchFightsAsync()
    {
        if (_busy) return;

        // 同步预校验
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _errorText = "请先填写 API Key（在 fflogs 个人资料页生成）";
            _plugin.PrintChatError($"[FreeAction] {_errorText}");
            return;
        }
        var (code, fightId) = ParseReportUrl(_reportUrl);
        if (string.IsNullOrEmpty(code))
        {
            _errorText = "请粘贴 FFLogs 报告链接（如 https://cn.fflogs.com/reports/ABCD1234）";
            _plugin.PrintChatError($"[FreeAction] {_errorText}");
            return;
        }

        _busy = true;
        _statusText = $"拉取战斗列表 (report={code}, domain={(_domainIdx == 0 ? "cn" : "www")})...";
        _errorText = string.Empty;
        _plugin.PrintChat($"[FreeAction] 开始拉取 fflogs 报告 {code} 的战斗列表 (V1 API, {(_domainIdx == 0 ? "cn" : "www")})...");

        try
        {
            _presetFightId = fightId;
            _reportCode = code;
            if (_client == null)
                _client = new FflogsClient(_apiKey, useCnDomain: _domainIdx == 0);

            _fights = await _client.GetFightsAsync(code).ConfigureAwait(true);

            // 默认选择：链接里指定的 fight > 击杀且时长 > 5s 的第一场 > 第一场
            int idx = -1;
            if (_presetFightId > 0)
                idx = _fights.FindIndex(f => f.Id == _presetFightId);
            if (idx < 0)
                idx = _fights.FindIndex(f => f.Kill && f.DurationMs > 5000);
            _selectedFightIdx = idx >= 0 ? idx : (_fights.Count > 0 ? 0 : -1);
            _casts.Clear();
            _selectedCastIdx.Clear();
            _statusText = $"拉取到 {_fights.Count} 场战斗" +
                (_presetFightId > 0 && idx >= 0 ? $"（已自动选中链接指定的 #{_presetFightId}）" : "");
            _plugin.PrintChat($"[FreeAction] ✓ 拉取成功：共 {_fights.Count} 场战斗" +
                (_presetFightId > 0 && idx >= 0 ? $"，已自动选中 #{_presetFightId}" : ""));
        }
        catch (Exception ex)
        {
            _errorText = $"拉取失败: {ex.Message}";
            _statusText = string.Empty;
            _plugin.PrintChatError($"[FreeAction] ✗ 拉取失败: {ex.Message}");
            if (ex.InnerException != null)
                _plugin.PrintChatError($"[FreeAction]   内部异常: {ex.InnerException.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task FetchCastsAsync()
    {
        if (_busy) return;
        if (_selectedFightIdx < 0 || _selectedFightIdx >= _fights.Count)
        {
            _errorText = "请先选择一场战斗";
            return;
        }
        _busy = true;
        var fight = _fights[_selectedFightIdx];
        _statusText = $"查询 boss 施法事件 (fight #{fight.Id} {fight.BossName})...";
        _errorText = string.Empty;
        _plugin.PrintChat($"[FreeAction] 开始查询 fight #{fight.Id} ({fight.BossName}) 的 boss 施法事件...");

        try
        {
            var client = EnsureClient();
            client.DebugStatsCallback = msg => _plugin.PrintChat(msg);
            _casts = await client.GetEnemyCastsAsync(_reportCode, fight.Id, fight.StartTime, fight.EndTime).ConfigureAwait(true);
            _selectedCastIdx.Clear();
            _statusText = $"查询到 {_casts.Count} 个 boss 施法事件";
            _plugin.PrintChat($"[FreeAction] ✓ 查询成功：共 {_casts.Count} 个 boss 施法事件（已过滤玩家）");

            // 如果是 0，自动调试：打印原始响应前 500 字符到聊天框
            if (_casts.Count == 0)
            {
                _plugin.PrintChat("[FreeAction] 调试: 0 事件，开始诊断...");
                try
                {
                    // 测试 1：不带 filter、不带 hostility
                    string raw1 = await client.DebugGetEventsRawAsync(_reportCode, fight.StartTime, fight.EndTime, false).ConfigureAwait(true);
                    _plugin.PrintChat($"[FreeAction] 调试[无filter/hostility] 长度={raw1.Length}, 前300字符:");
                    _plugin.PrintChat("  " + (raw1.Length > 300 ? raw1.Substring(0, 300) + "..." : raw1));

                    // 测试 2：带 hostility=1，不带 filter
                    string raw2 = await client.DebugGetEventsRawAsync(_reportCode, fight.StartTime, fight.EndTime, true).ConfigureAwait(true);
                    _plugin.PrintChat($"[FreeAction] 调试[hostility=1] 长度={raw2.Length}, 前300字符:");
                    _plugin.PrintChat("  " + (raw2.Length > 300 ? raw2.Substring(0, 300) + "..." : raw2));
                }
                catch (Exception dx)
                {
                    _plugin.PrintChatError($"[FreeAction] 调试失败: {dx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _errorText = $"查询失败: {ex.Message}";
            _statusText = string.Empty;
            _plugin.PrintChatError($"[FreeAction] ✗ 查询失败: {ex.Message}");
            if (ex.InnerException != null)
                _plugin.PrintChatError($"[FreeAction]   内部异常: {ex.InnerException.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>把选中的施法事件导入为校正点。</summary>
    private void ImportSelectedToSyncPoints(RotationProfile profile)
    {
        int imported = 0;
        foreach (var idx in _selectedCastIdx.OrderBy(i => i))
        {
            if (idx < 0 || idx >= _casts.Count) continue;
            var c = _casts[idx];
            if (c.GameId <= 0) continue; // 没有 gameID 的没法用作校正

            float ms = (float)c.RelativeMs;
            string hexId = $"{c.GameId:X}";
            string alias = string.IsNullOrEmpty(c.AbilityName) ? $"Boss_{hexId}" : c.AbilityName;
            profile.SyncPoints.Add(new SyncPoint(ms, hexId, alias));
            imported++;
        }
        profile.SyncPoints.Sort((a, b) => a.ExpectedTimeMs.CompareTo(b.ExpectedTimeMs));
        C.Save();
        _statusText = $"已导入 {imported} 个校正点到「{profile.Name}」";
    }
}
