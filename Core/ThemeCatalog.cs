using Terminal.Gui.Configuration;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 统一的主题/配色目录: 内置 Terminal.Gui 主题 + 自定义柔和配色, 合成一个可选列表。
/// </summary>
public static class ThemeCatalog
{
    public sealed record Entry(string Display, string? Builtin, string? PaletteId);

    /// <summary>构建完整列表: 内置 TG 主题 + 自定义配色 (配色以 Theme 形式呈现, 选中即整界面套用)</summary>
    public static List<Entry> Build()
    {
        var list = new List<Entry>();
        foreach (var name in ThemeManager.GetThemeNames())
            list.Add(new Entry(name, name, null));
        foreach (var p in ColorSchemes.All)
            list.Add(new Entry(p.Name, null, p.Id));
        return list;
    }

    /// <summary>根据当前配置找选中项索引</summary>
    public static int IndexOf(List<Entry> catalog, string theme, string paletteId)
    {
        var palette = ColorSchemes.Find(paletteId);
        if (palette is not null)
        {
            var i = catalog.FindIndex(e => e.PaletteId == palette.Id);
            if (i >= 0)
                return i;
        }
        var t = catalog.FindIndex(e => e.Builtin == theme);
        return t >= 0 ? t : 0;
    }
}
