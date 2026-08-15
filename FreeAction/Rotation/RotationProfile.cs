using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeAction.Rotation;

/// <summary>
/// 技能池条目：技能名 + ActionId。文本编辑时间轴时按名字引用。
/// </summary>
[Serializable]
public class SkillDef
{
    /// <summary>技能名（必须唯一，文本编辑时按此引用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>技能（Action）ID。</summary>
    public uint ActionId { get; set; }

    public SkillDef() { }

    public SkillDef(string name, uint actionId)
    {
        Name = name;
        ActionId = actionId;
    }

    public override string ToString() => $"{Name} (#{ActionId})";
}

/// <summary>
/// 时间轴条目：在指定时间（毫秒）释放指定技能。
/// </summary>
[Serializable]
public class TimelineEntry
{
    /// <summary>触发时间，单位毫秒。0 = 战斗开始立即释放。</summary>
    public float TimeMs { get; set; } = 0f;

    /// <summary>技能（Action）ID。</summary>
    public uint ActionId { get; set; }

    /// <summary>可选别名，仅用于 UI 显示。</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>是否已在本轮时间轴中触发过（运行时字段，不序列化）。</summary>
    [NonSerialized] public bool Fired;

    public TimelineEntry() { }

    public TimelineEntry(float timeMs, uint actionId, string alias = "")
    {
        TimeMs = timeMs;
        ActionId = actionId;
        Alias = alias;
    }

    public override string ToString()
        => string.IsNullOrEmpty(Alias) ? $"#{ActionId} @ {FormatTime(TimeMs)}" : $"{Alias}(#{ActionId}) @ {FormatTime(TimeMs)}";

    public static string FormatTime(float ms)
    {
        float s = ms / 1000f;
        int mm = (int)(s / 60);
        float ss = s - mm * 60;
        return $"{mm:00}:{ss:00.0}";
    }
}

/// <summary>
/// 时间轴校正点：当检测到指定 boss 技能开始释放时，
/// 将当前时间轴时间校正为 <see cref="ExpectedTimeMs"/>。
/// </summary>
[Serializable]
public class SyncPoint
{
    /// <summary>期望的 boss 技能开始释放时间（毫秒）。</summary>
    public float ExpectedTimeMs { get; set; } = 0f;

    /// <summary>boss 技能（Action）ID（十六进制字符串，如 "C403"）。</summary>
    public string BossActionId { get; set; } = "";

    /// <summary>可选的别名。</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>是否已在本轮中触发过校正（运行时字段）。</summary>
    [NonSerialized] public bool Fired;

    public SyncPoint() { }

    public SyncPoint(float expectedTimeMs, string bossActionId, string alias = "")
    {
        ExpectedTimeMs = expectedTimeMs;
        BossActionId = bossActionId;
        Alias = alias;
    }

    /// <summary>解析 boss 技能 ID。支持十进制和十六进制（"C403" 或 0xC403）。</summary>
    public uint ParseBossActionId()
    {
        if (string.IsNullOrWhiteSpace(BossActionId)) return 0;
        var s = BossActionId.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : 0;
        if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var hx)) return hx;
        return uint.TryParse(s, out var d) ? d : 0;
    }

    public override string ToString()
        => $"{Alias}({BossActionId}) @ {TimelineEntry.FormatTime(ExpectedTimeMs)}";
}

/// <summary>
/// 一套完整的时间轴配置。可绑定到特定职业，也可通用。
/// </summary>
[Serializable]
public class RotationProfile
{
    public string Name { get; set; } = "New Profile";

    /// <summary>绑定的职业 ID，0 表示通用（所有职业生效）。</summary>
    public uint JobId { get; set; } = 0;

    /// <summary>技能池：本配置用到的技能名 + ActionId。文本编辑时间轴时按名字引用。</summary>
    public List<SkillDef> Skills { get; set; } = new();

    /// <summary>时间轴条目列表（按时间升序触发）。</summary>
    public List<TimelineEntry> Entries { get; set; } = new();

    /// <summary>时间轴校正点列表（boss 技能用于校正时间）。</summary>
    public List<SyncPoint> SyncPoints { get; set; } = new();

    /// <summary>该配置是否启用。</summary>
    public bool Enabled { get; set; } = true;

    public RotationProfile() { }

    public RotationProfile(string name) => Name = name;

    /// <summary>按时间升序返回时间轴条目。</summary>
    public IEnumerable<TimelineEntry> OrderedEntries
        => Entries.OrderBy(e => e.TimeMs);

    /// <summary>按期望时间升序返回校正点。</summary>
    public IEnumerable<SyncPoint> OrderedSyncPoints
        => SyncPoints.OrderBy(s => s.ExpectedTimeMs);

    /// <summary>按技能名查找技能池中的 ActionId，找不到返回 0。</summary>
    public uint FindActionIdByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var sd = Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        return sd?.ActionId ?? 0;
    }

    /// <summary>
    /// 解析文本格式的时间轴，每行格式形如：
    ///   00:00.0 "&lt;月光&gt;~"
    ///   00:00.7 "&lt;意气冲天&gt;~"
    /// 时间格式为 mm:ss.f，&lt;&gt; 内为技能名（在技能池中查找 ActionId）。
    /// 解析后会替换当前 Entries。
    /// </summary>
    /// <returns>解析失败的行号列表（从 1 开始）。</returns>
    public List<int> ParseTimelineText(string text)
    {
        var badLines = new List<int>();
        var newEntries = new List<TimelineEntry>();
        if (string.IsNullOrEmpty(text)) return badLines;

        // 匹配：分:秒[.小数] 然后任意字符直到 <技能名>，再直到 ~
        var regex = new Regex(
            @"^\s*(\d+)\s*:\s*(\d+(?:\.\d+)?)\s+.*?<([^>]+?)>.*?~.*$",
            RegexOptions.Compiled);

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = regex.Match(line);
            if (!m.Success)
            {
                badLines.Add(i + 1);
                continue;
            }

            if (!int.TryParse(m.Groups[1].Value, out int mm)) { badLines.Add(i + 1); continue; }
            if (!float.TryParse(m.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float ss)) { badLines.Add(i + 1); continue; }
            string skillName = m.Groups[3].Value.Trim();

            float totalMs = (mm * 60f + ss) * 1000f;
            uint actionId = FindActionIdByName(skillName);
            newEntries.Add(new TimelineEntry(totalMs, actionId, skillName));
        }

        newEntries.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        Entries = newEntries;
        return badLines;
    }

    /// <summary>把当前 Entries 导出为可重新解析的文本格式。</summary>
    public string ExportTimelineText()
    {
        var sb = new StringBuilder();
        foreach (var e in OrderedEntries)
        {
            string name = string.IsNullOrEmpty(e.Alias) ? $"#{e.ActionId}" : e.Alias;
            sb.AppendLine($"{TimelineEntry.FormatTime(e.TimeMs)} \"<{name}>~\"");
        }
        return sb.ToString();
    }

    /// <summary>创建一个示例时间轴配置。</summary>
    public static RotationProfile CreateDefault()
    {
        var p = new RotationProfile("默认示例")
        {
            JobId = 0,
            Enabled = true,
            Skills = new List<SkillDef>
            {
                new SkillDef("月光", 36963),
                new SkillDef("意气冲天", 7521),
            },
            Entries = new List<TimelineEntry>
            {
                new TimelineEntry(0f, 36963, "月光"),
                new TimelineEntry(700f, 7521, "意气冲天"),
            },
            SyncPoints = new List<SyncPoint>(),
        };
        return p;
    }
}
