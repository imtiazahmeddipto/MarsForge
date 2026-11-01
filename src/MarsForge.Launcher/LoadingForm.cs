using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MarsForge.Launcher
{
    /// <summary>Small "Loading..." window: unpacks the app the first time, starts it, and closes once its window is up.</summary>
    sealed class LoadingForm : Form
    {
        // look (sizes in 96-dpi pixels)
        const int W = 360, H = 116, TitleH = 28, Edge = 2, Blocks = 20;
        static readonly Color Ink = Color.FromArgb(14, 12, 11);
        static readonly Color Body = Color.FromArgb(28, 25, 24);
        static readonly Color Accent = Color.FromArgb(226, 115, 58);
        static readonly Color AccentHover = Color.FromArgb(240, 140, 90);
        static readonly Color TextC = Color.FromArgb(238, 231, 224);
        static readonly Color Track = Color.FromArgb(19, 17, 17);
        static readonly Color TrackLine = Color.FromArgb(107, 93, 85);

        readonly Payload _payload;
        readonly string[] _args;
        readonly float _s;
        readonly Font _titleFont = new Font("Segoe UI Semibold", 9f);
        readonly Font _textFont = new Font("Segoe UI Semibold", 11f);
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 15 };
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        double _shown;
        volatile float _target;
        int _ticks;
        bool _hoverClose, _hoverMin, _done;

        public LoadingForm(Payload payload, string[] args)
        {
            _payload = payload;
            _args = args;
            using (var g = CreateGraphics()) _s = g.DpiX / 96f;
            Text = "MarsForge";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(P(W), P(H));
            CenterOnScreen();
            BackColor = Body;
            DoubleBuffered = true;
            ShowInTaskbar = true;
            _timer.Tick += (s, e) => Animate();
        }

        int P(float v) => (int)Math.Round(v * _s);
        Rectangle R(float x, float y, float w, float h) => new Rectangle(P(x), P(y), P(x + w) - P(x), P(y + h) - P(y));
        Rectangle CloseBox => R(W - 26, 6, 17, 16);
        Rectangle MinBox => R(W - 48, 6, 17, 16);

        /// <summary>Puts the window in the middle of the screen the mouse is on.</summary>
        void CenterOnScreen()
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.X + (area.Width - P(W)) / 2, area.Y + (area.Height - P(H)) / 2);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            CenterOnScreen();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer.Start();
            Task.Run(() => Work());
        }

        // ---------- the work ----------
        void Work()
        {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarsForge");
                string dir = Package.InstallDir(_payload, root);
                if (!Package.IsInstalled(dir))
                {
                    using (var mutex = new Mutex(false, "MarsForge.Unpack"))
                    {
                        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
                        try
                        {
                            if (!Package.IsInstalled(dir))                   // another copy may have just done it
                                Package.Install(_payload, dir, p => _target = (float)(0.9 * p), _cts.Token);
                        }
                        finally { mutex.ReleaseMutex(); }
                    }
                    Package.RemoveOld(root, dir);
                }
                Start(dir);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Ui(() => MessageBox.Show(this, "MarsForge could not get ready:\n\n" + e.Message, "MarsForge",
                    MessageBoxButtons.OK, MessageBoxIcon.Error));
            }
            Ui(Close);
        }

        void Start(string dir)
        {
            var psi = new ProcessStartInfo(Package.ExeIn(dir))
            {
                UseShellExecute = false,
                WorkingDirectory = dir,
                Arguments = string.Join(" ", _args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")),
            };
            // the folder of this MarsForge.exe (the app looks for an optional "data" folder there)
            psi.EnvironmentVariables["MARSFORGE_HOME"] = Path.GetDirectoryName(Application.ExecutablePath);
            using (var app = Process.Start(psi))
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 60 && !_cts.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                    app.Refresh();
                    if (app.HasExited || app.MainWindowHandle != IntPtr.Zero) break;
                    _target += (0.97f - _target) * 0.08f;                    // keep moving while the app opens
                }
            }
            _target = 1;
            Thread.Sleep(300);                                                // let the full bar show
        }

        void Ui(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts.Cancel();
            _timer.Stop();
            base.OnFormClosing(e);
        }

        // ---------- drawing ----------
        void Animate()
        {
            _ticks++;
            _shown += (_target - _shown) * 0.18;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var b = new SolidBrush(Body)) g.FillRectangle(b, R(0, 0, W, H));
            using (var b = new SolidBrush(Accent)) g.FillRectangle(b, R(0, 0, W, TitleH));
            using (var ink = new SolidBrush(Ink))
            {
                g.FillRectangle(ink, R(0, TitleH - Edge, W, Edge));          // line under the title bar
                g.FillRectangle(ink, R(0, 0, W, Edge));                      // outline
                g.FillRectangle(ink, R(0, H - Edge, W, Edge));
                g.FillRectangle(ink, R(0, 0, Edge, H));
                g.FillRectangle(ink, R(W - Edge, 0, Edge, H));
            }
            TextRenderer.DrawText(g, "MarsForge", _titleFont, R(10, 0, 200, TitleH - Edge), Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            DrawButton(g, MinBox, _hoverMin, false);
            DrawButton(g, CloseBox, _hoverClose, true);

            string dots = new string('.', 1 + _ticks / 25 % 3);
            TextRenderer.DrawText(g, "Loading" + dots, _textFont, R(16, TitleH + 10, 250, 26), TextC,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);

            // segmented bar
            var bar = R(16, TitleH + 44, W - 32, 24);
            using (var b = new SolidBrush(Track)) g.FillRectangle(b, bar);
            using (var pen = new Pen(TrackLine, P(Edge)) { Alignment = PenAlignment.Inset }) g.DrawRectangle(pen, bar);
            float pad = 4 * _s, gap = 3 * _s;
            float bw = (bar.Width - 2 * pad - (Blocks - 1) * gap) / Blocks;
            int filled = (int)Math.Floor(Math.Min(1, _shown + 1e-6) * Blocks);
            using (var b = new SolidBrush(Accent))
                for (int i = 0; i < filled; i++)
                {
                    float x = bar.X + pad + i * (bw + gap);
                    g.FillRectangle(b, (int)Math.Round(x), (int)Math.Round(bar.Y + pad),
                        (int)Math.Round(x + bw) - (int)Math.Round(x), (int)Math.Round(bar.Height - 2 * pad));
                }
        }

        void DrawButton(Graphics g, Rectangle r, bool hover, bool close)
        {
            using (var b = new SolidBrush(hover ? AccentHover : Accent)) g.FillRectangle(b, r);
            using (var pen = new Pen(Ink, P(Edge)) { Alignment = PenAlignment.Inset }) g.DrawRectangle(pen, r);
            using (var pen = new Pen(Ink, Math.Max(1.5f, 1.6f * _s)))
            {
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, k = 3.2f * _s;
                if (close)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                    g.DrawLine(pen, cx - k, cy + k, cx + k, cy - k);
                    g.SmoothingMode = SmoothingMode.None;
                }
                else g.DrawLine(pen, cx - k, cy + k * 0.8f, cx + k, cy + k * 0.8f);
            }
        }

        // ---------- mouse: drag by the title bar, minimise, close ----------
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool c = CloseBox.Contains(e.Location), m = MinBox.Contains(e.Location);
            if (c != _hoverClose || m != _hoverMin) { _hoverClose = c; _hoverMin = m; Invalidate(); }
            Cursor = c || m ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverClose || _hoverMin) { _hoverClose = _hoverMin = false; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (CloseBox.Contains(e.Location)) { if (!_done) { _done = true; Close(); } return; }
            if (MinBox.Contains(e.Location)) { WindowState = FormWindowState.Minimized; return; }
            if (e.Y < P(TitleH)) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }  // drag
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _titleFont.Dispose(); _textFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
