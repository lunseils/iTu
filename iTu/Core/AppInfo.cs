using System.Diagnostics;
using System.Reflection;

namespace iTu.Core;

/// <summary>某个正在运行的实例的身份信息。</summary>
public sealed record InstanceInfo(int Pid, string Version, string ExePath, DateTime StartedAt)
{
    /// <summary>是否与指定构建完全一致（版本 + 可执行文件路径）。</summary>
    public bool IsSameBuild(string version, string exePath) =>
        string.Equals(Version, version, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ExePath, exePath, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"版本 {Version} / PID {Pid} / 路径 {ExePath} / 启动于 {StartedAt:HH:mm:ss}";
}

/// <summary>
/// 程序身份与环境信息。
///
/// 为什么要单独一个类：
///   截图工具的"修复没生效"问题，绝大多数情况下不是代码问题，而是
///   **用户实际运行的还是旧的那份 exe**（旧实例常驻托盘并占着全局热键，
///   双击新 exe 只会被单实例逻辑唤醒旧进程）。
///   因此这里提供版本号、进程号、exe 路径与实例标记文件，
///   让"当前到底在跑哪一份构建"变成可查询、可日志、可自证的事实。
/// </summary>
public static class AppInfo
{
    // 标记文件名带当前用户 SID 的短哈希：同一台机器上多用户会话共用临时目录，
    // 通用文件名会互相串扰（A 用户的标记被 B 用户读到 → 误判版本冲突）。
    private static readonly string InstanceFileName =
        "itu-" + StableHash(Environment.UserName) + ".instance";

    private static string StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h.ToString("x8");
        }
    }

    /// <summary>程序版本（取自程序集版本，发布时由 csproj 的 Version 决定）。</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>带前缀的版本号，用于日志与托盘提示。</summary>
    public static string VersionText => "v" + Version;

    /// <summary>项目在 GitHub 上的地址。</summary>
    public const string GitHubUrl = "https://github.com/lunseils/iTu";

    /// <summary>当前进程的可执行文件完整路径。</summary>
    public static string ExePath { get; } =
        Environment.ProcessPath ?? Environment.GetCommandLineArgs().FirstOrDefault() ?? "";

    /// <summary>本进程启动时间。</summary>
    public static DateTime StartTime { get; } = DateTime.Now;

    /// <summary>实例标记文件：由主实例写入，后续启动的进程可据此知道"谁在跑"。</summary>
    public static string InstanceFilePath =>
        Path.Combine(Path.GetTempPath(), InstanceFileName);

    private static string ResolveVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var v = asm.GetName().Version;
        if (v == null) return "1.0.0";
        // 三段式即可：1.0.0.0 → 1.0.0
        return v.Revision > 0 ? v.ToString() : v.ToString(3);
    }

    /// <summary>写入实例标记（失败不影响主流程）。</summary>
    public static void WriteInstanceMarker()
    {
        try
        {
            var text = string.Join('\t',
                Environment.ProcessId.ToString(),
                Version,
                ExePath,
                StartTime.ToString("O"));
            File.WriteAllText(InstanceFilePath, text);
        }
        catch
        {
            // 临时目录不可写时忽略：只影响诊断能力
        }
    }

    /// <summary>删除实例标记（仅当标记确实属于本进程时）。</summary>
    public static void RemoveInstanceMarker()
    {
        try
        {
            var info = TryReadInstanceMarker();
            if (info != null && info.Pid != Environment.ProcessId) return;
            if (File.Exists(InstanceFilePath)) File.Delete(InstanceFilePath);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>读取实例标记；标记不存在或格式损坏时返回 null。</summary>
    public static InstanceInfo? TryReadInstanceMarker()
    {
        try
        {
            if (!File.Exists(InstanceFilePath)) return null;
            var parts = File.ReadAllText(InstanceFilePath).Split('\t');
            if (parts.Length < 4) return null;
            if (!int.TryParse(parts[0], out var pid) || pid <= 0) return null;
            if (!DateTime.TryParse(parts[3], out var started)) started = DateTime.MinValue;
            return new InstanceInfo(pid, parts[1], parts[2], started);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>进程是否仍然存活。</summary>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 结束指定实例（仅在用户明确确认后调用）。
    /// 会校验可执行文件路径，避免误杀同 PID 复用的其它进程。
    /// </summary>
    public static bool TryTerminate(InstanceInfo info, out string? error)
    {
        error = null;
        try
        {
            using var p = Process.GetProcessById(info.Pid);
            if (p.HasExited)
            {
                error = "进程已退出";
                return false;
            }

            // 校验路径，避免 PID 复用导致误杀。
            // 关键：标记里记录了目标路径（knownPath）却读不到实际进程路径时，
            // 绝不能跳过校验直接 Kill——那正是权限不足/受保护进程下误杀同 PID
            // 复用进程的通道。此时改为拒绝并给出明确原因。
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* 权限不足 → 下面按"无法校验"处理 */ }

            bool knownPath = !string.IsNullOrEmpty(info.ExePath) && Path.IsPathFullyQualified(info.ExePath);
            if (knownPath)
            {
                if (string.IsNullOrEmpty(path))
                {
                    error = "无法读取目标进程路径以校验 PID 复用，已取消结束操作（请到任务管理器手动结束）";
                    return false;
                }
                if (!string.Equals(path, info.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    error = "PID 已被其它程序复用，已取消";
                    return false;
                }
            }
            // knownPath=false：旧版本标记没写路径，无校验依据，维持原行为（信任标记直接结束）

            p.Kill();
            if (!p.WaitForExit(3000))
            {
                error = "进程未在限定时间内退出";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
