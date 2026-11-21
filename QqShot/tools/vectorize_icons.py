"""把 Ui/Assets/icons/*.png 追踪成矢量轮廓，生成 Ui/LegacyIcons.cs。

用法: python tools/vectorize_icons.py [阈值]   # 阈值越小笔画越粗
"""
import glob, json, math, os, sys
from PIL import Image

UP = 8            # 上采样倍数（决定轮廓精度）
EPS = 1.6         # RDP 简化阈值（上采样坐标）
TH = float(sys.argv[1]) if len(sys.argv) > 1 else 0.5
ERODE = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0   # 笔画的额外内缩量（原始像素）
UP_OVERRIDE = int(sys.argv[3]) if len(sys.argv) > 3 else 0  # 指定上采样倍数（0=默认 8）

DIRS = [(1,0),(1,1),(0,1),(-1,1),(-1,0),(-1,-1),(0,-1),(1,-1)]


def walk(mask, W, H, x, y):
    get = lambda a, b: mask[b*W+a] if 0 <= a < W and 0 <= b < H else 0
    pts, cx, cy, d, sx, sy = [], x, y, 0, x, y
    for _ in range(W*H*4):
        pts.append((cx, cy)); moved = False
        for k in range(8):
            nd = (d+k) % 8
            nx, ny = cx+DIRS[nd][0], cy+DIRS[nd][1]
            if get(nx, ny):
                cx, cy, d = nx, ny, (nd+5) % 8; moved = True; break
        if not moved: break
        if (cx, cy) == (sx, sy) and len(pts) > 2: break
    return pts


def rdp(pts, eps):
    if len(pts) < 4: return pts
    def dist(p, a, b):
        ax, ay = a; bx, by = b; px, py = p
        dx, dy = bx-ax, by-ay
        if dx == 0 and dy == 0: return math.hypot(px-ax, py-ay)
        t = max(0, min(1, ((px-ax)*dx+(py-ay)*dy)/(dx*dx+dy*dy)))
        return math.hypot(px-(ax+t*dx), py-(ay+t*dy))
    dmax, idx = 0, 0
    for i in range(1, len(pts)-1):
        dd = dist(pts[i], pts[0], pts[-1])
        if dd > dmax: dmax, idx = dd, i
    if dmax > eps: return rdp(pts[:idx+1], eps)[:-1] + rdp(pts[idx:], eps)
    return [pts[0], pts[-1]]


def area(lp):
    return sum(lp[k][0]*lp[(k+1) % len(lp)][1] - lp[(k+1) % len(lp)][0]*lp[k][1]
               for k in range(len(lp))) / 2


def bbox(lp):
    xs = [p[0] for p in lp]; ys = [p[1] for p in lp]
    return [min(xs), min(ys), max(xs), max(ys)]


def dedupe(loops, tol=0.10, ratio=(0.65, 1.55)):
    """同一处边界会被正反各描一次：包围盒接近、面积量级相当、符号相反 → 只留大者。"""
    used = [False]*len(loops)
    for i in range(len(loops)):
        if used[i]: continue
        bi, ai = bbox(loops[i]), abs(area(loops[i]))
        for j in range(i+1, len(loops)):
            if used[j]: continue
            bj, aj = bbox(loops[j]), abs(area(loops[j]))
            if ai*aj == 0: continue
            if (all(abs(bi[k]-bj[k]) < tol for k in range(4))
                    and ratio[0] <= ai/aj <= ratio[1]
                    and area(loops[i])*area(loops[j]) < 0):
                used[i if ai < aj else j] = True
    return [lp for k, lp in enumerate(loops) if not used[k]]


def process(path):
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    up = UP_OVERRIDE or UP
    W, H = w*up, h*up
    raw = im.split()[3].resize((W, H), Image.BICUBIC).tobytes()
    thr = int(TH*255)
    fg = bytearray(W*H)
    for i in range(W*H):
        if raw[i] > thr: fg[i] = 1

    # 外部背景
    bg = bytearray(W*H); stack = []
    for x in range(W):
        for y in (0, H-1):
            if not fg[y*W+x]: stack.append((x, y))
    for y in range(H):
        for x in (0, W-1):
            if not fg[y*W+x]: stack.append((x, y))
    while stack:
        x, y = stack.pop()
        if not (0 <= x < W and 0 <= y < H): continue
        i = y*W+x
        if bg[i] or fg[i]: continue
        bg[i] = 1
        stack += [(x+1, y), (x-1, y), (x, y+1), (x, y-1)]

    # 外轮廓（只从左上角起步）
    loops, seen = [], bytearray(W*H)
    for y in range(H):
        for x in range(W):
            if not fg[y*W+x] or seen[y*W+x]: continue
            if (y > 0 and fg[(y-1)*W+x]) or (x > 0 and fg[y*W+x-1]): continue
            pts = walk(fg, W, H, x, y)
            for px, py in pts: seen[py*W+px] = 1
            if len(pts) > 6: loops.append(pts)

    # 内部洞：逐个连通块描边
    done = bytearray(W*H)
    for y in range(H):
        for x in range(W):
            i = y*W+x
            if fg[i] or bg[i] or done[i]: continue
            comp, k = [(x, y)], 0
            done[i] = 1
            while k < len(comp):
                cx, cy = comp[k]; k += 1
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = cx+dx, cy+dy
                    if not (0 <= nx < W and 0 <= ny < H): continue
                    j = ny*W+nx
                    if fg[j] or bg[j] or done[j]: continue
                    done[j] = 1; comp.append((nx, ny))
            if len(comp) < 6: continue
            hm = bytearray(W*H)
            for px, py in comp: hm[py*W+px] = 1
            sx, sy = min(comp, key=lambda p: (p[1], p[0]))
            pts = walk(hm, W, H, sx, sy)
            if len(pts) > 6: loops.append(pts)

    # 简化 → 归一化 → **在归一化坐标下**去重。
    # 注意：去重必须在归一化之后，否则容差 0.1 放在像素坐标里等于不生效
    # （每个洞会被正反各描一次，奇偶填充相互抵消，图标整体变实心块）。
    poly = []
    for lp in loops:
        pts = rdp(lp, EPS)
        if len(pts) >= 3:
            poly.append([(a / W, b / H) for a, b in pts])

    clean = []
    for lp in dedupe(poly, tol=0.10):
        if ERODE > 0:
            lp = erode(lp, ERODE / max(1, min(w, h)))
        clean.append([(round(a, 4), round(b, 4)) for a, b in lp])
    return w, h, clean


def erode(loop, dist):
    """把闭合多边形沿内侧平移 dist（原始像素单位）。

    用于把"硬阈值追踪出来"的笔画调细一点点 —— 比整体缩小图标更能保留比例，
    也不会像抬高阈值那样把细笔画直接吃断。
    """
    n = len(loop)
    if n < 3 or dist <= 0: return loop

    sign = 1.0 if area(loop) > 0 else -1.0
    out = []
    for i in range(n):
        x0, y0 = loop[i-1]
        x1, y1 = loop[i]
        x2, y2 = loop[(i+1) % n]

        # 两条邻边的法线取平均，保证拐角处过渡连续
        nx = ((y1-y0) + (y2-y1))
        ny = (-(x1-x0) - (x2-x1))
        ln = math.hypot(nx, ny)
        if ln < 1e-9:
            out.append((x1, y1)); continue

        nx, ny = nx/ln, ny/ln
        out.append((x1 + nx*sign*dist, y1 + ny*sign*dist))
    return out


def emit(data, target):
    L = ["using System.Drawing.Drawing2D;", "", "namespace QqShot.Ui;", "",
         "/// <summary>",
         "/// 旧版图标的**精确矢量复刻**（tools/vectorize_icons.py 生成，勿手改坐标）。",
         "/// 原图 alpha 高倍二值化 → 追踪外轮廓与内部洞 → 亚像素简化 → 归一化；",
         "/// 运行时按目标尺寸重建路径，奇偶填充还原镂空。边缘是真矢量，缩放不产生台阶。",
         "/// </summary>",
         "internal static class LegacyIcons", "{",
         "    private sealed record Entry(Size Design, float[][] Loops);", "",
         "    private static readonly Dictionary<string, Entry> Map = new()", "    {"]
    for n in sorted(data):
        e = data[n]; w, h = e["size"]
        L.append(f'        ["{n}"] = new Entry(new Size({w}, {h}), new[]')
        L.append("        {")
        for lp in e["loops"]:
            L.append("            new[] { " + ", ".join(f"{x:.4f}f, {y:.4f}f" for x, y in lp) + " },")
        L.append("        }),")
    L.append("    };")
    L.append("""
    /// <summary>该图标在设计稿里的像素尺寸（按原比例摆放）。</summary>
    public static Size DesignSize(string name)
        => Map.TryGetValue(name, out var e) ? e.Design : new Size(20, 20);

    /// <summary>把图标填充到指定矩形（奇偶填充还原镂空）。</summary>
    public static void Draw(Graphics g, string name, RectangleF box, Color color)
    {
        if (!Map.TryGetValue(name, out var entry)) return;
        if (box.Width < 0.5f || box.Height < 0.5f) return;

        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = new GraphicsPath(FillMode.Alternate);
        foreach (var loop in entry.Loops)
        {
            var pts = new PointF[loop.Length / 2];
            for (int i = 0, k = 0; i < loop.Length; i += 2, k++)
                pts[k] = new PointF(box.X + loop[i] * box.Width, box.Y + loop[i + 1] * box.Height);
            if (pts.Length >= 3) path.AddPolygon(pts);
        }

        using (var brush = new SolidBrush(color))
            g.FillPath(brush, path);

        g.SmoothingMode = mode;
    }
}""")
    open(target, "w", encoding="utf-8", newline="\n").write("\n".join(L) + "\n")


if __name__ == "__main__":
    data = {}
    for f in sorted(glob.glob("Ui/Assets/icons/*.png")):
        n = os.path.basename(f)[:-4]
        if n in ("avatar", "pill"): continue
        w, h, loops = process(f)
        data[n] = {"size": [w, h], "loops": loops}
        print(f"{n:12s} 轮廓 {len(loops):2d} 点 {sum(len(c) for c in loops):4d}")
    emit(data, "Ui/LegacyIcons.cs")
    print(f"已生成 Ui/LegacyIcons.cs（阈值 {TH}）")
