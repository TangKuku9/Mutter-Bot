using System.Globalization;
using System.Reflection;

namespace Mutter.Core;

/// <summary>
/// 应用版本号工具。发布版本采用日期命名（YYMMDD，例如 261109），程序集版本每段
/// 上限 65535，日期版本由 AssemblyInformationalVersion 承载；System.Version 又至少
/// 要求 major.minor，因此清单与标签的纯日期值在这里统一补齐。更新检查、清单校验、
/// 状态接口与面板展示全部走这一处，避免「纯日期 / 补零」两套口径漂移；
/// 旧格式 x.y.z 继续兼容。
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// 当前运行版本：读取 AssemblyInformationalVersion（261109+commit → 261109）。
    /// 程序集版本每段上限 65535，日期版本无法写进 AssemblyVersion，故以信息版本为准；
    /// 缺失时回退到程序集版本（旧版 x.y.z 仍显示为 2.4.0）。
    /// </summary>
    public static string Current
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly();
            var informational = assembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational)) return Normalize(informational);

            var version = assembly?.GetName().Version;
            return version is null ? "unknown" : Format(version);
        }
    }

    /// <summary>
    /// 归一化版本文本：去掉 v 前缀与预发布/VCS 后缀，并把日期版本的补零段去掉
    ///（261109.0.0 → 261109）。无法识别时原样返回，交由调用方决定是否报错。
    /// </summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        if (text.Length == 0) return string.Empty;
        var core = text.Split('-', '+')[0];
        return TryParseCore(core, out var version) ? Format(version) : text;
    }

    /// <summary>
    /// 解析用于比较的版本。纯数字按日期版本补成 261109.0.0 再交给 System.Version；
    /// 返回 false 表示无法识别（更新检查据此报错，不做猜测）。
    /// </summary>
    public static bool TryParse(string? value, out Version version)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return TryParseCore(text, out version);
    }

    private static bool TryParseCore(string text, out Version version)
    {
        version = new Version(0, 0);
        if (text.Length == 0) return false;
        // System.Version 至少要求 major.minor；纯日期版本补零对齐。
        if (text.All(char.IsAsciiDigit)) text += ".0.0";
        return Version.TryParse(text, out version!);
    }

    /// <summary>
    /// 展示格式：日期版本（六位 major）去掉尾部 .0 段；旧格式沿用 ToString(3)，
    /// 与历史版本「2.4.0」的展示完全一致。
    /// </summary>
    private static string Format(Version version)
    {
        if (version.Major < 100000)
            return version.Build < 0 ? version.ToString(2) : version.ToString(3);

        var text = version.Major.ToString(CultureInfo.InvariantCulture);
        if (version.Minor > 0) text += "." + version.Minor.ToString(CultureInfo.InvariantCulture);
        if (version.Build > 0) text += "." + version.Build.ToString(CultureInfo.InvariantCulture);
        if (version.Revision > 0) text += "." + version.Revision.ToString(CultureInfo.InvariantCulture);
        return text;
    }
}
