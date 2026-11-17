using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UartSlTest
{
    // .sl16 file = exactly the stream sent by SAVE: BASIC program bytes + 1 checksum byte (sum mod 256).
    // Line: [line number 2 B][length 1 B][content, ends with 0D]. The end marker (FF) is not stored.
    public static class Sl16
    {
        public static bool Check(byte[] s, out string err)
        {
            err = null;
            if (s.Length < 2) { err = "stream too short (empty program?)"; return false; }
            int n = s.Length - 1, sum = 0;
            for (int i = 0; i < n; i++) sum += s[i];
            if ((sum & 0xFF) != s[n]) { err = "bad checksum"; return false; }
            int p = 0;
            while (p < n)
            {
                if (p + 3 > n) { err = "truncated line"; return false; }
                int len = s[p + 2];
                if (len < 2 || p + 3 + len > n) { err = "bad line length at byte " + p; return false; }
                if (s[p + 2 + len] != 0x0D) { err = "missing CR at the end of the line at byte " + p; return false; }
                p += 3 + len;
            }
            return true;
        }

        public static int CountLines(byte[] s)
        {
            int n = s.Length - 1, p = 0, c = 0;
            while (p + 3 <= n) { p += 3 + s[p + 2]; c++; }
            return c;
        }

        // name taken from the first line that starts with REM (token F1 AB)
        public static string NameFromRem(byte[] s)
        {
            int n = s.Length - 1, p = 0;
            while (p + 3 <= n)
            {
                int len = s[p + 2];
                int c = p + 3;
                if (len >= 3 && s[c] == 0xF1 && s[c + 1] == 0xAB)
                {
                    var sb = new StringBuilder();
                    for (int i = c + 2; i < p + 2 + len; i++) sb.Append((char)s[i]);
                    string t = Clean(sb.ToString());
                    if (t.Length > 0) return t;
                }
                p += 3 + len;
            }
            return null;
        }

        // name taken from the first "[number] REM text" line of an ASCII listing
        public static string NameFromText(byte[] s)
        {
            var line = new StringBuilder();
            for (int i = 0; i <= s.Length; i++)
            {
                if (i == s.Length || s[i] == 0x0D || s[i] == 0x0A)
                {
                    string t = line.ToString().TrimStart(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                    line.Length = 0;
                    if (t.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = Clean(t.Substring(3).TrimStart(' ', '\''));
                        if (name.Length > 0) return name;
                    }
                }
                else line.Append((char)s[i]);
            }
            return null;
        }

        public static string Clean(string t)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char ch in t.Trim())
                sb.Append(ch < 32 || ch > 126 || Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
            string r = sb.ToString().Trim(' ', '.', '_');
            if (r.Length > 40) r = r.Substring(0, 40).TrimEnd(' ', '.');
            return r;
        }
    }

    // Small icons drawn in code (no image files needed)
    public static class Icons
    {
        static Bitmap Make(Action<Graphics, Pen, Brush> draw)
        {
            var b = new Bitmap(20, 20);
            using (var g = Graphics.FromImage(b))
            using (var pen = new Pen(Color.FromArgb(40, 70, 120), 2f))
            using (var br = new SolidBrush(Color.FromArgb(40, 70, 120)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                draw(g, pen, br);
            }
            return b;
        }

        public static Bitmap Refresh() { return Make((g, p, b) => { g.DrawArc(p, 4, 4, 12, 12, 30, 290); g.FillPolygon(b, new[] { new Point(18, 3), new Point(18, 11), new Point(10, 8) }); }); }
        public static Bitmap Plug() { return Make((g, p, b) => { g.FillRectangle(b, 5, 9, 10, 7); g.DrawLine(p, 8, 3, 8, 9); g.DrawLine(p, 12, 3, 12, 9); }); }
        public static Bitmap Cross() { return Make((g, p, b) => { g.DrawLine(p, 5, 5, 15, 15); g.DrawLine(p, 15, 5, 5, 15); }); }
        public static Bitmap Folder() { return Make((g, p, b) => { g.FillRectangle(b, 2, 6, 16, 11); g.FillRectangle(b, 2, 4, 7, 3); }); }
        public static Bitmap Plane() { return Make((g, p, b) => { g.FillPolygon(b, new[] { new Point(2, 10), new Point(18, 3), new Point(13, 17), new Point(10, 12) }); }); }
        public static Bitmap File() { return Make((g, p, b) => { g.DrawRectangle(p, 5, 2, 10, 16); g.DrawLine(p, 8, 8, 12, 8); g.DrawLine(p, 8, 12, 12, 12); }); }
        public static Icon AppIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(30, 70, 120))) g.FillEllipse(b, 1, 1, 30, 30);
                using (var b = new SolidBrush(Color.FromArgb(150, 220, 255)))
                {
                    g.FillPolygon(b, new[] { new Point(6, 11), new Point(18, 11), new Point(18, 7), new Point(26, 14), new Point(18, 21), new Point(18, 17), new Point(6, 17) });
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    // Small status indicator: green LED + "Open" / dark (off) LED + "Closed"
    public class LedLabel : Control
    {
        bool open;
        public bool IsOpen { get { return open; } set { open = value; Invalidate(); } }

        public LedLabel() { DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(4, (Height - 16) / 2, 16, 16);
            using (var b = new SolidBrush(open ? Color.FromArgb(60, 225, 90) : Color.FromArgb(15, 15, 15))) g.FillEllipse(b, r);
            using (var p = new Pen(open ? Color.FromArgb(20, 120, 40) : Color.FromArgb(110, 110, 110), 1.5f)) g.DrawEllipse(p, r);
            if (open) using (var b = new SolidBrush(Color.FromArgb(200, 255, 210))) g.FillEllipse(b, 8, r.Top + 3, 4, 4);   // highlight
            using (var f = new Font("Segoe UI", 9F, FontStyle.Bold))
            using (var b = new SolidBrush(open ? Color.FromArgb(20, 120, 40) : Color.FromArgb(60, 60, 60)))
                g.DrawString(open ? "Open" : "Closed", f, b, 26, (Height - 16) / 2);
        }
    }

    // Banner: PC <-> PC-1600
    public class BannerPanel : Panel
    {
        public bool PortOpen, Sending, Receiving;

        public BannerPanel() { DoubleBuffered = true; ResizeRedraw = true; }

        static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            var gp = new GraphicsPath();
            gp.AddArc(x, y, 2 * r, 2 * r, 180, 90);
            gp.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
            gp.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
            gp.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        static void FillRound(Graphics g, Color c, float x, float y, float w, float h, float r)
        {
            using (var gp = Round(x, y, w, h, r)) using (var b = new SolidBrush(c)) g.FillPath(b, gp);
        }

        static void Text(Graphics g, string s, Font f, Color c, float cx, float y)
        {
            using (var b = new SolidBrush(c))
            {
                SizeF sz = g.MeasureString(s, f);
                g.DrawString(s, f, b, cx - sz.Width / 2, y);
            }
        }

        static void Arrow(Graphics g, int x1, int x2, int y, bool right, bool active)
        {
            Color c = active ? Color.FromArgb(130, 225, 255) : Color.FromArgb(70, 120, 175);
            Point[] pts = right
                ? new[] { new Point(x1, y - 5), new Point(x2 - 22, y - 5), new Point(x2 - 22, y - 12), new Point(x2, y), new Point(x2 - 22, y + 12), new Point(x2 - 22, y + 5), new Point(x1, y + 5) }
                : new[] { new Point(x2, y - 5), new Point(x1 + 22, y - 5), new Point(x1 + 22, y - 12), new Point(x1, y), new Point(x1 + 22, y + 12), new Point(x1 + 22, y + 5), new Point(x2, y + 5) };
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        static void Led(Graphics g, Color c, int cx, int y)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 5, y, 10, 10);
        }

        // Sharp PC-1600: light grey body, dark bezel with a wide LCD, function keys and QWERTY keyboard
        static void DrawPc1600(Graphics g, int kx, int top)
        {
            FillRound(g, Color.FromArgb(120, 120, 118), kx - 86, top + 10, 172, 82, 7);      // shadow edge
            FillRound(g, Color.FromArgb(208, 208, 203), kx - 85, top + 8, 170, 82, 7);       // body
            FillRound(g, Color.FromArgb(48, 50, 56), kx - 81, top + 11, 162, 35, 4);         // bezel
            using (var b = new SolidBrush(Color.FromArgb(170, 186, 152))) g.FillRectangle(b, kx - 62, top + 14, 120, 29);   // LCD
            using (var b = new SolidBrush(Color.FromArgb(55, 70, 55)))
                for (int r = 0; r < 4; r++)
                {
                    int w = new[] { 84, 104, 62, 96 }[r];
                    for (int c = 0; c < w; c += 3) g.FillRectangle(b, kx - 59 + c, top + 17 + r * 6, 2, 4);   // dotted text rows
                }
            using (var f = new Font("Arial", 5.5F, FontStyle.Bold)) Text(g, "SHARP", f, Color.White, kx + 71, top + 14);
            using (var b = new SolidBrush(Color.FromArgb(120, 125, 135)))
                for (int i = 0; i < 3; i++) g.FillRectangle(b, kx - 77, top + 17 + i * 8, 10, 5);      // side keys left of the LCD
            using (var b = new SolidBrush(Color.FromArgb(95, 98, 108)))
                for (int i = 0; i < 9; i++) g.FillRectangle(b, kx - 77 + i * 17, top + 50, 14, 5);    // function key row
            using (var b = new SolidBrush(Color.FromArgb(240, 240, 235)))
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 13; col++)
                        g.FillRectangle(b, kx - 77 + col * 12 + row * 3, top + 58 + row * 8, 10, 6);   // QWERTY rows
            using (var b = new SolidBrush(Color.FromArgb(240, 240, 235))) g.FillRectangle(b, kx - 30, top + 82, 60, 5);   // space bar
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (Width < 100 || Height < 60) return;
            var r = ClientRectangle;
            using (var br = new LinearGradientBrush(r, Color.FromArgb(14, 38, 72), Color.FromArgb(30, 70, 120), LinearGradientMode.Horizontal))
                g.FillRectangle(br, r);

            int pcx = Width * 22 / 100, kx = Width * 78 / 100, top = 12;

            // PC
            FillRound(g, Color.FromArgb(214, 208, 190), pcx - 48, top, 96, 70, 8);
            using (var b = new SolidBrush(Color.FromArgb(10, 25, 30))) g.FillRectangle(b, pcx - 40, top + 8, 80, 50);
            using (var f = new Font("Consolas", 16F, FontStyle.Bold)) Text(g, "PC", f, Color.FromArgb(90, 230, 120), pcx, top + 20);
            using (var b = new SolidBrush(Color.FromArgb(190, 184, 168))) g.FillRectangle(b, pcx - 14, top + 70, 28, 8);
            FillRound(g, Color.FromArgb(200, 194, 178), pcx - 56, top + 80, 112, 16, 4);
            using (var b = new SolidBrush(Color.FromArgb(120, 115, 105)))
                for (int i = 0; i < 10; i++) { g.FillRectangle(b, pcx - 50 + i * 10, top + 84, 7, 3); g.FillRectangle(b, pcx - 48 + i * 10, top + 89, 7, 3); }

            DrawPc1600(g, kx, top);

            // captions
            using (var f = new Font("Segoe UI", 9F)) { Text(g, "Windows PC", f, Color.White, pcx, top + 100); Text(g, "Sharp PC-1600", f, Color.White, kx, top + 100); }

            // centre: title, arrows, LEDs
            int mid = Width / 2, ax1 = pcx + 70, ax2 = kx - 92;
            using (var f = new Font("Segoe UI", 14F, FontStyle.Bold)) Text(g, "SerComet", f, Color.White, mid, top - 4);
            Arrow(g, ax1, ax2, top + 44, true, Sending);
            Arrow(g, ax1, ax2, top + 74, false, Receiving);
            Color ok = PortOpen ? Color.FromArgb(70, 220, 90) : Color.FromArgb(90, 90, 100);
            Led(g, ok, mid - 18, top + 96);
            Led(g, ok, mid, top + 96);
            Led(g, Sending || Receiving ? Color.FromArgb(240, 60, 50) : Color.FromArgb(90, 90, 100), mid + 18, top + 96);
        }
    }

    public partial class Form1 : Form
    {
        SerialPort port;
        readonly List<byte> rx = new List<byte>();
        readonly object rxLock = new object();
        int lastRx;
        bool sending;
        volatile bool rs232;

        BannerPanel banner;
        ComboBox comboPorts, comboBaud;
        Button btnRefresh, btnOpen, btnBrowse, btnOpenDir, btnSend, btnSendOther, btnRefreshFiles;
        TextBox txtDir, txtLog;
        ListBox lstFiles;
        Label lblHint;
        LedLabel led;
        RadioButton radio1500, radio1600;
        RadioButton radioSoft, radioRs;
        NumericUpDown numDelay;
        Timer timer;

        public Form1()
        {
            InitializeComponent();
            try { Icon = Icons.AppIcon(); } catch { }
            BuildUi();
            FormClosing += delegate { ClosePort(); };
            RefreshPorts();
            UpdateMode();
            Log("Ready. Select the port and click 'Open port'.");
        }

        Button Btn(string text, Bitmap img, int x, int y, int w, int h, AnchorStyles anchor)
        {
            return new Button
            {
                Text = "  " + text, Image = img, ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleCenter, Left = x, Top = y, Width = w, Height = h, Anchor = anchor
            };
        }

        void BuildUi()
        {
            const AnchorStyles TL = AnchorStyles.Top | AnchorStyles.Left;
            const AnchorStyles TR = AnchorStyles.Top | AnchorStyles.Right;
            const AnchorStyles TLR = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var lblPort = new Label { Text = "Port:", Left = 10, Top = 16, Width = 40 };
            comboPorts = new ComboBox { Left = 55, Top = 12, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            btnRefresh = Btn("Refresh", Icons.Refresh(), 165, 9, 100, 28, TL);
            btnOpen = Btn("Open port", Icons.Plug(), 275, 9, 130, 28, TL);
            led = new LedLabel { Left = 415, Top = 11, Width = 90, Height = 26 };
            var gbTarget = new GroupBox { Text = "Target computer", Left = 520, Top = 0, Width = 190, Height = 42, Anchor = TR };
            radio1500 = new RadioButton { Text = "PC-1500", Left = 10, Top = 16, Width = 75, Enabled = false };   // not available yet
            radio1600 = new RadioButton { Text = "PC-1600", Left = 100, Top = 16, Width = 80, Checked = true };
            gbTarget.Controls.AddRange(new Control[] { radio1500, radio1600 });

            banner = new BannerPanel { Left = 10, Top = 46, Width = 700, Height = 132, Anchor = TLR };

            var gb = new GroupBox { Text = "PC-1600 transmission", Left = 10, Top = 184, Width = 700, Height = 58, Anchor = TLR };
            radioSoft = new RadioButton { Text = "Software serial (19200 8N1, binary)", Left = 12, Top = 24, Width = 235, Checked = true };
            radioRs = new RadioButton { Text = "RS-232 (serial port, text)", Left = 250, Top = 24, Width = 155 };
            var lblBaud = new Label { Text = "Baud:", Left = 410, Top = 27, Width = 35 };
            comboBaud = new ComboBox { Left = 447, Top = 23, Width = 65, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (int b in new[] { 300, 600, 1200, 2400, 4800, 9600, 19200 }) comboBaud.Items.Add(b.ToString());
            comboBaud.SelectedItem = "9600";
            var lblDelay = new Label { Text = "Line delay (ms):", Left = 522, Top = 27, Width = 90 };
            numDelay = new NumericUpDown { Left = 614, Top = 23, Width = 70, Minimum = 0, Maximum = 5000, Value = 100, Increment = 50 };
            gb.Controls.AddRange(new Control[] { radioSoft, radioRs, lblBaud, comboBaud, lblDelay, numDelay });

            var lblDir = new Label { Text = "Folder:", Left = 10, Top = 256, Width = 50 };
            txtDir = new TextBox
            {
                Left = 65, Top = 252, Width = 440, Anchor = TLR,
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC1600")
            };
            btnBrowse = Btn("Browse...", Icons.Folder(), 515, 249, 105, 28, TR);
            btnOpenDir = Btn("Open", Icons.Folder(), 625, 249, 85, 28, TR);

            lblHint = new Label { Left = 10, Top = 284, Width = 700, Height = 48, Anchor = TLR, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(4) };

            lstFiles = new ListBox { Left = 10, Top = 340, Width = 540, Height = 110, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            btnSend = Btn("Send selected", Icons.Plane(), 560, 340, 150, 34, TR);
            btnSendOther = Btn("Send file...", Icons.File(), 560, 380, 150, 34, TR);
            btnRefreshFiles = Btn("Refresh list", Icons.Refresh(), 560, 420, 150, 30, TR);

            txtLog = new TextBox
            {
                Left = 10, Top = 460, Width = 700, Height = 130, Multiline = true,
                ScrollBars = ScrollBars.Vertical, ReadOnly = true, Font = new Font("Consolas", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            Controls.AddRange(new Control[] { lblPort, comboPorts, btnRefresh, btnOpen, led, gbTarget, banner, gb,
                lblDir, txtDir, btnBrowse, btnOpenDir, lblHint, lstFiles, btnSend, btnSendOther, btnRefreshFiles, txtLog });

            btnRefresh.Click += delegate { RefreshPorts(); };
            btnOpen.Click += delegate { TogglePort(); };
            radioSoft.CheckedChanged += delegate { UpdateMode(); };
            radioRs.CheckedChanged += delegate { UpdateMode(); };
            comboBaud.SelectedIndexChanged += delegate { ApplyBaud(); };
            btnBrowse.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.SelectedPath = txtDir.Text;
                    if (d.ShowDialog() == DialogResult.OK) { txtDir.Text = d.SelectedPath; RefreshFiles(); }
                }
            };
            btnOpenDir.Click += delegate
            {
                try { Directory.CreateDirectory(txtDir.Text); System.Diagnostics.Process.Start(txtDir.Text); }
                catch (Exception ex) { Log("Error: " + ex.Message); }
            };
            btnRefreshFiles.Click += delegate { RefreshFiles(); };
            btnSend.Click += delegate
            {
                if (lstFiles.SelectedItem == null) { Log("Select a file in the list."); return; }
                SendFile(Path.Combine(txtDir.Text, (string)lstFiles.SelectedItem));
            };
            btnSendOther.Click += delegate
            {
                using (var d = new OpenFileDialog())
                {
                    d.Filter = rs232 ? "BASIC text (*.bas;*.txt)|*.bas;*.txt|All files|*.*" : "PC-1600 files (*.sl16)|*.sl16|All files|*.*";
                    if (d.ShowDialog() == DialogResult.OK) SendFile(d.FileName);
                }
            };

            timer = new Timer { Interval = 100 };
            timer.Tick += TimerTick;
            timer.Start();
        }

        string Ext { get { return rs232 ? ".bas" : ".sl16"; } }
        int Baud { get { return rs232 ? int.Parse((string)comboBaud.SelectedItem) : 19200; } }

        void UpdateMode()
        {
            rs232 = radioRs.Checked;
            comboBaud.Enabled = numDelay.Enabled = rs232;
            lblHint.Text = rs232
                ? "RECEIVE: send the program as text from the PC-1600 through its RS-232 port (same baud rate on both sides). The file is saved after 3 s of silence, named after the first REM line.\r\nSEND: start receiving on the PC-1600 first, then press Send. Lines are sent one by one with the delay set above."
                : "RECEIVE: on the PC-1600 run SAVE (CALL addr,X). The file is saved automatically, named after the first REM.\r\nSEND: run LOAD on the PC-1600 first (CALL addr+176,X), then press Send (30 s to start).";
            ApplyBaud();
            RefreshFiles();
        }

        void ApplyBaud()
        {
            try { if (port != null && port.IsOpen) { port.BaudRate = Baud; SetStatus(true); } } catch (Exception ex) { Log("Error: " + ex.Message); }
        }

        void SetStatus(bool open)
        {
            banner.PortOpen = open;
            banner.Invalidate();
            led.IsOpen = open;
        }

        void Log(string s)
        {
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
        }

        void RefreshPorts()
        {
            string cur = comboPorts.SelectedItem as string;
            comboPorts.Items.Clear();
            foreach (string p in SerialPort.GetPortNames()) comboPorts.Items.Add(p);
            if (cur != null && comboPorts.Items.Contains(cur)) comboPorts.SelectedItem = cur;
            else if (comboPorts.Items.Count > 0) comboPorts.SelectedIndex = 0;
        }

        void RefreshFiles()
        {
            lstFiles.Items.Clear();
            try
            {
                if (Directory.Exists(txtDir.Text))
                    foreach (string f in Directory.GetFiles(txtDir.Text, "*" + Ext))
                        lstFiles.Items.Add(Path.GetFileName(f));
            }
            catch (Exception ex) { Log("List error: " + ex.Message); }
        }

        void TogglePort()
        {
            if (port != null) { ClosePort(); return; }
            string name = comboPorts.SelectedItem as string;
            if (name == null) { Log("No port selected."); return; }
            try
            {
                port = new SerialPort(name, Baud, Parity.None, 8, StopBits.One);
                port.Handshake = Handshake.None;
                port.ReadBufferSize = 65536;
                port.DataReceived += OnData;
                port.Open();
                btnOpen.Text = "  Close port";
                btnOpen.Image = Icons.Cross();
                SetStatus(true);
                Log("Port " + name + " open (" + port.BaudRate + " 8N1).");
            }
            catch (Exception ex)
            {
                Log("Cannot open the port: " + ex.Message);
                port = null;
            }
        }

        void ClosePort()
        {
            if (port == null) return;
            try { port.DataReceived -= OnData; if (port.IsOpen) port.Close(); } catch { }
            port = null;
            btnOpen.Text = "  Open port";
            btnOpen.Image = Icons.Plug();
            banner.PortOpen = false;
            banner.Invalidate();
            led.IsOpen = false;
        }

        void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var p = port;
                if (p == null) return;
                int n = p.BytesToRead;
                if (n <= 0) return;
                var buf = new byte[n];
                n = p.Read(buf, 0, n);
                if (sending) return;
                lock (rxLock)
                {
                    for (int i = 0; i < n; i++) rx.Add(buf[i]);
                    lastRx = Environment.TickCount;
                }
            }
            catch { }
        }

        // end of transmission = silence: 0.8 s (software serial, SAVE sends without gaps), 3 s (RS-232 text)
        void TimerTick(object sender, EventArgs e)
        {
            byte[] data = null;
            bool receiving = false;
            lock (rxLock)
            {
                int quiet = unchecked(Environment.TickCount - lastRx);
                receiving = rx.Count > 0;
                if (rx.Count > 0 && quiet > (rs232 ? 3000 : 800))
                {
                    data = rx.ToArray();
                    rx.Clear();
                    receiving = false;
                }
            }
            if (banner.Receiving != receiving || banner.Sending != sending)
            {
                banner.Receiving = receiving;
                banner.Sending = sending;
                banner.Invalidate();
            }
            if (data != null) Received(data);
        }

        void Received(byte[] data)
        {
            try
            {
                Directory.CreateDirectory(txtDir.Text);
                if (rs232)
                {
                    string name = Sl16.NameFromText(data) ?? "PROGRAM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string path = UniquePath(name, ".bas");
                    File.WriteAllBytes(path, data);
                    Log("RECEIVED " + data.Length + " B (text) -> " + Path.GetFileName(path));
                    RefreshFiles();
                    return;
                }
                string err;
                if (Sl16.Check(data, out err))
                {
                    string name = Sl16.NameFromRem(data) ?? "PROGRAM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string path = UniquePath(name, ".sl16");
                    File.WriteAllBytes(path, data);
                    Log("RECEIVED " + data.Length + " B, " + Sl16.CountLines(data) + " lines, checksum OK -> " + Path.GetFileName(path));
                    RefreshFiles();
                }
                else
                {
                    string path = UniquePath("ERROR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), ".bad");
                    File.WriteAllBytes(path, data);
                    Log("RECEIVE ERROR (" + err + "), " + data.Length + " B saved as " + Path.GetFileName(path) + ". Repeat SAVE.");
                }
            }
            catch (Exception ex) { Log("Write error: " + ex.Message); }
        }

        string UniquePath(string name, string ext)
        {
            string path = Path.Combine(txtDir.Text, name + ext);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(txtDir.Text, name + "_" + i + ext);
            return path;
        }

        async void SendFile(string path)
        {
            if (port == null || !port.IsOpen) { Log("Open the port first."); return; }
            if (sending) return;
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { Log("Read error: " + ex.Message); return; }
            bool text = rs232;
            int delay = (int)numDelay.Value;
            if (!text)
            {
                string err;
                if (!Sl16.Check(data, out err)) { Log("File rejected: " + err); return; }
            }
            sending = true;
            btnSend.Enabled = btnSendOther.Enabled = false;
            Log("SENDING " + Path.GetFileName(path) + " (" + data.Length + " B)...");
            try
            {
                var p = port;
                await Task.Run(() =>
                {
                    p.DiscardInBuffer();
                    if (!text)
                    {
                        p.Write(data, 0, data.Length);
                    }
                    else
                    {
                        // text: line by line, every line ends with CR
                        var line = new List<byte>();
                        for (int i = 0; i <= data.Length; i++)
                        {
                            if (i == data.Length || data[i] == 0x0D || data[i] == 0x0A)
                            {
                                if (line.Count > 0)
                                {
                                    line.Add(0x0D);
                                    p.Write(line.ToArray(), 0, line.Count);
                                    line.Clear();
                                    while (p.BytesToWrite > 0) System.Threading.Thread.Sleep(5);
                                    System.Threading.Thread.Sleep(delay);
                                }
                            }
                            else line.Add(data[i]);
                        }
                    }
                    while (p.BytesToWrite > 0) System.Threading.Thread.Sleep(10);
                });
                Log(text ? "Sent." : "Sent. The PC-1600 finishes after 0.5 s of silence; check X=1 (PRINT X) and LIST.");
            }
            catch (Exception ex) { Log("Send error: " + ex.Message); }
            finally
            {
                lock (rxLock) { rx.Clear(); }
                sending = false;
                btnSend.Enabled = btnSendOther.Enabled = true;
            }
        }
    }
}
