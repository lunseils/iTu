using System.Diagnostics;
using System.Drawing.Imaging;
using iTu.Core;

namespace iTu.Core;

public enum RecordState
{
    Idle,
    Recording,
    Paused,
    Stopping
}

/// <summary>
/// 屏幕录制。
///
/// 编码后端优先级：
///   1. ffmpeg（若系统 PATH 中存在）—— 同时支持 GIF 与 MP4，兼容性最佳
///   2. 内置 GIF89a 编码器（纯托管，无外部依赖）
///
/// 帧数据以原始 RGB 管道推给 ffmpeg，避免中间落盘。
///
/// 抓帧跑在**独立线程**上（不用 WinForms Timer）：录制面板要能在后台
/// 停止/暂停/取消，而 UI 定时器只能在自己的消息泵里启停，跨线程 Stop 是不安全的；
/// 独立线程也让"界面卡一下"不再丢帧。
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _gate = new();

    private Process? _ffmpeg;

    private readonly List<Bitmap> _frames = new();
    // 保护 _frames：采集线程（Tick）与 Stop/Cancel/Dispose（主线程）并发读写同一集合。
    private readonly object _framesGate = new();
    private Bitmap? _frameBuffer;
    private byte[]? _pixelBuffer;

    private Thread? _capture;
    private volatile bool _captureStop;

    private DateTime _start = DateTime.Now;
    private TimeSpan _pausedTotal;
    private DateTime? _pausedAt;

    private volatile RecordState _state = RecordState.Idle;

    public Rectangle Region { get; }

    /// <summary>实际使用的帧率（面板上如实展示）。</summary>
    public int Fps { get; }

    public RecordState State => _state;

    /// <summary>当前实际生效的编码后端（面板上如实展示，避免用户以为在录 MP4）。</summary>
    public string EncoderName { get; private set; } = "内置 GIF";

    public int FrameCount => Volatile.Read(ref _captured);
    private int _captured;

    public string? OutputPath { get; private set; }

    /// <summary>已录制时长（暂停期间不计入）。</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                var end = _pausedAt ?? DateTime.Now;
                var span = end - _start - _pausedTotal;
                return span < TimeSpan.Zero ? TimeSpan.Zero : span;
            }
        }
    }

    public ScreenRecorder(AppSettings settings, Rectangle region)
    {
        _settings = settings;
        Region = region;
        Fps = Math.Clamp(settings.RecordFps, 1, 60);
    }

    /// <summary>
    /// 送给 ffmpeg 的偶数尺寸。H.264/yuv420p 要求宽高为偶数；
    /// 采样与 -s 必须用同一个尺寸，否则奇数宽高会帧长不匹配、输出花屏。
    /// </summary>
    private int EncodeWidth => Math.Max(2, Region.Width - (Region.Width % 2));
    private int EncodeHeight => Math.Max(2, Region.Height - (Region.Height % 2));

    public void Start()
    {
        lock (_gate)
        {
            if (_state != RecordState.Idle) return;

            _frames.Clear();
            Volatile.Write(ref _captured, 0);
            _gifMaxFrames = int.MaxValue;
            _gifTrimWarned = false;

            bool started;
            if (_settings.RecordFormat == RecordFormat.Mp4)
            {
                started = TryStartFfmpeg(true);
                EncoderName = started ? "ffmpeg MP4" : "内置 GIF（未找到 ffmpeg）";
            }
            else
            {
                started = TryStartFfmpeg(false);
                EncoderName = started ? "ffmpeg GIF" : "内置 GIF";
            }

            _start = DateTime.Now;
            _pausedTotal = TimeSpan.Zero;
            _pausedAt = null;
            _state = RecordState.Recording;
            _captureStop = false;

            _capture = new Thread(CaptureLoop) { IsBackground = true, Name = "iTu.Capture" };
            _capture.Start();

            Log.Info($"[record] 开始 region={Region} 编码={EncoderName} " +
                     $"fps={Fps}");
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state != RecordState.Recording) return;
            _state = RecordState.Paused;
            _pausedAt = DateTime.Now;
            Log.Info("[record] 暂停");
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != RecordState.Paused) return;
            _pausedTotal += DateTime.Now - (_pausedAt ?? DateTime.Now);
            _pausedAt = null;
            _state = RecordState.Recording;
            Log.Info("[record] 继续");
        }
    }

    /// <summary>固定节拍的抓帧循环（暂停时只空转，不采样）。</summary>
    private void CaptureLoop()
    {
        int fps = Fps;
        int frameMs = Math.Max(10, 1000 / fps);
        var clock = Stopwatch.StartNew();
        long next = 0;

        while (!_captureStop)
        {
            if (_state == RecordState.Recording)
            {
                try { Tick(); }
                catch (Exception ex) { Log.Info("[record] 抓帧异常: " + ex.Message); }
            }

            next += frameMs;
            long wait = next - clock.ElapsedMilliseconds;
            if (wait > 0)
            {
                try { Thread.Sleep((int)wait); } catch { }
            }
            else
            {
                // 落后太多就直接对齐到当前时刻，避免补帧风暴
                next = clock.ElapsedMilliseconds;
            }
        }
    }

    private void Tick()
    {
        int w = EncodeWidth;
        int h = EncodeHeight;
        int frameBytes = w * h * 4;

        if (_frameBuffer == null)
            _frameBuffer = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        try
        {
            using (var g = Graphics.FromImage(_frameBuffer))
                g.CopyFromScreen(Region.Left, Region.Top, 0, 0, _frameBuffer.Size, CopyPixelOperation.SourceCopy);
        }
        catch
        {
            return;   // 屏幕锁定 / 切换分辨率等瞬时失败，跳过该帧
        }

        if (_ffmpeg != null)
        {
            // 直接取 BGRA 原始字节交给 ffmpeg（不再逐像素转 BGR24）
            if (_pixelBuffer == null || _pixelBuffer.Length < frameBytes)
                _pixelBuffer = new byte[frameBytes];

            var data = _frameBuffer.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // 32bppArgb 的 stride 恒等于 width*4，可整块拷贝
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0, _pixelBuffer, 0, Math.Min(frameBytes, data.Stride * h));
            }
            finally
            {
                _frameBuffer.UnlockBits(data);
            }

            QueueFrame(_pixelBuffer, frameBytes);
        }
        else
        {
            var copy = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(copy))
                g.DrawImageUnscaled(_frameBuffer, 0, 0);

            // 内置 GIF 编码需要把帧全部留在内存里，必须设上限：
            // 否则录一分钟后内存就按 GB 增长（旧实现没有任何上限）。
            if (_gifMaxFrames == int.MaxValue)
            {
                _gifMaxFrames = (int)Math.Max(30,
                    Math.Min(2000, MaxGifBytes / Math.Max(1L, (long)w * h * 4)));
            }

            Bitmap? trimTarget = null;
            lock (_framesGate)
            {
                while (_frames.Count >= _gifMaxFrames)
                {
                    trimTarget = _frames[0];
                    _frames.RemoveAt(0);
                    if (!_gifTrimWarned)
                    {
                        _gifTrimWarned = true;
                        Log.Info($"[record] 内置 GIF 编码内存达到上限（{_gifMaxFrames} 帧），后续丢弃最旧帧");
                    }
                }
                _frames.Add(copy);
            }
            trimTarget?.Dispose();
        }

        Interlocked.Increment(ref _captured);
    }

    /// <summary>内置 GIF 编码允许占用的内存上限。</summary>
    private const long MaxGifBytes = 384L * 1024 * 1024;

    private int _gifMaxFrames = int.MaxValue;
    private bool _gifTrimWarned;

    // ---- 后台写管道：避免 ffmpeg 反压卡住采集线程 ----

    private readonly Queue<byte[]> _writeQueue = new();
    private readonly object _queueGate = new();
    private Thread? _writer;
    private volatile bool _writerStop;

    /// <summary>队列上限，超出则丢弃最旧帧（保流畅，不保完整）。</summary>
    private const int MaxQueuedFrames = 8;

    private void QueueFrame(byte[] buffer, int length)
    {
        EnsureWriter();

        var copy = new byte[length];
        Buffer.BlockCopy(buffer, 0, copy, 0, length);

        lock (_queueGate)
        {
            while (_writeQueue.Count >= MaxQueuedFrames)
                _writeQueue.Dequeue();      // 丢最旧的，防止内存膨胀
            _writeQueue.Enqueue(copy);
            Monitor.Pulse(_queueGate);
        }
    }

    private void EnsureWriter()
    {
        if (_writer != null) return;

        _writerStop = false;
        _writer = new Thread(() =>
        {
            while (true)
            {
                byte[]? frame = null;
                lock (_queueGate)
                {
                    if (_writeQueue.Count == 0)
                    {
                        if (_writerStop) return;
                        Monitor.Wait(_queueGate, 50);
                        if (_writeQueue.Count == 0)
                        {
                            if (_writerStop) return;
                            continue;
                        }
                    }
                    frame = _writeQueue.Dequeue();
                }

                try
                {
                    // ffmpeg 用 -i pipe:0 读原始帧 —— 必须写 StandardInput。
                    // 旧代码写的是 StandardOutput（只读流），首次写入即抛异常，
                    // 于是录制在第一帧就静默停止。
                    var stdin = _ffmpeg?.StandardInput.BaseStream;
                    if (stdin == null) return;
                    stdin.Write(frame, 0, frame.Length);
                    stdin.Flush();
                }
                catch (Exception ex)
                {
                    // 写管道失败（ffmpeg 崩溃或 Stop 已关管道）。只记录并结束写线程，
                    // 不在这里改 _state：_state 由 Start/Stop/Cancel 在 lock(_gate) 内统一管理，
                    // 写线程越权改写会与 Stop 的 Idle 写入竞态，可能把状态卡死在 Stopping 导致无法再次录制。
                    Log.Info("[record] 写管道失败，写线程退出: " + ex.Message);
                    return;
                }
            }
        })
        { IsBackground = true, Name = "iTu.VideoWriter" };

        _writer.Start();

        // ffmpeg 会把进度/告警写到 stderr；没人读的话管道写满后 ffmpeg 会卡死
        if (_ffmpeg == null) return;

        _ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) Log.Info("[ffmpeg] " + e.Data);
        };
        _ffmpeg.BeginErrorReadLine();
    }

    /// <summary>停止采集并让写线程把队列写空（尾帧不能丢）。</summary>
    private void StopWriter()
    {
        _writerStop = true;
        lock (_queueGate) Monitor.PulseAll(_queueGate);

        var w = _writer;
        try { w?.Join(4000); } catch { }
        _writer = null;

        int dropped;
        lock (_queueGate)
        {
            dropped = _writeQueue.Count;
            _writeQueue.Clear();
        }
        if (dropped > 0)
            Log.Info($"[record] 写管道超时，丢弃 {dropped} 帧尾帧");
    }

    private void StopCapture()
    {
        _captureStop = true;
        var t = _capture;
        _capture = null;
        try { t?.Join(2000); } catch { }
    }

    /// <summary>停止录制并落盘，返回输出文件路径（无有效画面时返回 null）。</summary>
    public string? Stop()
    {
        lock (_gate)
        {
            if (_state is not (RecordState.Recording or RecordState.Paused)) return null;
            _state = RecordState.Stopping;
        }

        StopCapture();
        StopWriter();

        // ffmpeg：先关管道让它把文件写完；超时就强杀，但文件仍按字节数校验
        var proc = _ffmpeg;
        _ffmpeg = null;
        if (proc != null)
        {
            try { proc.StandardInput.Close(); } catch { }
            try
            {
                if (!proc.WaitForExit(10000)) { proc.Kill(); proc.WaitForExit(2000); }
            }
            catch { }
            try { proc.Dispose(); } catch { }

            var path0 = OutputPath;
            long size = 0;
            try { if (path0 != null && File.Exists(path0)) size = new FileInfo(path0).Length; } catch { }

            if (path0 == null || size == 0)
            {
                Log.Info($"[record] ffmpeg 未产出有效文件（帧数={FrameCount}）");
                DeleteOutputFile();
                _state = RecordState.Idle;
                return null;
            }

            Log.Info($"[record] 已保存 {path0} {size} B 帧数={FrameCount} " +
                     $"时长={Elapsed:mm\\:ss\\.f}");
            _state = RecordState.Idle;
            return path0;
        }

        // 内置 GIF 编码
        List<Bitmap> frames;
        lock (_framesGate)
        {
            frames = _frames.ToList();
            _frames.Clear();
        }
        if (frames.Count == 0)
        {
            _state = RecordState.Idle;
            return null;
        }

        var dir = ImageUtil.EnsureDirectory(_settings.SaveDirectory);
        var path = ImageUtil.EnsureUniquePath(Path.Combine(dir,
            ImageUtil.BuildFileName(_settings.FileNamePattern + "_录制", ".gif")));

        try
        {
            WriteGif(frames, path, Fps);
            OutputPath = path;
            Log.Info($"[record] 已保存 {path} 帧数={frames.Count} 时长={Elapsed:mm\\:ss\\.f}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Info("[record] GIF 编码失败: " + ex.Message);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return null;
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
            _state = RecordState.Idle;
        }
    }

    /// <summary>放弃本次录制：停止并删除半成品文件。</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_state == RecordState.Idle) return;
            _state = RecordState.Stopping;
        }

        StopCapture();
        StopWriter();
        KillFfmpeg();

        lock (_framesGate)
        {
            foreach (var f in _frames) f.Dispose();
            _frames.Clear();
        }

        DeleteOutputFile();
        _state = RecordState.Idle;
        Log.Info("[record] 已取消（未保存）");
    }

    private void KillFfmpeg()
    {
        var proc = _ffmpeg;
        _ffmpeg = null;
        if (proc == null) return;

        try { proc.StandardInput.Close(); } catch { }
        try { if (!proc.WaitForExit(1500)) proc.Kill(); } catch { }
        try { proc.Dispose(); } catch { }
    }

    private void DeleteOutputFile()
    {
        var path = OutputPath;
        OutputPath = null;
        if (path == null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Info("[record] 删除半成品失败: " + ex.Message);
        }
    }

    private bool TryStartFfmpeg(bool mp4)
    {
        var exe = FindFfmpeg();
        if (exe == null) return false;

        try
        {
            var dir = ImageUtil.EnsureDirectory(_settings.SaveDirectory);
            var ext = mp4 ? ".mp4" : ".gif";
            OutputPath = ImageUtil.EnsureUniquePath(
                Path.Combine(dir, ImageUtil.BuildFileName(_settings.FileNamePattern + "_录制", ext)));

            int w = EncodeWidth;
            int h = EncodeHeight;
            int fps = Fps;

            var args = mp4
                ? $"-y -f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps} -i pipe:0 " +
                  $"-c:v libx264 -preset veryfast -crf 24 -pix_fmt yuv420p -movflags +faststart \"{OutputPath}\""
                : $"-y -f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps} -i pipe:0 " +
                  $"-vf \"scale={w}:{h}:flags=neighbor,split[a][b];[a]palettegen=max_colors=192[p];[b][p]paletteuse=dither=bayer:bayer_scale=3\" \"{OutputPath}\"";

            _ffmpeg = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            _ffmpeg.Start();
            return true;
        }
        catch
        {
            _ffmpeg = null;
            OutputPath = null;
            return false;
        }
    }

    public static string? FindFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>写出动画 GIF（内置编码器，无外部依赖）。</summary>
    private static void WriteGif(List<Bitmap> frames, string path, int fps)
    {
        if (frames.Count == 0) return;

        // AddFrame 的 delay 单位是毫秒（GifEncoder 内部会 ÷10 转成 GIF 的 1/100 秒）。
        // 这里按 fps 换算成每帧的毫秒间隔。
        var delayMs = Math.Max(20, 1000 / Math.Max(1, fps));

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var encoder = new GifEncoder(fs, frames[0].Width, frames[0].Height);
        foreach (var frame in frames)
            encoder.AddFrame(frame, delayMs);
        encoder.Finish();
    }

    public void Dispose()
    {
        // 还在录就当成"取消"：不留下半截文件，也不把 ffmpeg 挂在那里
        if (_state != RecordState.Idle) Cancel();
        else StopWriter();

        lock (_framesGate)
        {
            foreach (var f in _frames) f.Dispose();
            _frames.Clear();
        }

        _frameBuffer?.Dispose();
        _frameBuffer = null;
        _pixelBuffer = null;

        KillFfmpeg();
    }
}
