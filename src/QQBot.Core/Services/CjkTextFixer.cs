using System.Text;
using System.Text.RegularExpressions;

namespace QQBot.Core.Services;

/// <summary>
/// 清理上游代理按显示列宽硬折行造成的断行与空格。
/// faroapi→gemini 这条链路会在长中文字符串中间插入裸换行（甚至插进气球表情括号里，
/// 线上出现过 "(ᗜ\n˰ᗜ)"、"我这转半\n天都打不开"），还会在行首补空格、行尾留空行。
/// 判断依据是"位置像不像自然段落边界"，不是按语言：折断点两侧常常是 ᗜ ˰ 这类非 CJK 字符。
/// </summary>
public static class CjkTextFixer
{
    /// <summary>看起来是新的一行内容（列表、菜单、分栏）而不是被折断的半句话。</summary>
    private static readonly Regex StructureStart = new(
        @"^[\t ]*(?:[-*·•>│|▪◦]|\d+[.)、:：]|\[[^\]]{1,14}\]|【)", RegexOptions.Compiled);

    /// <summary>两个汉字/中文标点之间的空格：中文里不存在，全是噪声。</summary>
    private static readonly Regex SpaceBetweenCjk = new(
        @"(?<=[\u3400-\u9fff\u3000-\u303f\uff00-\uffef])[ \t]+(?=[\u3400-\u9fff\u3000-\u303f\uff00-\uffef])",
        RegexOptions.Compiled);

    public static string Cleanse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var blocks = new List<string>();
        var current = new List<string>();

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    blocks.Add(string.Join("\n", current));
                    current.Clear();
                }
                continue;
            }

            if (current.Count == 0)
            {
                current.Add(line);
                continue;
            }

            var prev = current[^1];
            // 空行分隔、列表起始、冒号引出下一行 —— 这些是人（或提示词模板）真要换行的地方
            if (StructureStart.IsMatch(line) || prev.EndsWith('：') || prev.EndsWith(':'))
                current.Add(line);
            else
                current[^1] = Splice(prev, line);
        }

        if (current.Count > 0) blocks.Add(string.Join("\n", current));

        return SpaceBetweenCjk.Replace(string.Join("\n\n", blocks), string.Empty).Trim();
    }

    /// <summary>接回被折断的半句话：两侧都是 ASCII 字词时补一个空格，否则直接贴回去。</summary>
    private static string Splice(string prev, string next)
    {
        if (prev.Length == 0) return next;
        if (next.Length == 0) return prev;

        var needsSpace = IsWordChar(prev[^1]) && IsWordChar(next[0]);
        return needsSpace ? prev + " " + next : prev + next;
    }

    private static bool IsWordChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || char.IsDigit(c);
}
