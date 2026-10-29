using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 账户管理（学 PCL 的多账户）：列表里选一个就切过去，支持新增离线账户、删除。
/// 微软账户的 refresh_token 是登录时自动存下来的，切过去用时自动换新 token。
/// </summary>
public partial class LaunchPage
{
    private void ShowAccountManager()
    {
        // 打开时先把当前登录状态刷进列表（用户可能在设置页改过名字/重新登录过）
        AccountManager.Sync(Config);

        var dlg = new Dialog
        {
            Title = "账户",
            Width = 64,
            Height = 18,
            BorderStyle = LineStyle.Single,
        };

        var items = new ObservableCollection<string>();
        var shown = new List<AccountEntry>();
        var curTexts = new List<string>();

        var list = new ListView<string>
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(5),
        };

        void Rebuild()
        {
            shown = Config.Accounts.ToList();
            curTexts = shown
                .Select(a => $"{(a.Id == Config.CurrentAccountId ? "●" : "○")} {a.DisplayName,-20} {a.KindName}")
                .ToList();
            if (curTexts.Count == 0)
                curTexts.Add("(还没有账户)");
            list.SetSource(new ObservableCollection<string>(curTexts));
            SyncButtons();
        }

        // IsDefault 的按钮自动高度是 3 行（普通按钮 2 行），和旁边按钮并排会突出一行 → 显式钉成 2
        var switchBtn = new Button { Text = "切换", X = 1, Y = Pos.AnchorEnd(3), IsDefault = true, NoPadding = true, Width = 12, Height = 2 };
        var addBtn = new Button { Text = "添加离线", X = Pos.Right(switchBtn) + 2, Y = Pos.AnchorEnd(3), NoPadding = true, Width = 14 };
        var delBtn = new Button { Text = "删除", X = Pos.Right(addBtn) + 2, Y = Pos.AnchorEnd(3), NoPadding = true, Width = 12 };
        var closeBtn = new Button { Text = "关闭", X = Pos.Right(delBtn) + 2, Y = Pos.AnchorEnd(3), NoPadding = true, Width = 12 };

        AccountEntry? Current()
        {
            if (list.SelectedItem is not { } sel)
                return null;
            var idx = curTexts.IndexOf(sel);
            return idx >= 0 && idx < shown.Count ? shown[idx] : null;
        }

        void SyncButtons()
        {
            var cur = Current();
            switchBtn.Enabled = cur is not null && cur.Id != Config.CurrentAccountId;
            delBtn.Enabled = cur is not null;
        }

        void ApplySelected()
        {
            var entry = Current();
            if (entry is null || entry.Id == Config.CurrentAccountId)
                return;
            AccountManager.Apply(Config, entry);
            Config.Save();
            RefreshPlayerLabel();
            Rebuild();
        }

        switchBtn.Accepting += (_, _) => ApplySelected();
        list.Accepted += (_, _) => ApplySelected();
        list.ValueChanged += (_, _) => SyncButtons();

        addBtn.Accepting += (_, _) =>
        {
            PromptOfflineName(name =>
            {
                if (name is null)
                    return;
                var unique = AccountManager.UniqueName(Config, name);
                AccountManager.AddOffline(Config, unique);
                Config.Save();
                RefreshPlayerLabel();
                Rebuild();
            });
        };

        delBtn.Accepting += (_, _) =>
        {
            var entry = Current();
            if (entry is null)
                return;
            var r = MessageBox.Query(Application.Instance, "删除账户",
                $"确定删除账户[{entry.DisplayName}]({entry.KindName})?\n\n" +
                (entry.IsMicrosoft ? "仅删除本地保存的登录凭据,不影响微软账户本身。" : "离线账户删了就没了。"),
                "删除", "取消");
            if (r != 0)
                return;
            AccountManager.Remove(Config, entry);
            Config.Save();
            RefreshPlayerLabel();
            Rebuild();
        };

        closeBtn.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(list, switchBtn, addBtn, delBtn, closeBtn);
        Rebuild();
        list.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>输入离线玩家名</summary>
    private static void PromptOfflineName(Action<string?> onDone)
    {
        var dlg = new Dialog
        {
            Title = "添加离线账户",
            Width = 52,
            Height = 9,
            BorderStyle = LineStyle.Single,
        };
        dlg.Add(new Label { Text = "玩家名(游戏里显示的名字)", X = 1, Y = 1 });
        var field = new TextField { Text = "Steve", X = 1, Y = 2, Width = Dim.Fill(2) };
        var ok = new Button { Text = "添加", X = 1, Y = 5, IsDefault = true };
        var cancel = new Button { Text = "取消", X = Pos.Right(ok) + 2, Y = 5 };

        string? result = null;
        ok.Accepting += (_, _) =>
        {
            var nm = (field.Text ?? "").Trim();
            // 离线名只能是字母数字下划线，长度 3-16（和服务端校验一致）
            if (nm.Length is < 3 or > 16 || !nm.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                MessageBox.ErrorQuery(Application.Instance, "名字不合法",
                    "离线玩家名只能用字母,数字,下划线,长度 3~16。", "好");
                return;
            }
            result = nm;
            dlg.RequestStop();
        };
        cancel.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(field, ok, cancel);
        field.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
        onDone(result);
    }
}
