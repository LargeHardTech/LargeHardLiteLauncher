using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>页面标识</summary>
public enum PageId
{
    Launch,
    Download,
    /// <summary>联机（陶瓦联机 / Terracotta）</summary>
    Terracotta,
    /// <summary>启动 JAR（独立跑一个 jar，图形化调参数）</summary>
    JarLaunch,
    Settings,
    More,
}

/// <summary>
/// 页面基类：主窗口内容区的一个页面，弹性填满。
/// </summary>
public abstract class PageBase : View
{
    protected readonly LauncherConfig Config;

    protected PageBase(LauncherConfig config, string title)
    {
        Config = config;
        Title = title;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true; // 切页时焦点可带入本页
    }

    public virtual void OnPageEnter() { }
    public virtual void OnPageLeave() { }
}
