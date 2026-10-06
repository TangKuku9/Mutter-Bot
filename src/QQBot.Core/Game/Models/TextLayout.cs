using System.Text;

namespace QQBot.Core.Game.Models;

/// <summary>
/// QQ 气泡排版工具。主流手机屏消息气泡一行约容纳 14 个汉字、
/// 24 个小写字母或 18 个大写字母。据此定义"汉字当量"宽度：
/// 全角字符（汉字/全角标点/制表符）= 1.0，小写与数字 = 14/24，
/// 大写与 @ = 14/18，半角标点与空格 = 0.35。
/// Flow() 只对超宽行按 token 贪心折行，续行以全角空格悬挂缩进；
/// 已达标行原样保留，不影响手工排版的面板与表格。
/// </summary>
public static class TextLayout
{
    /// <summary>每行宽度预算（汉字当量）。</summary>
    public const double LineBudget = 14.0;

    private const double Wide = 1.0;
    private const double Lower = 14.0 / 24;
    private const double Upper = 14.0 / 18;
    private const double Narrow = 0.35;

    private const char Indent = '　';

    public static double Width(string s)
    {
        var w = 0.0;
        foreach (var c in s) w += CharWidth(c);
        return w;
    }

    private static double CharWidth(char c)
    {
        if (c is '—' or '…') return Wide;                            // 破折号/省略号按全角
        if (c >= '\u3000') return Wide;                              // CJK 符号、全角标点及汉字
        if (c is >= '\u2460' and <= '\u24FF') return Wide;           // ①②③…
        if (c is >= '\u2500' and <= '\u257F') return Wide;           // ─ ═ 制表符
        if (c is '☑' or '☐') return Wide;
        if (c is >= 'A' and <= 'Z' or '@') return Upper;
        if (c is >= 'a' and <= 'z' or (>= '0' and <= '9')) return Lower;
        return Narrow;                                               // 空格与半角标点
    }

    /// <summary>对多行文本做折行：仅处理超宽行，其余原样保留。</summary>
    /// <param name="text">待排版文本。</param>
    /// <param name="firstLineUsed">首行已被占用（如被动回复自动 @用户 的前缀）的汉字当量宽度。</param>
    public static string Flow(string text, double firstLineUsed = 0) =>
        string.Join("\n", text.Split('\n').Select((line, i) =>
            i == 0 ? WrapLine(line, firstLineUsed) : WrapLine(line, 0)).SelectMany(x => x));

    private static IEnumerable<string> WrapLine(string line, double usedBefore = 0)
    {
        var budget = Math.Max(4, LineBudget - usedBefore); // 首行预算扣除 @前缀占宽，下限保底 4
        if (Width(line) <= budget)
        {
            yield return line;
            yield break;
        }

        var buf = new StringBuilder();
        var used = 0.0;
        var first = true;

        foreach (var token in Tokenize(line))
        {
            var isClosingPunct = token.Text.Length == 1 && ClosingPuncts.Contains(token.Text[0]);
            var effectiveBudget = first ? budget : LineBudget;

            // 放不下且非收尾标点 → 先断行（续行悬挂缩进一格）
            if (used > 0 && used + token.Width > effectiveBudget && !isClosingPunct)
            {
                yield return buf.ToString();
                buf.Clear();
                buf.Append(Indent);
                used = 1.0;
                first = false;
            }

            // 单个 token 连新行都放不下 → 按字符硬切（原子协议标签除外：宁可微溢出也不发出半个标签）
            if (!token.Atomic
                && used + token.Width > (first ? budget : LineBudget) && token.Text.Length > 1)
            {
                foreach (var ch in token.Text)
                {
                    var cw = CharWidth(ch);
                    var b = first ? budget : LineBudget;
                    if (used + cw > b)
                    {
                        yield return buf.ToString();
                        buf.Clear();
                        buf.Append(Indent);
                        used = 1.0;
                        first = false;
                    }
                    buf.Append(ch);
                    used += cw;
                }
                continue;
            }

            buf.Append(token.Text);
            used += token.Width;
        }

        if (buf.Length > 0) yield return buf.ToString();
    }

    private static readonly string ClosingPuncts = "，。！？；、…）】》」』：";

    /// <summary>可跟在数字后构成不可拆“数量链”的单位字，如 5时19分、2分30秒、3天。</summary>
    private const string MeasureUnits = "时分秒天周年月载日";

    /// <summary>QQ 消息内嵌协议标签前缀（&lt;qqbot-at-user/&gt;、&lt;qqbot-cmd-input/&gt; 等）。</summary>
    private const string TagPrefix = "<qqbot-";

    /// <summary>协议标签在气泡里的占宽估值（约等于渲染后的“@昵称”芯片），不可拆也不可硬切。</summary>
    private const double TagWidth = 4.0;

    private static IEnumerable<Token> Tokenize(string line)
    {
        // 先按协议标签切段：标签内部全是 ASCII，归入普通窄字符run 会被当成超长 token
        // 仍按字符硬切，发出半个标签（客户端会直接显示成乱码）。
        var tokens = new List<Token>();
        var pos = 0;
        while (pos < line.Length)
        {
            var tagAt = line.IndexOf(TagPrefix, pos, StringComparison.Ordinal);
            if (tagAt < 0)
            {
                AppendRunTokens(line.Substring(pos), tokens);
                break;
            }
            if (tagAt > pos) AppendRunTokens(line.Substring(pos, tagAt - pos), tokens);

            var close = line.IndexOf('>', tagAt);
            if (close < 0)
            {
                AppendRunTokens(line[tagAt..], tokens);        // 残缺标签按普通文本处理
                break;
            }
            var length = close - tagAt + 1;
            tokens.Add(new Token(line.Substring(tagAt, length), TagWidth, Atomic: true));
            pos = tagAt + length;
        }
        return MergeMeasureChains(tokens);
    }

    /// <summary>窄/宽字符分组分词，收尾标点并入前一个 token（无标签文本行为与历史一致）。</summary>
    private static void AppendRunTokens(string text, List<Token> tokens)
    {
        var sb = new StringBuilder();
        var narrowRun = false;

        foreach (var ch in text)
        {
            var narrow = CharWidth(ch) < Wide;
            // 收尾标点并入前一个 token，断行不拆词、标点不落行首
            var merging = sb.Length > 0 && ClosingPuncts.Contains(ch);
            if (sb.Length > 0 && !merging && narrow != narrowRun)
            {
                var run = sb.ToString();
                tokens.Add(new Token(run, Width(run)));
                sb.Clear();
            }
            narrowRun = narrow;
            sb.Append(ch);
        }

        if (sb.Length > 0)
        {
            var run = sb.ToString();
            tokens.Add(new Token(run, Width(run)));
        }
    }

    /// <summary>二次合并：“数量链”不可从中间截断（防止“5时19 / 分。”式断裂）。</summary>
    private static List<Token> MergeMeasureChains(List<Token> tokens)
    {
        var merged = new List<Token>(tokens.Count);
        foreach (var t in tokens)
        {
            if (merged.Count > 0 && !t.Atomic && CanExtendMeasureChain(merged[^1], t))
            {
                var text = merged[^1].Text + t.Text;
                merged[^1] = merged[^1] with { Text = text, Width = Width(text) };
                continue;
            }
            merged.Add(t);
        }
        return merged;
    }

    private static bool CanExtendMeasureChain(Token prev, Token cur)
    {
        if (prev.Text.Length == 0) return false;
        var tail = prev.Text[^1];
        var chainOpen = char.IsAsciiDigit(tail) || MeasureUnits.Contains(tail);
        if (!chainOpen) return false;

        // ASCII 字母数字段可沿链吸附（如“19”）；全角段仅接受“单位字+可选收尾标点”
        if (CharWidth(cur.Text[0]) < Wide)
            return cur.Text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ',');

        var core = cur.Text.TrimEnd(ClosingPuncts.ToArray());
        return core.Length is >= 1 and <= 2 && core.All(MeasureUnits.Contains);
    }

    private readonly record struct Token(string Text, double Width, bool Atomic = false);
}
