// Frame-accurate capture of a window via Windows.Graphics.Capture.
// Every frame DWM composes for the target window is copied to CPU memory, then analysed:
//   progress_open(i) = 1 - MAD(frame_i, OPEN) / MAD(BASE, OPEN)
// where BASE = first captured frame (no dialog) and OPEN = the most-different frame (dialog fully shown).
// A dialog that "pops" in shows a single 0 -> 1 step; an animated entrance shows intermediate values.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

unsafe class Program
{
    record Frame(double T, int W, int H, byte[] Px);
    static bool s_dumpAll;

    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int level, out IntPtr ctx);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static IntPtr FindWindowByTitle(string sub, uint pid = 0)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            GetWindowThreadProcessId(h, out var p);
            if (sb.ToString().Contains(sub, StringComparison.OrdinalIgnoreCase) && (pid == 0 || p == pid)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IDirect3DDevice CreateDevice()
    {
        const uint BGRA = 0x20;
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, BGRA, IntPtr.Zero, 0, 7, out var dev, out _, out var ctx));
        var iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(dev, ref iidDxgiDevice, out var dxgi));
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var insp));
        var d = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(insp);
        Marshal.Release(insp); Marshal.Release(dxgi); Marshal.Release(ctx); Marshal.Release(dev);
        return d;
    }

    static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var iidInterop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, ref iidInterop, out var interop));
        var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var vtbl = *(IntPtr**)interop;
        var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
        IntPtr result;
        Marshal.ThrowExceptionForHR(createForWindow(interop, hwnd, &iidItem, &result));
        Marshal.Release(interop);
        var item = GraphicsCaptureItem.FromAbi(result);
        Marshal.Release(result);
        return item;
    }

    static int Main(string[] args)
    {
        string title = Arg(args, "--title");
        string launch = Arg(args, "--launch");
        string launchArgs = Arg(args, "--args") ?? "";
        string outDir = Arg(args, "--out") ?? "capout";
        int durationMs = int.Parse(Arg(args, "--duration") ?? "8000");
        Directory.CreateDirectory(outDir);

        Process proc = null;
        var sw = Stopwatch.StartNew();
        if (launch != null)
        {
            proc = Process.Start(new ProcessStartInfo(launch, launchArgs) { UseShellExecute = false });
        }

        IntPtr hwnd = IntPtr.Zero;
        while (hwnd == IntPtr.Zero && sw.ElapsedMilliseconds < 15000)
        {
            hwnd = FindWindowByTitle(title, proc != null ? (uint)proc.Id : 0);
            if (hwnd == IntPtr.Zero) Thread.Sleep(20);
        }
        if (hwnd == IntPtr.Zero) { Console.Error.WriteLine("window not found: " + title); return 1; }
        Console.WriteLine($"hwnd=0x{hwnd:X}");

        var device = CreateDevice();
        var item = CreateItem(hwnd);
        var frames = new List<Frame>();
        var framesLock = new object();
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 8, item.Size);
        pool.FrameArrived += (p, _) =>
        {
            using var f = p.TryGetNextFrame();
            if (f == null) return;
            using var sb = SoftwareBitmap.CreateCopyFromSurfaceAsync(f.Surface, BitmapAlphaMode.Premultiplied).AsTask().Result;
            var buf = new byte[sb.PixelWidth * sb.PixelHeight * 4];
            sb.CopyToBuffer(buf.AsBuffer());
            lock (framesLock) frames.Add(new Frame(f.SystemRelativeTime.TotalMilliseconds, sb.PixelWidth, sb.PixelHeight, buf));
        };
        var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { }
        try { session.IsBorderRequired = false; } catch { }
        session.StartCapture();

        var end = sw.ElapsedMilliseconds + durationMs;
        while (sw.ElapsedMilliseconds < end)
        {
            if (proc != null && proc.HasExited) break;
            Thread.Sleep(10);
        }
        session.Dispose();
        pool.Dispose();
        Thread.Sleep(200);

        List<Frame> fr;
        lock (framesLock) fr = frames.ToList();
        int skipMs = int.Parse(Arg(args, "--skip-ms") ?? "0");
        if (fr.Count > 0) { var first = fr[0].T; fr = fr.Where(f => f.T - first >= skipMs).ToList(); }
        s_dumpAll = args.Contains("--dump");
        var markers = new List<(double, double)>();
        var logPath = Arg(args, "--applog");
        if (logPath != null && File.Exists(logPath))
        {
            var ev = File.ReadAllLines(logPath).Select(l => l.Split(',')).Where(p => p.Length >= 4).ToList();
            var shows = ev.Where(p => (p[1] == "ShowAsync" || p[1] == "warmup-ShowAsync")).Select(p => double.Parse(p[3])).ToList();
            var hides = ev.Where(p => p[1] == "Hide").Select(p => double.Parse(p[3])).ToList();
            for (int k = 0; k < shows.Count; k++) markers.Add((shows[k], k < hides.Count ? hides[k] : double.MaxValue));
        }
        Analyse(fr, outDir, markers);
        return 0;
    }

    static string Arg(string[] a, string k) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static double MeanLum(Frame f)
    {
        long s = 0, n = 0;
        // ignore the top 40 px (title bar)
        for (int i = f.W * 4 * 40; i < f.Px.Length; i += 4 * 7) { s += (f.Px[i] + f.Px[i + 1] * 2 + f.Px[i + 2]) / 4; n++; }
        return (double)s / n;
    }

    static double Mad(Frame a, Frame b)
    {
        long s = 0; long n = 0;
        var pa = a.Px; var pb = b.Px;
        for (int i = 0; i < pa.Length; i += 4 * 3) // every 3rd pixel
        {
            s += Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
            n++;
        }
        return (double)s / n;
    }

    // Bounding box of pixels whose luminance differs from BASE by more than thr, ignoring a uniform darkening (smoke).
    static (int x0, int y0, int x1, int y1) DiffBox(Frame f, Frame b, int thr)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < f.H; y += 2)
            for (int x = 0; x < f.W; x += 2)
            {
                int i = (y * f.W + x) * 4;
                int lf = (f.Px[i] + f.Px[i + 1] * 2 + f.Px[i + 2]) / 4;
                int lb = (b.Px[i] + b.Px[i + 1] * 2 + b.Px[i + 2]) / 4;
                if (lf - lb > thr) // brighter than base: dialog text / buttons / panel edges, not smoke (smoke only darkens)
                {
                    if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y;
                }
            }
        return (x0, y0, x1, y1);
    }

    // markers: list of (showQpc, hideQpc) pairs taken from the app log; empty => heuristic mode (external app).
    static void Analyse(List<Frame> all, string outDir, List<(double show, double hide)> markers)
    {
        var fr = new List<Frame>();
        foreach (var f in all)
        {
            if (fr.Count > 0 && (f.W != fr[0].W || f.H != fr[0].H)) continue;
            if (fr.Count > 0 && Mad(f, fr[^1]) < 0.05) continue;
            fr.Add(f);
        }
        while (fr.Count > 2 && MeanLum(fr[^1]) < 12) fr.RemoveAt(fr.Count - 1);
        if (fr.Count < 2) { Console.WriteLine("not enough frames: " + all.Count); return; }
        var t0 = fr[0].T;

        if (markers.Count == 0)
        {
            // heuristic: base = first frame, open = most different frame
            int oi = 0; double best = -1;
            for (int i = 0; i < fr.Count; i++) { var m = Mad(fr[i], fr[0]); if (m > best) { best = m; oi = i; } }
            // show = first frame that differs noticeably from base; hide = first frame after oi that differs from open
            int si = Enumerable.Range(1, fr.Count - 1).FirstOrDefault(i => Mad(fr[i], fr[0]) > 1.0);
            int hi = Enumerable.Range(oi + 1, Math.Max(0, fr.Count - oi - 1)).FirstOrDefault(i => Mad(fr[i], fr[oi]) > 1.0);
            markers.Add((fr[Math.Max(si, 1)].T - 0.1, hi > 0 ? fr[hi].T - 0.1 : double.MaxValue));
        }

        if (s_dumpAll)
            for (int i = 0; i < fr.Count; i++) { using var bmp = ToBitmap(fr[i]); bmp.Save(Path.Combine(outDir, $"f{i:D3}_{fr[i].T - t0:F0}ms.png"), ImageFormat.Png); }
        {
            var all2 = new StringBuilder("idx,qpc_ms,t_ms,dt_ms,mad_vs_first,mad_vs_prev,box_w,box_h\n");
            for (int i = 0; i < fr.Count; i++)
            {
                var bx = DiffBox(fr[i], fr[0], 40);
                all2.Append($"{i},{fr[i].T:F1},{fr[i].T - t0:F1},{(i > 0 ? fr[i].T - fr[i - 1].T : 0):F1},{Mad(fr[i], fr[0]):F2},{(i > 0 ? Mad(fr[i], fr[i - 1]) : 0):F2},{(bx.x1 >= 0 ? bx.x1 - bx.x0 : 0)},{(bx.y1 >= 0 ? bx.y1 - bx.y0 : 0)}\n");
            }
            File.WriteAllText(Path.Combine(outDir, "allframes.csv"), all2.ToString());
        }

        var sum = new StringBuilder();
        var csv = new StringBuilder("pair,phase,idx,qpc_ms,latency_ms,dt_ms,progress_open,box_w,box_h\n");
        sum.AppendLine($"captured frames: {all.Count}, distinct: {fr.Count}");
        int pairNo = 0;
        foreach (var (show, hide) in markers)
        {
            pairNo++;
            int bi = fr.FindLastIndex(f => f.T < show); if (bi < 0) bi = 0;
            int oi = fr.FindLastIndex(f => f.T < hide); if (oi <= bi) oi = fr.Count - 1;
            var baseF = fr[bi]; var openF = fr[oi];
            double denom = Mad(baseF, openF);
            double P(int i) => 1 - Mad(fr[i], openF) / denom;
            sum.AppendLine($"== pair {pairNo}: base frame #{bi}, open-reference frame #{oi}, MAD(base,open)={denom:F2}");
            foreach (var (phase, t, from) in new[] { ("OPEN ", show, bi), ("CLOSE", hide, oi) })
            {
                if (t == double.MaxValue) continue;
                var idx = Enumerable.Range(0, fr.Count).Where(i => fr[i].T >= t && fr[i].T <= t + 700).ToList();
                int inter = idx.Count(i => P(i) > 0.03 && P(i) < 0.97);
                int firstVisible = idx.FirstOrDefault(i => phase == "OPEN " ? P(i) > 0.03 : P(i) < 0.97, -1);
                int settled = idx.FirstOrDefault(i => phase == "OPEN " ? P(i) > 0.97 : P(i) < 0.03, -1);
                sum.AppendLine($"  {phase}: frames in +700ms window = {idx.Count}, INTERMEDIATE frames = {inter}, " +
                               $"first change at +{(firstVisible >= 0 ? fr[firstVisible].T - t : double.NaN):F1} ms, settled at +{(settled >= 0 ? fr[settled].T - t : double.NaN):F1} ms");
                foreach (var i in new[] { from }.Concat(idx))
                {
                    var bx = DiffBox(fr[i], baseF, 40);
                    int bw = bx.x1 >= 0 ? bx.x1 - bx.x0 : 0, bh = bx.y1 >= 0 ? bx.y1 - bx.y0 : 0;
                    sum.AppendLine($"     #{i,3}  +{fr[i].T - t,7:F1} ms  dt={(i > 0 ? fr[i].T - fr[i - 1].T : 0),6:F1}  progress_open={P(i),6:F3}  box={bw}x{bh}");
                    csv.Append($"{pairNo},{phase.Trim()},{i},{fr[i].T:F1},{fr[i].T - t:F1},{(i > 0 ? fr[i].T - fr[i - 1].T : 0):F1},{P(i):F3},{bw},{bh}\n");
                }
                var stripIdx = new[] { from }.Concat(idx).Take(12).ToList();
                Strip(fr, stripIdx, t, P, Path.Combine(outDir, $"strip_pair{pairNo}_{phase.Trim().ToLower()}.png"));
            }
        }
        File.WriteAllText(Path.Combine(outDir, "frames.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), sum.ToString());
        Console.Write(sum);
    }

    static void Strip(List<Frame> fr, List<int> idx, double tRef, Func<int, double> P, string path)
    {
        int n = idx.Count; if (n == 0) return;
        int cw = 330; int ch = (int)(fr[0].H * (cw / (double)fr[0].W));
        int cols = Math.Min(n, 4), rows = (n + cols - 1) / cols;
        using var strip = new Bitmap(cols * cw, rows * (ch + 22));
        using var g = Graphics.FromImage(strip);
        g.Clear(Color.White);
        using var font = new Font("Segoe UI", 9);
        for (int k = 0; k < n; k++)
        {
            int i = idx[k];
            using var bmp = ToBitmap(fr[i]);
            int cx = (k % cols) * cw, cy = (k / cols) * (ch + 22);
            g.DrawImage(bmp, new Rectangle(cx, cy + 22, cw, ch));
            g.DrawString($"#{i}  {(fr[i].T - tRef >= 0 ? "+" : "")}{fr[i].T - tRef:F0}ms  p={P(i):F2}", font, Brushes.Black, cx + 4, cy + 3);
            g.DrawRectangle(Pens.Gray, cx, cy + 22, cw - 1, ch - 1);
        }
        strip.Save(path, ImageFormat.Png);
    }

    static Bitmap ToBitmap(Frame f)
    {
        var bmp = new Bitmap(f.W, f.H, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, f.W, f.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < f.H; y++) Marshal.Copy(f.Px, y * f.W * 4, d.Scan0 + y * d.Stride, f.W * 4);
        bmp.UnlockBits(d);
        // force opaque
        return bmp;
    }

}
