using System.Diagnostics;

namespace QqShot.Ui;

/// <summary>
/// 转发（分享）能力：菜单、复制、拉起外部应用、邮件。
///
/// 与编辑器解耦：只依赖一张"取图"委托，不关心图片从哪来，
/// 便于复用（编辑器、钉图窗口都能用）。
///
/// 没有接任何厂商 SDK —— 做法是"复制到剪贴板 + 尽量拉起目标程序"，
/// 任何聊天窗口都能 Ctrl+V，也不需要企业资质。
/// </summary>
public static class ShareService
{
    /// <summary>在指定屏幕位置弹出转发菜单。</summary>
    /// <param name="owner">用于弹出 Toast 的宿主窗口。</param>
    /// <param name="origin">菜单左上角的屏幕坐标。</param>
    /// <param name="render">取当前图片（调用时才渲染，避免无谓开销）。</param>
    /// <param name="onStatus">状态回调（写日志 / 状态栏）。</param>
    /// <param name="onSaveAs">"另存为"交由调用方处理。</param>
    public static CapsuleMenu ShowMenu(IWin32Window owner, Point origin,
        Func<Bitmap> render, Action<string> onStatus, Action onSaveAs)
    {
        return CapsuleMenu.Popup(origin, new[]
        {
            new CapsuleMenu.MenuItem("转发到微信", IconKind.Share, () => SendTo(owner, render, "微信", onStatus)),
            new CapsuleMenu.MenuItem("转发到 QQ", IconKind.Share, () => SendTo(owner, render, "QQ", onStatus)),
            new CapsuleMenu.MenuItem("用邮件发送", IconKind.Mail, () => SendByMail(owner, render, onStatus)),
            CapsuleMenu.MenuItem.Separator,
            new CapsuleMenu.MenuItem("复制图片", IconKind.Copy, () =>
            {
                try
                {
                    using var image = render();
                    Clipboard.SetImage(image);
                    Toast.Show(owner, "已复制图片", Theme.Success);
                    onStatus("已复制图片");
                }
                catch (Exception ex)
                {
                    Toast.Show(owner, "复制失败：" + ex.Message, Theme.Danger);
                }
            }),
            new CapsuleMenu.MenuItem("另存为图片…", IconKind.Save, onSaveAs),
        });
    }

    /// <summary>复制图片并尝试拉起目标程序；拉不起来就只复制并提示手动粘贴。</summary>
    private static void SendTo(IWin32Window owner, Func<Bitmap> render, string app, Action<string> onStatus)
    {
        try
        {
            using (var image = render())
                Clipboard.SetImage(image);

            var exe = app == "微信"
                ? FindApp("Weixin.exe", "WeChat.exe")
                : FindApp("QQ.exe");

            if (exe != null)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Toast.Show(owner, $"已复制图片并打开{app}，在聊天窗口 Ctrl+V 即可", Theme.Success);
            }
            else
            {
                Toast.Show(owner, $"图片已复制，打开{app}后按 Ctrl+V 粘贴", Theme.Warning);
            }

            onStatus($"已转发到{app}");
        }
        catch (Exception ex)
        {
            Toast.Show(owner, "转发失败：" + ex.Message, Theme.Danger);
        }
    }

    /// <summary>邮件转发：图片存到临时文件，再拉起默认邮件客户端（正文带上文件路径）。</summary>
    private static void SendByMail(IWin32Window owner, Func<Bitmap> render, Action<string> onStatus)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "iTu");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"iTu-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            using (var image = render())
            {
                image.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                Clipboard.SetImage(image);
            }

            var body = Uri.EscapeDataString("截图已保存到：" + file);
            Process.Start(new ProcessStartInfo($"mailto:?subject=截图&body={body}") { UseShellExecute = true });

            // 邮件只是"拖入附件"的中转，不能永久堆积在 %TEMP%\iTu。
            // 给邮件客户端留出拖入时间后自动清理；失败也只记日志。
            _ = new System.Threading.Timer(_ =>
            {
                try { File.Delete(file); }
                catch (Exception ex) { Core.Log.Info($"[share] 清理临时邮件图片失败: {ex.Message}"); }
            }, null, TimeSpan.FromMinutes(10), System.Threading.Timeout.InfiniteTimeSpan);

            Toast.Show(owner, "已保存并打开邮件，附件请拖入：" + Path.GetFileName(file), Theme.Success);
            onStatus("邮件转发");
        }
        catch (Exception ex)
        {
            Toast.Show(owner, "邮件转发失败：" + ex.Message, Theme.Danger);
        }
    }

    /// <summary>在常见安装位置找目标程序（找不到返回 null，调用方降级为只复制）。</summary>
    private static string? FindApp(params string[] names)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        var subs = new[] { @"Tencent\Weixin", @"Tencent\WeChat", @"Tencent\QQNT", @"Tencent\QQ" };

        foreach (var name in names)
            foreach (var root in roots)
                foreach (var sub in subs)
                {
                    var path = Path.Combine(root, sub, name);
                    if (File.Exists(path)) return path;
                }

        return null;
    }
}
