using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 柔和配色方案 (低饱和 / 低对比, 不刺眼)。运行时通过 SchemeManager 覆盖 Base/Dialog/Error/Menu/Accent,
/// 应用后需要通知 UI 清 Scheme 缓存重绘 (订阅 Applied 事件)。
/// </summary>
public static class ColorSchemes
{
    public sealed record Palette(string Id, string Name, Color Bg, Color Fg, Color AltBg, Color Accent);

    public static readonly Palette[] All =
    {
        new("nord", "Nord (北欧蓝)", Rgb(0x2E, 0x34, 0x40), Rgb(0xD8, 0xDE, 0xE9), Rgb(0x3B, 0x42, 0x52), Rgb(0x88, 0xC0, 0xD0)),
        new("solarized-dark", "Solarized Dark", Rgb(0x00, 0x2B, 0x36), Rgb(0x93, 0xA1, 0xA1), Rgb(0x07, 0x36, 0x42), Rgb(0x2A, 0xA1, 0x98)),
        new("gruvbox-soft", "Gruvbox Soft", Rgb(0x32, 0x30, 0x2F), Rgb(0xEB, 0xDB, 0xB2), Rgb(0x3C, 0x38, 0x36), Rgb(0xA8, 0x99, 0x84)),
        new("one-dark-soft", "One Dark Soft", Rgb(0x28, 0x2C, 0x34), Rgb(0xAB, 0xB2, 0xBF), Rgb(0x3E, 0x44, 0x51), Rgb(0x61, 0xAF, 0xEF)),
        new("solarized-light", "Solarized Light", Rgb(0xFD, 0xF6, 0xE3), Rgb(0x58, 0x6E, 0x75), Rgb(0xEE, 0xE8, 0xD5), Rgb(0x26, 0x8B, 0xD2)),
        new("dracula", "Dracula", Rgb(0x28, 0x2A, 0x36), Rgb(0xF8, 0xF8, 0xF2), Rgb(0x44, 0x47, 0x5A), Rgb(0xBD, 0x93, 0xF9)),
        new("tokyo-night", "Tokyo Night", Rgb(0x1A, 0x1B, 0x26), Rgb(0xC0, 0xCA, 0xF5), Rgb(0x24, 0x28, 0x3B), Rgb(0x7A, 0xA2, 0xF7)),
        new("catppuccin-mocha", "Catppuccin Mocha", Rgb(0x1E, 0x1E, 0x2E), Rgb(0xCD, 0xD6, 0xF4), Rgb(0x31, 0x32, 0x44), Rgb(0x89, 0xB4, 0xFA)),
        new("monokai-pro", "Monokai Pro", Rgb(0x2D, 0x2A, 0x2E), Rgb(0xFC, 0xFC, 0xFA), Rgb(0x40, 0x3E, 0x41), Rgb(0xFF, 0xD8, 0x66)),
        new("everforest", "Everforest", Rgb(0x2D, 0x35, 0x3B), Rgb(0xD3, 0xC6, 0xAA), Rgb(0x34, 0x3F, 0x44), Rgb(0xA7, 0xC0, 0x80)),
        new("kanagawa", "Kanagawa", Rgb(0x1F, 0x1F, 0x28), Rgb(0xDC, 0xD7, 0xBA), Rgb(0x2A, 0x2A, 0x37), Rgb(0x7E, 0x9C, 0xD8)),
        new("rose-pine", "Rosé Pine", Rgb(0x19, 0x17, 0x24), Rgb(0xE0, 0xDE, 0xF4), Rgb(0x26, 0x23, 0x3A), Rgb(0xC4, 0xA7, 0xE7)),
    };

    /// <summary>配色应用后触发, UI 用它清 Scheme 缓存 + 重绘</summary>
    public static event Action? Applied;

    private static Color Rgb(int r, int g, int b) => new(r, g, b);

    public static Palette? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>TG 内部 runtime scheme 表 (AddScheme 的覆盖存在这里, 切主题时会被重新盖回去)</summary>
    private static readonly System.Reflection.FieldInfo? RuntimeSchemesField =
        typeof(SchemeManager).GetField("_runtimeSchemes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    /// <summary>应用配色</summary>
    public static void Apply(Palette? palette)
    {
        if (palette is null)
            return;

        foreach (Schemes s in Enum.GetValues<Schemes>())
            SchemeManager.AddScheme(s.ToString(), BuildScheme(palette, s));

        Applied?.Invoke();
    }

    /// <summary>
    /// 清掉配色覆盖, 回到内置主题。
    /// 关键: 不能只 AddScheme 恢复快照, TG 切主题时 ApplyActiveThemeOverlays() 会把
    /// _runtimeSchemes 里的覆盖无条件盖回去, 导致"选了配色后再切主题永远显示旧主题"。
    /// RemoveScheme 又禁止删内置 scheme, 所以直接清空 TG 的私有 runtime 表。
    /// (该字段被 SchemeManager 内部方法引用, NativeAOT 下不会被裁剪)
    /// </summary>
    public static void Clear()
    {
        try
        {
            if (RuntimeSchemesField?.GetValue(null) is System.Collections.IDictionary runtime)
                runtime.Clear();
        }
        catch { /* 反射失败则退化为不清 (仅影响配色回退) */ }

        Applied?.Invoke();
    }

    private static Scheme BuildScheme(Palette p, Schemes kind)
    {
        var normal = new Attribute(p.Fg, p.Bg);
        var hot = new Attribute(p.Accent, p.Bg);
        var focus = new Attribute(p.Bg, p.Accent);          // 焦点: 反色块
        var hotFocus = new Attribute(p.Bg, p.Accent);
        var active = new Attribute(p.Fg, p.AltBg);          // 选中/激活: 稍亮背景
        var hotActive = new Attribute(p.Accent, p.AltBg);
        var highlight = new Attribute(p.Fg, p.AltBg);
        var editable = new Attribute(p.Fg, p.AltBg);
        var readOnly = new Attribute(p.Fg, p.Bg);
        var disabled = new Attribute(Dim(p.Fg), p.Bg);

        return kind switch
        {
            Schemes.Error => new Scheme(new Attribute(p.Fg, p.AltBg))
            {
                Normal = normal,
                Focus = focus,
                HotNormal = hot,
                HotFocus = hotFocus,
                Active = active,
                HotActive = hotActive,
                Highlight = highlight,
                Editable = editable,
                ReadOnly = readOnly,
                Disabled = disabled,
            },
            _ => new Scheme(normal)
            {
                Normal = normal,
                Focus = focus,
                HotNormal = hot,
                HotFocus = hotFocus,
                Active = active,
                HotActive = hotActive,
                Highlight = highlight,
                Editable = editable,
                ReadOnly = readOnly,
                Disabled = disabled,
            },
        };
    }

    /// <summary>把颜色往背景方向调暗, 用于 Disabled</summary>
    private static Color Dim(Color c) =>
        new((byte)((c.R + 0x60) / 2), (byte)((c.G + 0x60) / 2), (byte)((c.B + 0x60) / 2));
}
