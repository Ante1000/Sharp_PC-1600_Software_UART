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
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 9000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };
        TerminalForm term;
        Button btnTerminal;
        Button btnRefresh, btnOpen, btnBrowse, btnOpenDir, btnSend, btnSendOther, btnRefreshFiles;
        TextBox txtDir, txtLog;
        ListBox lstFiles;
        Label lblHint;
        LedLabel led;
        RadioButton radio1500, radio1600, radioOther;
        RadioButton radioSoft, radioRs, radioSio;
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
                Text = text, Image = img, ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 6, 0), Left = x, Top = y, Width = w, Height = h, Anchor = anchor
            };
        }

        void BuildUi()
        {
            const AnchorStyles TL = AnchorStyles.Top | AnchorStyles.Left;
            const AnchorStyles TR = AnchorStyles.Top | AnchorStyles.Right;
            const AnchorStyles TLR = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var lblPort = new Label { Text = "Port:", Left = 20, Top = 16, Width = 32 };
            comboPorts = new ComboBox { Left = 55, Top = 12, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            btnRefresh = Btn("Refresh", Icons.Refresh(), 165, 9, 85, 28, TL);
            btnOpen = Btn("Open port", Icons.Plug(), 258, 9, 85, 28, TL);
            led = new LedLabel { Left = 348, Top = 11, Width = 85, Height = 26 };
            var gbTarget = new GroupBox { Text = "Target computer", Left = 438, Top = 0, Width = 272, Height = 42, Anchor = TR };
            radio1500 = new RadioButton { Text = "PC-1500", Left = 8, Top = 16, Width = 70 };
            radio1600 = new RadioButton { Text = "PC-1600", Left = 80, Top = 16, Width = 70, Checked = true };
            radioOther = new RadioButton { Text = "Other 15-pin SIO", Left = 152, Top = 16, Width = 116 };
            gbTarget.Controls.AddRange(new Control[] { radio1500, radio1600, radioOther });

            banner = new BannerPanel { Left = 10, Top = 46, Width = 700, Height = 132, Anchor = TLR };

            var gb = new GroupBox { Text = "Transmission mode", Left = 10, Top = 184, Width = 700, Height = 58, Anchor = TLR };
            radioSoft = new RadioButton { Text = "Software Serial", Left = 10, Checked = true };
            radioRs = new RadioButton { Text = "RS-232 - serial interface", Left = 130 };
            radioSio = new RadioButton { Text = "TTL-UART", Left = 300, Enabled = false };
            var lblBaud = new Label { Text = "Baud:", Left = 425, Top = 27, Width = 35 };
            comboBaud = new ComboBox { Left = 462, Top = 23, Width = 62, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (int b in new[] { 300, 600, 1200, 2400, 4800, 9600, 19200 }) comboBaud.Items.Add(b.ToString());
            comboBaud.SelectedItem = "9600";
            var lblDelay = new Label { Text = "Line delay (ms):", Left = 532, Top = 27, Width = 82 };
            numDelay = new NumericUpDown { Left = 616, Top = 23, Width = 45, Minimum = 0, Maximum = 5000, Value = 100, Increment = 50 };
            foreach (RadioButton rb in new[] { radioSoft, radioRs, radioSio })
            {
                rb.AutoSize = true;      // identical height and text baseline for all three
                rb.Top = 24;
            }
            gb.Controls.AddRange(new Control[] { radioSoft, radioRs, radioSio, lblBaud, comboBaud, lblDelay, numDelay });

            var lblDir = new Label { Text = "Folder:", Left = 10, Top = 256, Width = 50 };
            txtDir = new TextBox
            {
                Left = 65, Top = 252, Width = 460, Anchor = TLR,
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC1600")
            };
            btnBrowse = Btn("Browse...", Icons.Folder(), 530, 249, 92, 28, TR);
            btnOpenDir = Btn("Open", Icons.Folder(), 628, 249, 82, 28, TR);

            var gbInfo = new GroupBox { Text = "Info", Left = 10, Top = 280, Width = 700, Height = 84, Anchor = TLR };
            lblHint = new Label { Left = 8, Top = 14, Width = 684, Height = 67, Anchor = TLR };
            gbInfo.Controls.Add(lblHint);

            lstFiles = new ListBox { Left = 10, Top = 366, Width = 565, Height = 110, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            btnSend = Btn("Send selected", Icons.Plane(), 585, 366, 125, 30, TR);
            btnSendOther = Btn("Send file...", Icons.File(), 585, 400, 125, 30, TR);
            btnRefreshFiles = Btn("Refresh list", Icons.Refresh(), 585, 434, 125, 30, TR);
            btnTerminal = Btn("Terminal", Icons.Terminal(), 585, 468, 125, 28, TR);

            txtLog = new TextBox
            {
                Left = 10, Top = 502, Width = 700, Height = 114, Multiline = true,
                ScrollBars = ScrollBars.Vertical, ReadOnly = true, Font = new Font("Consolas", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            Controls.AddRange(new Control[] { lblPort, comboPorts, btnRefresh, btnOpen, led, gbTarget, banner, gb,
                lblDir, txtDir, btnBrowse, btnOpenDir, gbInfo, lstFiles, btnSend, btnSendOther, btnRefreshFiles, btnTerminal, txtLog });

            // title row: everything else moves down by one row (40 px)
            foreach (Control c in Controls) c.Top += 40;
            {
                const string titleText = "FILE TRANSFER TOOL FOR PC AND SHARP POCKET COMPUTERS";
                var titleFont = new Font("Segoe UI", 11F, FontStyle.Bold);
                Size ts = TextRenderer.MeasureText(titleText, titleFont);
                int x0 = (ClientSize.Width - (32 + 8 + ts.Width)) / 2;
                Controls.Add(new PictureBox { Image = Icons.Comet(32), SizeMode = PictureBoxSizeMode.AutoSize, Left = x0, Top = 4, Anchor = AnchorStyles.Top });
                Controls.Add(new Label
                {
                    Text = titleText, AutoSize = true, Font = titleFont, ForeColor = Color.FromArgb(30, 70, 120),
                    Left = x0 + 40, Top = 4 + (32 - ts.Height) / 2, Anchor = AnchorStyles.Top
                });
                var about = new LinkLabel { Text = "About", AutoSize = true, Left = 665, Top = 12, Anchor = AnchorStyles.Top | AnchorStyles.Right };
                about.LinkClicked += delegate { using (var d = new AboutForm()) d.ShowDialog(this); };
                tips.SetToolTip(about, "About SerComet: author, license and project page.");
                Controls.Add(about);
            }


            btnRefresh.Click += delegate { RefreshPorts(); };
            btnOpen.Click += delegate { TogglePort(); };
            radioSoft.CheckedChanged += delegate { UpdateMode(); };
            radioRs.CheckedChanged += delegate { UpdateMode(); };
            radioSio.CheckedChanged += delegate { UpdateMode(); };
            radio1500.CheckedChanged += delegate { if (radio1500.Checked) SetTarget(); };
            radio1600.CheckedChanged += delegate { if (radio1600.Checked) SetTarget(); };
            radioOther.CheckedChanged += delegate { if (radioOther.Checked) SetTarget(); };
            comboBaud.SelectedIndexChanged += delegate { if (comboBaud.Enabled) savedBaud = (string)comboBaud.SelectedItem; ApplyBaud(); };
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
            btnTerminal.Click += delegate { OpenTerminal(); };
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

            tips.SetToolTip(comboPorts, "Select a COM port, or the virtual COM port created by a USB adapter (FTDI)");
            tips.SetToolTip(btnRefresh, "Re-scan the list of available COM ports.");
            tips.SetToolTip(btnOpen, "Open or close the selected COM port.");
            tips.SetToolTip(led, "Port status: green = open, dark = closed.");
            tips.SetToolTip(radio1500, "Target: Sharp PC-1500 (RS-232 through the CE-158(X) interface).");
            tips.SetToolTip(radio1600, "Target: Sharp PC-1600 (software serial Saver/Loader or its RS-232 port).");
            tips.SetToolTip(radioOther, "Target: other Sharp pocket computers with the 15-pin connector (TTL-UART SIO).");
            tips.SetToolTip(radioSoft, "Software UART in the Sharp (machine-language Saver/Loader), binary .sl16 files, fixed speed.");
            tips.SetToolTip(radioRs, "Classic RS-232 text transfer, like a terminal program. The baud rate is selectable.");
            tips.SetToolTip(radioSio, "TTL-UART SIO text transfer through the 15-pin connector. The baud rate is selectable.");
            tips.SetToolTip(comboBaud, "Baud rate (8N1). Fixed and greyed out for Software Serial, selectable for the text modes.");
            tips.SetToolTip(numDelay, "Pause after each line sent from a text file, in ms (the Sharp needs time to store a line).\r\n" +
                "Suggested starting values (tune by testing, raise it if lines are lost or garbled):\r\n" +
                "  PC-1500 (CE-158): 200 ms\r\n  PC-1600: 100 ms\r\n  15-pin models: 100 ms\r\n" +
                "The value is preset when you choose the target computer.\r\n" +
                "Note: a published PC-1600 setup (9600 baud, XON/XOFF) uses a delay of 2/60 s per character and 0 per line; this program delays per line only.");
            tips.SetToolTip(txtDir, "Folder where received files are saved and listed from.");
            tips.SetToolTip(btnBrowse, "Choose another folder.");
            tips.SetToolTip(btnOpenDir, "Open the folder in Windows Explorer.");
            tips.SetToolTip(lstFiles, "Files in the folder for the current mode (.sl16 or .bas). Select one to send it.");
            tips.SetToolTip(btnSend, "Send the selected file to the Sharp. Start the Loader / receiver on the Sharp first.");
            tips.SetToolTip(btnSendOther, "Pick any file from the disk and send it to the Sharp.");
            tips.SetToolTip(btnRefreshFiles, "Re-read the folder and refresh the file list.");
            tips.SetToolTip(btnTerminal, "Open a terminal window (like TeraTerm): typed characters are sent, received data is shown. Text modes only.");
            tips.SetToolTip(txtLog, "Log of transfers and messages.");

            timer = new Timer { Interval = 100 };
            timer.Tick += TimerTick;
            timer.Start();
        }

        // target computer: default folder and the available transmission modes
        void SetTarget()
        {
            bool other = radioOther.Checked;
            bool p1500 = radio1500.Checked;
            radioSoft.Enabled = radioRs.Enabled = !other;
            radioSio.Enabled = other;
            radioRs.Text = "RS-232 - serial interface";
            if (other) radioSio.Checked = true;
            else if (radioSio.Checked) radioSoft.Checked = true;
            banner.ModelName = other ? "Sharp with 15-pin connector" : p1500 ? "Sharp PC-1500" : "Sharp PC-1600";
            banner.Invalidate();
            numDelay.Value = p1500 ? 200 : 100;   // suggested starting values, adjust by testing
            SetDefaultDir(other ? "PC_15-pin" : p1500 ? "PC1500" : "PC1600");
            UpdateMode();
        }

        string HintText()
        {
            if (radioSio.Checked)
                return "Compatible models: PC-1350, PC-1360, PC-1450..90, PC-E500..650 series, PC-U6000, PC-2500 with 15-pin connector, TTL-UART SIO through a USB/COM adapter (like TeraTerm), text transfer (.bas files). RECEIVE: send the program as text, the file is saved after 3 s of silence. SEND: start receiving on the pocket computer, then press Send.";
            if (radio1500.Checked && radioRs.Checked)
                return "PC-1500 with the CE-158(X) RS-232 interface, text transfer (.bas files), same baud rate on both sides. RECEIVE: the file is saved after 3 s of silence, named after the first REM line.\r\nSEND: start receiving on the PC-1500 first, then press Send. Lines are sent one by one with the delay set above.";
            if (radio1500.Checked)
                return "PC-1500 software serial (Saver/Loader for the PC-1500) is not implemented yet. Use RS-232 - CE-158(X).";
            if (radioRs.Checked)
                return "RS-232 serial port of the PC-1600, text transfer (.bas files), same baud rate on both sides. RECEIVE: send the program as text from the PC-1600. The file is saved after 3 s of silence, named after the first REM line.\r\nSEND: start receiving on the PC-1600 first, then press Send. Lines are sent one by one with the delay set above.";
            return "Software serial: 19200 8N1, binary transfer (.sl16 files). RECEIVE: on the PC-1600 run SAVE (CALL addr,X). The file is saved automatically, named after the first REM.\r\nSEND: run LOAD on the PC-1600 first (CALL addr+176,X), then press Send (30 s to start).";
        }

        void SetDefaultDir(string sub)
        {
            txtDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), sub);
            RefreshFiles();
            Log("Folder: " + txtDir.Text);
        }

        void OpenTerminal()
        {
            if (port == null || !port.IsOpen) { Log("Open the port first."); return; }
            if (!rs232) { Log("The terminal works in RS-232 and TTL-UART modes."); return; }
            if (term != null) { term.Activate(); return; }
            lock (rxLock) { rx.Clear(); }
            term = new TerminalForm("SerComet Terminal - " + port.PortName + " " + port.BaudRate + " 8N1", SendRaw);
            term.FormClosed += delegate { term = null; Log("Terminal closed."); };
            term.Show(this);
            Log("Terminal open: typed characters are sent immediately, received data is shown live.");
        }

        void SendRaw(byte[] b)
        {
            try { if (port != null && port.IsOpen) port.Write(b, 0, b.Length); }
            catch (Exception ex) { Log("Send error: " + ex.Message); }
        }

        string Ext { get { return rs232 ? ".bas" : ".sl16"; } }
        string savedBaud = "9600";   // baud chosen by the user for the text modes
        int Baud { get { return rs232 ? int.Parse((string)comboBaud.SelectedItem) : (radio1500.Checked ? 9600 : 19200); } }

        void UpdateMode()
        {
            rs232 = radioRs.Checked || radioSio.Checked;
            comboBaud.Enabled = numDelay.Enabled = btnTerminal.Enabled = rs232;
            // greyed-out combo still shows the real speed of the mode: software serial = 19200 (PC-1600) or 9600 (PC-1500)
            comboBaud.SelectedItem = rs232 ? savedBaud : (radio1500.Checked ? "9600" : "19200");
            if (!rs232 && term != null) term.Close();
            lblHint.Text = HintText();
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
                btnOpen.Text = "Close port";
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
            if (term != null) term.Close();
            try { port.DataReceived -= OnData; if (port.IsOpen) port.Close(); } catch { }
            port = null;
            btnOpen.Text = "Open port";
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
                if (n != buf.Length) Array.Resize(ref buf, n);
                var t = term;
                if (t != null)
                {
                    try { t.BeginInvoke(new Action(() => t.Rx(buf))); } catch { }
                    return;
                }
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
            if (term != null) { Log("Close the terminal first."); return; }
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
        public static Bitmap Terminal() { return Make((g, p, b) => { g.DrawRectangle(p, 2, 3, 16, 13); g.DrawLine(p, 5, 7, 8, 10); g.DrawLine(p, 5, 13, 8, 10); g.DrawLine(p, 10, 13, 14, 13); }); }
        public static Bitmap Plane() { return Make((g, p, b) => { g.FillPolygon(b, new[] { new Point(2, 10), new Point(18, 3), new Point(13, 17), new Point(10, 12) }); }); }
        public static Bitmap File() { return Make((g, p, b) => { g.DrawRectangle(p, 5, 2, 10, 16); g.DrawLine(p, 8, 8, 12, 8); g.DrawLine(p, 8, 12, 12, 12); }); }
        // shooting star (yellow star head, curved light-blue forked tail) on a navy sky, drawn at any size
        public static Bitmap Comet(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32F, size / 32F);
                using (var b = new SolidBrush(Color.FromArgb(18, 40, 80))) g.FillEllipse(b, 0, 0, 31, 31);
                using (var b = new SolidBrush(Color.White)) { g.FillRectangle(b, 5, 22, 1, 1); g.FillRectangle(b, 24, 5, 1, 1); g.FillRectangle(b, 12, 26, 1, 1); }

                using (var tail = new GraphicsPath())
                {
                    tail.AddBezier(3, 9, 10, 4, 19, 7, 22, 19);        // upper edge
                    tail.AddLine(22, 19, 21, 23);
                    tail.AddBezier(21, 23, 17, 12, 10, 12, 6, 18);     // lower edge
                    tail.AddLine(6, 18, 8, 12.5F);                      // fork
                    tail.CloseFigure();
                    using (var b = new SolidBrush(Color.FromArgb(135, 205, 255))) g.FillPath(b, tail);
                }

                var star = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    double ang = -Math.PI / 2 + i * Math.PI / 5 + 0.2;
                    double r = i % 2 == 0 ? 7.0 : 3.0;
                    star[i] = new PointF((float)(23 + r * Math.Cos(ang)), (float)(22 + r * Math.Sin(ang)));
                }
                using (var b = new SolidBrush(Color.FromArgb(255, 226, 100))) g.FillPolygon(b, star);
            }
            return bmp;
        }

        // application icon (window title bar and taskbar)
        public static Icon AppIcon()
        {
            using (var bmp = Comet(32)) return Icon.FromHandle(bmp.GetHicon());
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
        Bitmap cometImg;
        public string ModelName = "Sharp PC-1600";

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

        // Sharp PC-1600 (as on the photo): light body, LCD on the left (2/3), dark brown panel with the logo on the right (1/3),
        // dark QWERTY keys under the LCD, dark numeric keypad under the brown panel
        static void DrawPc1600(Graphics g, int kx, int top)
        {
            var state = g.Save();
            g.TranslateTransform(kx, top + 60);          // whole icon scaled to 70 %
            g.ScaleTransform(0.7F, 0.7F);
            g.TranslateTransform(-kx, -(top + 60));
            FillRound(g, Color.FromArgb(150, 150, 146), kx - 86, top + 10, 172, 82, 6);      // shadow edge
            FillRound(g, Color.FromArgb(226, 226, 220), kx - 85, top + 8, 170, 82, 6);       // body
            using (var b = new SolidBrush(Color.FromArgb(178, 194, 122))) g.FillRectangle(b, kx - 81, top + 12, 108, 30);   // LCD, no bezel
            using (var p = new Pen(Color.FromArgb(130, 145, 90))) g.DrawRectangle(p, kx - 81, top + 12, 108, 30);
            using (var b = new SolidBrush(Color.FromArgb(70, 50, 42))) g.FillRectangle(b, kx + 31, top + 12, 51, 21);       // brown panel (30 % lower)
            using (var f = new Font("Arial", 5.5F, FontStyle.Bold)) Text(g, "SHARP", f, Color.FromArgb(230, 225, 215), kx + 56, top + 16);

            Color dark = Color.FromArgb(48, 48, 54), dot = Color.FromArgb(215, 215, 210);
            using (var bd = new SolidBrush(dark))
            using (var bl = new SolidBrush(dot))
            using (var br = new SolidBrush(Color.FromArgb(190, 50, 45)))
            using (var bg = new SolidBrush(Color.FromArgb(190, 190, 184)))
            {
                // QWERTY rows under the LCD (left 2/3)
                int[] cnt = { 10, 9, 10 };
                int[] off = { 0, 5, 0 };
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < cnt[row]; col++)
                    {
                        int x = kx - 80 + off[row] + col * 10;
                        g.FillRectangle(bd, x, top + 47 + row * 9, 8, 6);
                        g.FillRectangle(bl, x + 3, top + 49 + row * 9, 2, 2);
                    }
                // bottom row: red key, light keys, wide dark space bar
                g.FillRectangle(br, kx - 80, top + 74, 8, 6);
                for (int i = 0; i < 3; i++) g.FillRectangle(bg, kx - 70 + i * 10, top + 74, 8, 6);
                g.FillRectangle(bd, kx - 38, top + 74, 30, 6);
                for (int i = 0; i < 3; i++) g.FillRectangle(bg, kx - 6 + i * 10, top + 74, 8, 6);

                // keypad under the brown panel (right 1/3): small top row + 4 rows x 5 keys
                for (int col = 0; col < 5; col++)
                    g.FillRectangle(col == 1 ? br : bd, kx + 32 + col * 10, top + 45, 8, 6);
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 5; col++)
                    {
                        int x = kx + 32 + col * 10, y = top + 54 + row * 8;
                        g.FillRectangle(row == 0 && col == 4 ? br : bd, x, y, 8, 6);
                        g.FillRectangle(bl, x + 3, y + 2, 2, 2);
                    }
            }
            g.Restore(state);
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
            using (var f = new Font("Segoe UI", 9F)) { Text(g, "Windows PC", f, Color.White, pcx, top + 100); Text(g, ModelName, f, Color.White, kx, top + 100); }

            // centre: title, arrows, LEDs
            int mid = Width / 2, ax1 = pcx + 70, ax2 = kx - 92;
            using (var f = new Font("Segoe UI", 14F, FontStyle.Bold))
            {
                Text(g, "SerComet", f, Color.White, mid, top - 4);
                if (cometImg == null) cometImg = Icons.Comet(28);
                g.DrawImage(cometImg, mid - g.MeasureString("SerComet", f).Width / 2 - 32, top - 6);
            }
            Arrow(g, ax1, ax2, top + 44, true, Sending);
            Arrow(g, ax1, ax2, top + 74, false, Receiving);
            Color ok = PortOpen ? Color.FromArgb(70, 220, 90) : Color.FromArgb(90, 90, 100);
            Led(g, ok, mid - 18, top + 96);
            Led(g, ok, mid, top + 96);
            Led(g, Sending || Receiving ? Color.FromArgb(240, 60, 50) : Color.FromArgb(90, 90, 100), mid + 18, top + 96);
        }
    }

    // Simple terminal (like TeraTerm): typed characters go out at once, received data is shown live
    public class TerminalForm : Form
    {
        readonly TextBox txt;
        readonly CheckBox chkEcho;
        readonly ComboBox comboNl;
        readonly Action<byte[]> send;
        bool lastCr;

        public TerminalForm(string title, Action<byte[]> send)
        {
            this.send = send;
            Text = title;
            ClientSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = Icons.AppIcon(); } catch { }

            var top = new Panel { Dock = DockStyle.Top, Height = 34 };
            chkEcho = new CheckBox { Text = "Local echo", Left = 8, Top = 8, Width = 90 };
            var lbl = new Label { Text = "Enter sends:", Left = 110, Top = 11, Width = 72 };
            comboNl = new ComboBox { Left = 185, Top = 6, Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
            comboNl.Items.AddRange(new object[] { "CR", "CR+LF", "LF" });
            comboNl.SelectedIndex = 0;
            var btnClear = new Button { Text = "Clear", Left = 270, Top = 5, Width = 70, Height = 24 };
            btnClear.Click += delegate { txt.Clear(); txt.Focus(); };
            top.Controls.AddRange(new Control[] { chkEcho, lbl, comboNl, btnClear });
            var tt = new ToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
            tt.SetToolTip(chkEcho, "Show typed characters locally (use it when the Sharp does not echo them).");
            tt.SetToolTip(comboNl, "Characters sent when you press Enter.");
            tt.SetToolTip(btnClear, "Clear the terminal window.");

            txt = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10F), ReadOnly = true, WordWrap = true };
            txt.BackColor = Color.Black;
            txt.ForeColor = Color.Gainsboro;
            txt.KeyPress += OnKeyPress;
            txt.KeyDown += OnKeyDown;
            Controls.Add(txt);
            Controls.Add(top);
            Shown += delegate { txt.Focus(); };
        }

        string Newline() { return comboNl.SelectedIndex == 1 ? "\r\n" : comboNl.SelectedIndex == 2 ? "\n" : "\r"; }

        void SendChars(string t)
        {
            var b = new List<byte>();
            foreach (char c in t) b.Add(c < 256 ? (byte)c : (byte)'?');
            if (b.Count == 0) return;
            send(b.ToArray());
            if (chkEcho.Checked) Rx(b.ToArray());
        }

        void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true;
            char c = e.KeyChar;
            if (c == '\r') SendChars(Newline());
            else if (c == '\b' || c == '\t' || c >= 32) SendChars(c.ToString());
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsText())
            {
                e.SuppressKeyPress = true;
                SendChars(Clipboard.GetText().Replace("\r\n", "\n").Replace("\n", Newline()));
            }
        }

        // received bytes -> text (CR or LF = new line, BS erases, other control codes are ignored)
        public void Rx(byte[] data)
        {
            var sb = new StringBuilder();
            foreach (byte c in data)
            {
                if (c == 13) { sb.Append("\r\n"); lastCr = true; continue; }
                if (c == 10) { if (!lastCr) sb.Append("\r\n"); lastCr = false; continue; }
                lastCr = false;
                if (c == 8) { if (sb.Length > 0) sb.Length--; }
                else if (c == 9 || c >= 32) sb.Append((char)c);
            }
            txt.AppendText(sb.ToString());
            if (txt.TextLength > 200000) { txt.Text = txt.Text.Substring(txt.TextLength - 100000); txt.SelectionStart = txt.TextLength; txt.ScrollToCaret(); }
        }
    }

    // About window
    public class AboutForm : Form
    {
        public AboutForm()
        {
            Text = "About SerComet";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 300);

            Controls.Add(new PictureBox { Image = Icons.Comet(72), SizeMode = PictureBoxSizeMode.AutoSize, Left = (380 - 72) / 2, Top = 16 });
            AddLine("SerComet", 100, new Font("Segoe UI", 16F, FontStyle.Bold), Color.FromArgb(30, 70, 120));
            AddLine("File transfer tool for PC and Sharp pocket computers", 138, new Font("Segoe UI", 9F), Color.Black);
            AddLine("Author: Alex F4VTS", 170, new Font("Segoe UI", 10F), Color.Black);
            AddLine("Copyright \u00A9 2026", 194, new Font("Segoe UI", 10F), Color.Black);
            AddLine("License: GNU General Public License v3.0", 218, new Font("Segoe UI", 10F), Color.Black);
            var web = new LinkLabel { Text = "Web: github.com/Ante1000", Left = 0, Top = 242, Width = 380, Height = 20, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10F) };
            web.Links.Add(5, 19, "https://github.com/Ante1000");
            web.LinkClicked += delegate (object s, LinkLabelLinkClickedEventArgs e)
            {
                try { System.Diagnostics.Process.Start((string)e.Link.LinkData); } catch { }
            };
            Controls.Add(web);
            var tt = new ToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
            tt.SetToolTip(web, "Open the project page in the browser.");
            var ok = new Button { Text = "OK", Left = (380 - 80) / 2, Top = 268, Width = 80, DialogResult = DialogResult.OK };
            Controls.Add(ok);
            AcceptButton = ok;
            tt.SetToolTip(ok, "Close this window.");
        }

        void AddLine(string text, int top, Font font, Color color)
        {
            Controls.Add(new Label { Text = text, Left = 0, Top = top, Width = 380, Height = 24, TextAlign = ContentAlignment.MiddleCenter, Font = font, ForeColor = color });
        }
    }
}
