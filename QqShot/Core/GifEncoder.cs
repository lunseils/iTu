using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QqShot.Core;

/// <summary>
/// 自研 GIF89a 编码器。
///
/// .NET 10 的 System.Drawing.Common 已移除动画 GIF 写入能力
/// （无 Encoder.TimeDelay、无多帧 SaveAdd），因此这里独立实现：
///   1. 中位切分量化 → 256 色调色板
///   2. GIF LZW 压缩
///   3. Graphic Control Extension 逐帧写入
/// 目标是让"屏幕录制 → GIF"不依赖任何外部程序。
/// </summary>
public sealed class GifEncoder : IDisposable
{
    private const int MinCodeSize = 8;
    private const int ClearCode = 1 << MinCodeSize;   // 256
    private const int EoiCode = ClearCode + 1;         // 257
    private const int MaxCode = 4095;

    private readonly Stream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _palette = new byte[256 * 3];
    private readonly bool[] _paletteUsed = new bool[256];
    private bool _paletteReady;
    private bool _finished;

    public GifEncoder(Stream stream, int width, int height)
    {
        _stream = stream;
        _width = width;
        _height = height;
    }

    /// <summary>写入一帧。</summary>
    /// <param name="frame">帧图像</param>
    /// <param name="delayMs">帧间隔（毫秒）</param>
    public void AddFrame(Bitmap frame, int delayMs)
    {
        if (_finished) throw new InvalidOperationException("GIF 已结束");

        var indices = new byte[_width * _height];
        if (!_paletteReady)
        {
            BuildPalette(frame);
            _paletteReady = true;
            WriteHeader();
        }
        Quantize(frame, indices);
        WriteFrame(indices, delayMs);
    }

    public void Finish()
    {
        if (_finished) return;
        if (!_paletteReady)
        {
            BuildPalette(null);
            _paletteReady = true;
            WriteHeader();
        }
        _stream.WriteByte(0x3B); // trailer
        _stream.Flush();
        _finished = true;
    }

    public void Dispose()
    {
        if (!_finished)
        {
            try { Finish(); } catch { /* 忽略 */ }
        }
    }

    // ---------- 文件结构 ----------

    private void WriteHeader()
    {
        // GIF89a
        _stream.Write("GIF89a"u8);

        // Logical Screen Descriptor
        WriteUInt16(_width);
        WriteUInt16(_height);
        _stream.WriteByte(0xF7); // 有全局调色板，颜色分辨率 8 位，调色板大小 256
        _stream.WriteByte(0);    // 背景色索引
        _stream.WriteByte(0);    // 像素宽高比

        // Global Color Table
        _stream.Write(_palette, 0, _palette.Length);

        // NETSCAPE2.0 应用扩展（循环播放）
        _stream.WriteByte(0x21);
        _stream.WriteByte(0xFF);
        _stream.WriteByte(0x0B);
        _stream.Write("NETSCAPE2.0"u8);
        _stream.WriteByte(0x03);
        _stream.WriteByte(0x01);
        WriteUInt16(0); // 无限循环
        _stream.WriteByte(0);
    }

    private void WriteFrame(byte[] indices, int delayMs)
    {
        // Graphic Control Extension
        _stream.WriteByte(0x21);
        _stream.WriteByte(0xF9);
        _stream.WriteByte(0x04);
        _stream.WriteByte(0x04);          // disposal = 1（不处理），无透明色
        // GIF 的 GCE delay 单位是 1/100 秒，不是毫秒。
        // 直接写毫秒会让 20fps 的录制变成 5fps（实测慢 10 倍）。
        // 0 在多数播放器里会被当作"无延迟/异常"，至少写 1。
        int delayCs = Math.Clamp((int)Math.Round(Math.Max(1, delayMs) / 10.0), 1, 65535);
        WriteUInt16(delayCs);
        _stream.WriteByte(0);             // 透明色索引
        _stream.WriteByte(0);             // 块终止符

        // Image Descriptor
        _stream.WriteByte(0x2C);          // 引导符
        WriteUInt16(0);                   // 左
        WriteUInt16(0);                   // 上
        WriteUInt16(_width);
        WriteUInt16(_height);
        _stream.WriteByte(0x00);          // 无局部调色板，非交错

        // LZW 最小码长 + 数据子块
        _stream.WriteByte(MinCodeSize);
        var compressed = LzwEncode(indices);
        for (int i = 0; i < compressed.Length; i += 255)
        {
            int len = Math.Min(255, compressed.Length - i);
            _stream.WriteByte((byte)len);
            _stream.Write(compressed, i, len);
        }
        _stream.WriteByte(0); // block terminator
    }

    private void WriteUInt16(int value)
    {
        _stream.WriteByte((byte)(value & 0xFF));
        _stream.WriteByte((byte)((value >> 8) & 0xFF));
    }

    // ---------- 调色板 ----------

    /// <summary>中位切分量化，生成 256 色调色板。</summary>
    private void BuildPalette(Bitmap? sample)
    {
        const int buckets = 32768; // 5bit per channel
        var counts = new int[buckets];
        var sumR = new long[buckets];
        var sumG = new long[buckets];
        var sumB = new long[buckets];

        if (sample != null)
        {
            var data = sample.LockBits(new Rectangle(0, 0, sample.Width, sample.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var buf = new byte[stride * sample.Height];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                int stepX = Math.Max(1, sample.Width / 320);
                int stepY = Math.Max(1, sample.Height / 320);

                for (int y = 0; y < sample.Height; y += stepY)
                {
                    int row = y * stride;
                    for (int x = 0; x < sample.Width; x += stepX)
                    {
                        int o = row + x * 4;
                        byte b = buf[o];
                        byte g = buf[o + 1];
                        byte r = buf[o + 2];
                        int idx = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                        counts[idx]++;
                        sumR[idx] += r;
                        sumG[idx] += g;
                        sumB[idx] += b;
                    }
                }
            }
            finally
            {
                sample.UnlockBits(data);
            }
        }
        else
        {
            counts[0] = 1;
        }

        var used = new List<int>();
        for (int i = 0; i < buckets; i++)
            if (counts[i] > 0) used.Add(i);

        if (used.Count == 0) used.Add(0);

        var boxes = new List<Box> { new Box(used) };
        foreach (var b in boxes) b.Recompute(counts, sumR, sumG, sumB);

        while (boxes.Count < 256)
        {
            // 选体积最大的可分裂盒子
            int bestIdx = -1;
            long bestVolume = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                if (b.Buckets.Count < 2) continue;
                long vol = (long)Math.Max(1, b.MaxR - b.MinR) *
                           Math.Max(1, b.MaxG - b.MinG) *
                           Math.Max(1, b.MaxB - b.MinB) *
                           Math.Max(1, b.Count);
                if (vol > bestVolume) { bestVolume = vol; bestIdx = i; }
            }
            if (bestIdx < 0) break;

            var box = boxes[bestIdx];
            var (a, bList) = box.Split(counts);
            a.Recompute(counts, sumR, sumG, sumB);
            bList.Recompute(counts, sumR, sumG, sumB);
            boxes[bestIdx] = a;
            boxes.Add(bList);
        }

        for (int i = 0; i < boxes.Count && i < 256; i++)
        {
            var box = boxes[i];
            byte r = (byte)(box.SumR / Math.Max(1, box.Count));
            byte g = (byte)(box.SumG / Math.Max(1, box.Count));
            byte b = (byte)(box.SumB / Math.Max(1, box.Count));
            _palette[i * 3] = r;
            _palette[i * 3 + 1] = g;
            _palette[i * 3 + 2] = b;
            _paletteUsed[i] = true;
        }

        BuildLookup();
    }

    private byte[] _lookup = new byte[32768];

    /// <summary>为每个 5bit 颜色桶预计算最近的调色板索引，避免逐像素暴力搜索。</summary>
    private void BuildLookup()
    {
        for (int bucket = 0; bucket < 32768; bucket++)
        {
            int r = ((bucket >> 10) & 31) << 3;
            int g = ((bucket >> 5) & 31) << 3;
            int b = (bucket & 31) << 3;

            int best = 0;
            long bestDist = long.MaxValue;
            for (int i = 0; i < 256; i++)
            {
                if (!_paletteUsed[i]) continue;
                long dr = r - _palette[i * 3];
                long dg = g - _palette[i * 3 + 1];
                long db = b - _palette[i * 3 + 2];
                long dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            _lookup[bucket] = (byte)best;
        }
    }

    private void Quantize(Bitmap frame, byte[] indices)
    {
        var data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * frame.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);

            int w = Math.Min(frame.Width, _width);
            int h = Math.Min(frame.Height, _height);

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int dst = y * _width;
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    byte b = buf[o];
                    byte g = buf[o + 1];
                    byte r = buf[o + 2];
                    int bucket = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    indices[dst + x] = _lookup[bucket];
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }
    }

    // ---------- LZW ----------

    /// <summary>GIF 变长 LZW 压缩。</summary>
    /// <remarks>
    /// LZW 的编码与解码必须严格对称。GIF 解码器在读入一个码后建立新条目：
    ///     newEntry = oldEntry + first_char(currentEntry)
    /// 其中 oldEntry 是上一个输出的码，currentEntry 是当前输出的码。
    ///
    /// 因此编码器输出 prefix（当前码）后，新建的字典条目是：
    ///     key       = (prev &lt;&lt; 8) | firstChar[prefix]
    ///     首字符    = firstChar[prev]
    ///
    /// 注意：键的后缀是"当前码展开后的首字符"，不是下一个待处理字符。
    /// 若直接用下一个字符建表，会与解码器错位，表现为整帧数据流损坏。
    ///
    /// 码宽在码表填满当前宽度（avail == 2^codeSize）时加宽一位。
    /// 本实现已用规范解码器回归验证，可完整还原全部像素。
    /// </remarks>
    private static byte[] LzwEncode(byte[] pixels)
    {
        var output = new List<byte>(pixels.Length / 2 + 64);
        var bitWriter = new BitWriter(output);

        const int initBits = MinCodeSize + 1;   // 9
        int codeSize = initBits;
        int avail = EoiCode + 1;                // 258

        var table = new Dictionary<int, int>(4096);
        var firstChar = new byte[4096];
        for (int i = 0; i < 256; i++) firstChar[i] = (byte)i;

        void WriteCode(int code) => bitWriter.Write(code, codeSize);

        WriteCode(ClearCode);

        if (pixels.Length == 0)
        {
            WriteCode(EoiCode);
            bitWriter.Flush();
            return output.ToArray();
        }

        int prefix = pixels[0];
        int prev = -1;                          // 上一个输出的码

        for (int i = 1; i < pixels.Length; i++)
        {
            int k = pixels[i];
            int key = (prefix << 8) | k;

            if (table.TryGetValue(key, out int code))
            {
                prefix = code;
                continue;
            }

            // 输出当前码
            WriteCode(prefix);

            if (avail < 4096)
            {
                // 首个码没有前驱，不建条目也不占用码表槽位
                if (prev >= 0)
                {
                    // 新条目 = prev 的展开 + firstChar[prefix]
                    int dictKey = (prev << 8) | firstChar[prefix];
                    firstChar[avail] = firstChar[prev];
                    if (!table.ContainsKey(dictKey))
                        table[dictKey] = avail;
                    avail++;

                    // 码表刚好填满当前宽度 → 下一个码加宽一位
                    if (avail == (1 << codeSize) && codeSize < 12)
                        codeSize++;
                }
            }
            else
            {
                // 码表已满：输出清除码并重置
                WriteCode(ClearCode);
                table.Clear();
                avail = EoiCode + 1;
                codeSize = initBits;
                prev = -1;
                prefix = k;
                continue;
            }

            prev = prefix;
            prefix = k;
        }

        WriteCode(prefix);
        WriteCode(EoiCode);
        bitWriter.Flush();
        return output.ToArray();
    }

    /// <summary>GIF 位打包器：低位在前。</summary>
    private sealed class BitWriter(List<byte> output)
    {
        private int _bitBuffer;
        private int _bitCount;

        public void Write(int code, int codeSize)
        {
            _bitBuffer |= code << _bitCount;
            _bitCount += codeSize;
            while (_bitCount >= 8)
            {
                output.Add((byte)(_bitBuffer & 0xFF));
                _bitBuffer >>= 8;
                _bitCount -= 8;
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
            {
                output.Add((byte)(_bitBuffer & 0xFF));
                _bitBuffer = 0;
                _bitCount = 0;
            }
        }
    }

    /// <summary>中位切分的单个色盒。</summary>
    private sealed class Box
    {
        public List<int> Buckets { get; }
        public int MinR { get; private set; } = 31;
        public int MaxR { get; private set; }
        public int MinG { get; private set; } = 31;
        public int MaxG { get; private set; }
        public int MinB { get; private set; } = 31;
        public int MaxB { get; private set; }
        public long SumR { get; private set; }
        public long SumG { get; private set; }
        public long SumB { get; private set; }
        public int Count { get; private set; }

        public Box(List<int> buckets) => Buckets = buckets;

        /// <summary>用给定直方图统计量刷新包围盒与均值累加。</summary>
        public void Recompute(int[] counts, long[] sumR, long[] sumG, long[] sumB)
        {
            MinR = MinG = MinB = 31;
            MaxR = MaxG = MaxB = 0;
            SumR = SumG = SumB = 0;
            Count = 0;

            foreach (var b in Buckets)
            {
                int r = (b >> 10) & 31;
                int g = (b >> 5) & 31;
                int bl = b & 31;
                if (r < MinR) MinR = r;
                if (r > MaxR) MaxR = r;
                if (g < MinG) MinG = g;
                if (g > MaxG) MaxG = g;
                if (bl < MinB) MinB = bl;
                if (bl > MaxB) MaxB = bl;
                SumR += sumR[b];
                SumG += sumG[b];
                SumB += sumB[b];
                Count += counts[b];
            }
        }

        /// <summary>沿最长轴按像素数中位数切分为两盒。</summary>
        public (Box, Box) Split(int[] counts)
        {
            int rangeR = MaxR - MinR;
            int rangeG = MaxG - MinG;
            int rangeB = MaxB - MinB;

            int axis = 0;
            int range = rangeR;
            if (rangeG > range) { axis = 1; range = rangeG; }
            if (rangeB > range) { axis = 2; range = rangeB; }

            // 按轴坐标排序
            var sorted = new List<int>(Buckets);
            sorted.Sort((x, y) => Axis(x, axis).CompareTo(Axis(y, axis)));

            long total = 0;
            foreach (var b in sorted) total += counts[b];

            long half = total / 2;
            long acc = 0;
            int splitIdx = 1;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                acc += counts[sorted[i]];
                splitIdx = i + 1;
                if (acc >= half) break;
            }
            if (splitIdx >= sorted.Count) splitIdx = sorted.Count - 1;

            var left = new Box(sorted.GetRange(0, splitIdx));
            var right = new Box(sorted.GetRange(splitIdx, sorted.Count - splitIdx));
            return (left, right);
        }

        private static int Axis(int bucket, int axis) => axis switch
        {
            0 => (bucket >> 10) & 31,
            1 => (bucket >> 5) & 31,
            _ => bucket & 31
        };
    }
}
