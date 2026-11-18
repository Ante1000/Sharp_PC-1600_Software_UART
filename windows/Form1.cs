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
        volatile bool abortRequested;
        bool sending, blinkPhase;
        int lastRxAny, lastTxAny;     // time of the last received / sent data (also in the terminal)
        volatile bool rs232;

        BannerPanel banner;
        CheckBox chkEof;
        Button btnView;
        ComboBox comboPorts, comboBaud, comboEol, comboFlow;
        CheckBox chkAuto;
        readonly WrapToolTip tips = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };
        TerminalForm term;
        Button btnTerminal, btnAbort, btnConvert, btnLocations, btnDelete;
        // default folders of the target computers: PC-1500, PC-1600, other 15-pin models
        string[] locs = LoadLocations();
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
            radioSoft = new RadioButton { Text = "Software Serial", Checked = true };
            radioRs = new RadioButton { Text = "RS-232 serial" };
            radioSio = new RadioButton { Text = "TTL-UART", Enabled = false };
            var lblBaud = new Label { Text = "Baud:" };
            comboBaud = new ComboBox { Width = 56, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (int b in new[] { 300, 600, 1200, 2400, 4800, 9600, 19200 }) comboBaud.Items.Add(b.ToString());
            comboBaud.SelectedItem = "9600";
            var lblDelay = new Label { Text = "Delay:" };
            numDelay = new NumericUpDown { Width = 42, Minimum = 0, Maximum = 5000, Value = 100, Increment = 50 };
            var lblEol = new Label { Text = "EOL:" };
            comboEol = new ComboBox { Width = 50, DropDownStyle = ComboBoxStyle.DropDownList };
            comboEol.Items.AddRange(new object[] { "CR", "CR+LF", "LF" });
            comboEol.SelectedIndex = 1;
            var lblFlow = new Label { Text = "Flow:" };
            comboFlow = new ComboBox { Width = 74, DropDownStyle = ComboBoxStyle.DropDownList };
            comboFlow.Items.AddRange(new object[] { "None", "XON/XOFF", "RTS/CTS", "DSR/DTR" });
            comboFlow.SelectedIndex = 1;       // XON/XOFF, as it was fixed before
            foreach (RadioButton rb in new[] { radioSoft, radioRs, radioSio }) rb.AutoSize = true;   // identical height and baseline
            foreach (Label l in new[] { lblBaud, lblDelay, lblEol, lblFlow }) l.AutoSize = true;
            // one row, evenly spread over the box: a label sits right next to its field, the remaining
            // space is divided into equal gaps between the groups (radio, radio, radio, Baud, Delay, EOL, Flow)
            var groups = new Control[][] {
                new Control[] { radioSoft }, new Control[] { radioRs }, new Control[] { radioSio },
                new Control[] { lblBaud, comboBaud }, new Control[] { lblDelay, numDelay },
                new Control[] { lblEol, comboEol }, new Control[] { lblFlow, comboFlow } };
            Func<Control, int> wid = c => c is RadioButton || c is Label ? c.PreferredSize.Width : c.Width;
            int used = 0;
            foreach (var g in groups) foreach (Control c in g) used += wid(c) + (c is Label ? 1 : 0);
            int left = 8, right = gb.Width - 8;                      // the last field ends exactly at the right edge (like the Open button below)
            int gap = Math.Max(0, (right - left - used) / (groups.Length - 1));
            int x = left;
            foreach (var g in groups)
            {
                if (g == groups[groups.Length - 1])             // last group flush with the right edge
                {
                    int lastW = 0;
                    foreach (Control c in g) lastW += wid(c) + (c is Label ? 1 : 0);
                    x = right - lastW;
                }
                foreach (Control c in g)
                {
                    c.Left = x;
                    c.Top = c is RadioButton ? 24 : c is Label ? 27 : 23;
                    x += wid(c) + (c is Label ? 1 : 0);
                }
                x += gap;
            }
            gb.Controls.AddRange(new Control[] { radioSoft, radioRs, radioSio, lblBaud, comboBaud, lblDelay, numDelay, lblEol, comboEol, lblFlow, comboFlow });

            var lblDir = new Label { Text = "Folder:", Left = 10, Top = 256, Width = 50 };
            txtDir = new TextBox
            {
                Left = 65, Top = 252, Width = 405, Anchor = TLR,
                Text = locs[1]
            };
            btnLocations = Btn("Folders", Icons.Folder(), 474, 249, 76, 28, TR);
            btnBrowse = Btn("Browse", Icons.Folder(), 554, 249, 76, 28, TR);
            btnOpenDir = Btn("Open", Icons.Folder(), 634, 249, 76, 28, TR);

            var gbInfo = new GroupBox { Text = "Info", Left = 10, Top = 280, Width = 700, Height = 84, Anchor = TLR };
            lblHint = new Label { Left = 8, Top = 14, Width = 684, Height = 67, Anchor = TLR };
            gbInfo.Controls.Add(lblHint);

            var gbFiles = new GroupBox { Text = "Files", Left = 10, Top = 366, Width = 565, Height = 148, Anchor = TLR };   // only the list; the row of buttons below it ends with the Terminal button
            lstFiles = new ListBox { Left = 6, Top = 16, Width = 553, Height = 126, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            chkEof = new CheckBox { Text = "Add EOF marker", Left = 202, Top = 520, Width = 115, Checked = true };
            chkAuto = new CheckBox { Text = "Auto tokenize", Left = 322, Top = 520, Width = 110, Checked = true };
            btnAbort = Btn("Abort", Icons.Cross(), 485, 518, 90, 24, TR);
            btnConvert = Btn("Convert", Icons.File(), 104, 518, 90, 24, TL);
            btnView = Btn("View", Icons.File(), 10, 518, 90, 24, TL);
            btnView.Enabled = false;
            gbFiles.Controls.Add(lstFiles);
            btnSend = Btn("Send selected", Icons.Plane(), 585, 366, 125, 32, TR);
            btnSendOther = Btn("Send file...", Icons.File(), 585, 402, 125, 32, TR);
            btnRefreshFiles = Btn("Refresh list", Icons.Refresh(), 585, 438, 125, 32, TR);
            btnDelete = Btn("Delete file", Icons.Cross(), 585, 474, 125, 32, TR);
            btnTerminal = Btn("Terminal", Icons.Terminal(), 585, 510, 125, 32, TR);

            var gbLog = new GroupBox
            {
                Text = "Log", Left = 10, Top = 546, Width = 700, Height = 118,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            txtLog = new TextBox
            {
                Left = 6, Top = 14, Width = 688, Height = 100, Multiline = true,
                ScrollBars = ScrollBars.Vertical, ReadOnly = true, Font = new Font("Consolas", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            gbLog.Controls.Add(txtLog);

            Controls.AddRange(new Control[] { lblPort, comboPorts, btnRefresh, btnOpen, led, gbTarget, banner, gb,
                lblDir, txtDir, btnLocations, btnBrowse, btnOpenDir, gbInfo, gbFiles, btnView, chkEof, chkAuto, btnConvert, btnAbort, btnSend, btnSendOther, btnRefreshFiles, btnDelete, btnTerminal, gbLog });

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
            btnAbort.Click += delegate { Abort(); };
            btnConvert.Click += delegate { ConvertSelected(); };
            btnView.Click += delegate { ViewSelected(); };
            lstFiles.SelectedIndexChanged += delegate { SelectionChanged(); };
            btnSend.Enabled = false;
            comboFlow.SelectedIndexChanged += delegate { ApplyFlow(); };
            btnLocations.Click += delegate { EditLocations(); };
            btnDelete.Click += delegate { DeleteSelected(); };
            chkAuto.CheckedChanged += delegate { RefreshFiles(); };
            btnSend.Click += delegate
            {
                if (lstFiles.SelectedItem == null) return;
                SendFile(Path.Combine(txtDir.Text, (string)lstFiles.SelectedItem));
            };
            btnSendOther.Click += delegate
            {
                using (var d = new OpenFileDialog())
                {
                    d.Filter = rs232 ? "BASIC text (*.bas;*.txt)|*.bas;*.txt|All files|*.*" : chkAuto.Checked ? "PC-1600 files (*.sl16;*.bas;*.txt)|*.sl16;*.bas;*.txt|All files|*.*" : "PC-1600 images (*.sl16)|*.sl16|All files|*.*";
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
            tips.SetToolTip(numDelay, "Line delay: pause after each line sent from a text file, in ms (the Sharp needs time to store a line).\r\n" +
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
            tips.SetToolTip(comboEol, "Line end sent after every line of a text file: CR, CR+LF (TeraTerm style) or LF.");
            tips.SetToolTip(btnView, "Show the contents of the selected file (BASIC text; program images are shown as BASIC).");
            tips.SetToolTip(chkEof, "Add the end-of-file marker. Text modes: after the file (and the dummy line) send the end-of-file character 1Ah (Ctrl-Z) so that the Sharp finishes the reception (1Ah is the end-of-file code of an ASCII file). Switch it off if the Sharp reports an error.");
            tips.SetToolTip(btnAbort, "Abort the transmission at once: stops sending the file and discards data waiting to be sent or received.");
            tips.SetToolTip(btnConvert, "Convert the selected file: program image (.sl16 / .img) to BASIC text (.bas), or BASIC text to a program image (PC-1600: .sl16, PC-1500: .img).");
            tips.SetToolTip(chkAuto, "Software Serial. ON: the converter is active. A program received from the Sharp is converted to BASIC text (.bas) and BASIC files are tokenized automatically when sent; temporary .sl16 images are not kept. OFF: only .sl16 images are used, no automatic conversion (use the Convert button).");
            tips.SetToolTip(btnLocations, "Set the default folders of the programs for PC-1500, PC-1600 and the other models.");
            tips.SetToolTip(btnDelete, "Delete the selected file from the folder (after a confirmation).");
            tips.SetToolTip(comboFlow, "Flow control for the text modes: None, XON/XOFF (default), RTS/CTS or DSR/DTR (the PC waits while the Sharp does not hold DSR). Not used by Software Serial.");
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
            radioRs.Text = "RS-232 serial";
            if (other) radioSio.Checked = true;
            else if (radioSio.Checked) radioSoft.Checked = true;
            banner.ModelName = other ? "Sharp with 15-pin connector" : p1500 ? "Sharp PC-1500" : "Sharp PC-1600";
            banner.Invalidate();
            numDelay.Value = p1500 ? 200 : 100;   // suggested starting values, adjust by testing
            SetDefaultDir(locs[other ? 2 : p1500 ? 0 : 1]);
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

        static string DefaultLocation(int i)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), i == 0 ? "PC1500" : i == 1 ? "PC1600" : "PC_15-pin");
        }

        static string LocationsFile()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerComet", "locations.txt");
        }

        static string[] LoadLocations()
        {
            var l = new[] { DefaultLocation(0), DefaultLocation(1), DefaultLocation(2) };
            try
            {
                if (File.Exists(LocationsFile()))
                {
                    string[] lines = File.ReadAllLines(LocationsFile());
                    for (int i = 0; i < 3 && i < lines.Length; i++) if (lines[i].Trim().Length > 0) l[i] = lines[i].Trim();
                }
            }
            catch { }
            return l;
        }

        void SaveLocations()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocationsFile()));
                File.WriteAllLines(LocationsFile(), locs);
            }
            catch (Exception ex) { Log("Cannot save the locations: " + ex.Message); }
        }

        // second window: the folders of the programs for each target computer
        void EditLocations()
        {
            using (var d = new LocationsForm(locs, new[] { DefaultLocation(0), DefaultLocation(1), DefaultLocation(2) }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                locs = d.Result;
                SaveLocations();
                SetDefaultDir(locs[radioOther.Checked ? 2 : radio1500.Checked ? 0 : 1]);
            }
        }

        void SetDefaultDir(string dir)
        {
            txtDir.Text = dir;
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
            lastTxAny = Environment.TickCount;
            try { if (port != null && port.IsOpen) port.Write(b, 0, b.Length); }
            catch (Exception ex) { Log("Send error: " + ex.Message); }
        }

        string Ext { get { return rs232 ? ".bas" : ".sl16"; } }
        Handshake FlowMode()
        {
            if (!rs232) return Handshake.None;
            switch (comboFlow.SelectedIndex)
            {
                case 0: return Handshake.None;
                case 2: return Handshake.RequestToSend;
                case 3: return Handshake.None;           // DSR/DTR: handled in SendFile (the port library has no DSR/DTR handshake)
                default: return Handshake.XOnXOff;
            }
        }

        void ApplyFlow()
        {
            try { if (port != null && port.IsOpen) { port.Handshake = FlowMode(); if (rs232 && comboFlow.SelectedIndex == 3) port.DtrEnable = true; } } catch (Exception ex) { Log("Error: " + ex.Message); }
        }

        string savedBaud = "9600";   // baud chosen by the user for the text modes
        int Baud { get { return rs232 ? int.Parse((string)comboBaud.SelectedItem) : (radio1500.Checked ? 9600 : 19200); } }

        void UpdateMode()
        {
            rs232 = radioRs.Checked || radioSio.Checked;
            comboBaud.Enabled = numDelay.Enabled = btnTerminal.Enabled = comboEol.Enabled = comboFlow.Enabled = chkEof.Enabled = rs232;
            chkAuto.Enabled = !rs232;
            ApplyFlow();
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
                {
                    var found = new List<string>();
                    foreach (string pat in rs232 ? new[] { "*.bas", "*.txt" } : chkAuto.Checked ? new[] { "*.sl16", "*.bas", "*.txt" } : new[] { "*.sl16" })
                        foreach (string f in Directory.GetFiles(txtDir.Text, pat)) found.Add(Path.GetFileName(f));
                    found.Sort(StringComparer.OrdinalIgnoreCase);
                    lstFiles.Items.AddRange(found.ToArray());
                }
            }
            catch (Exception ex) { Log("List error: " + ex.Message); }
            SelectionChanged();
        }

        void TogglePort()
        {
            if (port != null) { ClosePort(); return; }
            string name = comboPorts.SelectedItem as string;
            if (name == null) { Log("No port selected."); return; }
            try
            {
                port = new SerialPort(name, Baud, Parity.None, 8, StopBits.One);
                port.Handshake = FlowMode();
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
                lastRxAny = Environment.TickCount;
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
                int eofAt = rs232 ? rx.IndexOf(0x1A) : -1;     // 1Ah = end-of-file code of an ASCII file: finish at once
                if (eofAt >= 0)
                {
                    data = rx.GetRange(0, eofAt).ToArray();
                    rx.Clear();
                    receiving = false;
                }
                else if (rx.Count > 0 && quiet > (rs232 ? 3000 : 800))
                {
                    data = rx.ToArray();
                    rx.Clear();
                    receiving = false;
                }
            }
            receiving |= unchecked(Environment.TickCount - lastRxAny) < 300;
            bool tx = sending || unchecked(Environment.TickCount - lastTxAny) < 300;
            blinkPhase = !blinkPhase;                      // timer tick = 100 ms -> 5 flashes per second
            bool changed = banner.Receiving != receiving || banner.Sending != tx;
            banner.Receiving = receiving;
            banner.Sending = tx;
            if (receiving || tx) { banner.Blink = blinkPhase; banner.Invalidate(); }
            else if (changed || banner.Blink) { banner.Blink = false; banner.Invalidate(); }
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
                    string path = null;
                    string note = "";
                    if (chkAuto.Checked && !radio1500.Checked)
                    {
                        // auto tokenize ON: the image only exists in memory, the file is BASIC text
                        string problem;
                        string bas = Pc1600Basic.Detokenize(data, out problem);
                        if (problem == null)
                        {
                            path = UniquePath(name, ".bas");
                            File.WriteAllBytes(path, Encoding.GetEncoding(28591).GetBytes(bas));
                            note = " (converted from the PC-1600 intermediate code)";
                        }
                        else Log("Conversion problem: " + problem + "; the image is kept as .sl16.");
                    }
                    if (path == null)
                    {
                        path = UniquePath(name, ".sl16");
                        File.WriteAllBytes(path, data);
                    }
                    Log("RECEIVED " + data.Length + " B, " + Sl16.CountLines(data) + " lines, checksum OK -> " + Path.GetFileName(path) + note);
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

        // BASIC text <-> program image, for the selected target computer
        void ConvertSelected()
        {
            string path = null;
            if (lstFiles.SelectedItem != null) path = Path.Combine(txtDir.Text, (string)lstFiles.SelectedItem);
            else
                using (var d = new OpenFileDialog())
                {
                    d.InitialDirectory = Directory.Exists(txtDir.Text) ? txtDir.Text : null;
                    d.Filter = "Program image or BASIC text (*.sl16;*.img;*.bas;*.txt)|*.sl16;*.img;*.bas;*.txt|All files|*.*";
                    if (d.ShowDialog() != DialogResult.OK) return;
                    path = d.FileName;
                }
            try
            {
                bool p1500 = radio1500.Checked;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                byte[] data = File.ReadAllBytes(path);
                var enc = Encoding.GetEncoding(28591);
                string problem;
                string outPath;
                if (ext == ".sl16" || ext == ".img")
                {
                    string bas = Pc1600Basic.Detokenize(data, p1500 || ext == ".img", out problem);
                    outPath = UniquePathIn(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), ".bas");
                    File.WriteAllBytes(outPath, enc.GetBytes(bas));
                    Log("Converted " + Path.GetFileName(path) + " to BASIC text -> " + Path.GetFileName(outPath) + (problem != null ? " (warning: " + problem + ")" : ""));
                }
                else
                {
                    Log("Tokenizing to the " + (p1500 ? "PC-1500" : "PC-1600") + " intermediate code format...");
                    byte[] img = Pc1600Basic.Tokenize(enc.GetString(data), p1500, out problem);
                    if (img == null) { Log("Tokenizer error: " + problem); return; }
                    outPath = UniquePathIn(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), p1500 ? ".img" : ".sl16");
                    File.WriteAllBytes(outPath, img);
                    Log("Converted " + Path.GetFileName(path) + " -> " + Path.GetFileName(outPath) + " (" + img.Length + " B)");
                }
                RefreshFiles();
            }
            catch (Exception ex) { Log("Conversion error: " + ex.Message); }
        }

        // preview of the selected file: text as it is, program images as BASIC text, anything else as a hex dump
        void ViewSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string name = (string)lstFiles.SelectedItem;
            try
            {
                string path = Path.Combine(txtDir.Text, name);
                byte[] data = File.ReadAllBytes(path);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string text, problem;
                if (ext == ".sl16" || ext == ".img")
                {
                    text = Pc1600Basic.Detokenize(data, ext == ".img", out problem);
                    if (problem != null) text += "\r\n[" + problem + "]";
                    // program image: the user chooses between the original bytes and the detokenized BASIC text
                    new PreviewForm(name + " (" + data.Length + " B)", text, HexDump(data)).Show(this);
                    return;
                }
                else
                {
                    int ctl = 0;
                    foreach (byte b in data) if (b < 9 || (b > 13 && b < 32 && b != 26) ) ctl++;
                    if (ctl > data.Length / 20) text = HexDump(data);
                    else text = Encoding.GetEncoding(28591).GetString(data).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n").Replace("\u001A", "");
                }
                if (text.Length > 300000) text = text.Substring(0, 300000) + "\r\n[... cut]";
                new PreviewForm(name + " (" + data.Length + " B)", text).Show(this);
            }
            catch (Exception ex) { Log("View error: " + ex.Message); }
        }

        internal static string HexDump(byte[] d)
        {
            var sb = new StringBuilder();
            int n = Math.Min(d.Length, 16384);
            for (int i = 0; i < n; i += 16)
            {
                sb.Append(i.ToString("X4")).Append("  ");
                for (int j = 0; j < 16; j++) sb.Append(i + j < n ? d[i + j].ToString("X2") + " " : "   ");
                sb.Append(" ");
                for (int j = 0; j < 16 && i + j < n; j++) sb.Append(d[i + j] >= 32 && d[i + j] < 127 ? (char)d[i + j] : '.');
                sb.Append("\r\n");
            }
            if (d.Length > n) sb.Append("[... cut]");
            return sb.ToString();
        }

        // View and Send selected are available only when a file is selected (Send also not while a transfer runs)
        void SelectionChanged()
        {
            bool sel = lstFiles.SelectedItem != null;
            if (btnView != null) btnView.Enabled = sel;
            if (btnSend != null) btnSend.Enabled = sel && !sending;
        }

        void DeleteSelected()
        {
            if (lstFiles.SelectedItem == null) { Log("Select a file in the list."); return; }
            string name = (string)lstFiles.SelectedItem;
            using (var d = new DeleteConfirmForm(name))
                if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.Delete(Path.Combine(txtDir.Text, name));
                Log("Deleted " + name);
            }
            catch (Exception ex) { Log("Delete error: " + ex.Message); }
            RefreshFiles();
        }

        static string UniquePathIn(string dir, string name, string ext)
        {
            string path = Path.Combine(dir, name + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + "_" + i + ext);
            return path;
        }

        // forced stop of a transfer
        void Abort()
        {
            abortRequested = true;
            try { if (port != null && port.IsOpen) { port.DiscardOutBuffer(); port.DiscardInBuffer(); } } catch { }
            lock (rxLock) { rx.Clear(); }
            Log("ABORT: transmission stopped, buffers cleared.");
        }

        int progStart, progEnd;
        string progText;

        // keeps one log line up to date while a file is being sent: "SENDING name (n B) (37%)"
        void SetProgress(int pct)
        {
            if (txtLog.TextLength != progEnd) return;      // other messages were logged meanwhile: leave them alone
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + progText + " (" + pct + "%)\r\n";
            txtLog.Text = txtLog.Text.Substring(0, progStart) + line;
            progEnd = txtLog.TextLength;
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
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
            bool eof = chkEof.Checked;
            bool dsrFlow = rs232 && comboFlow.SelectedIndex == 3;
            int eolIdx = comboEol.SelectedIndex;
            byte[] eol = eolIdx == 1 ? new byte[] { 13, 10 } : eolIdx == 2 ? new byte[] { 10 } : new byte[] { 13 };
            if (!text)
            {
                string err;
                if (!path.EndsWith(".sl16", StringComparison.OrdinalIgnoreCase))
                {
                    if (!chkAuto.Checked) { Log("Auto tokenize is off: only .sl16 files can be sent (use Convert or switch Auto tokenize on)."); return; }
                    // BASIC text: convert to the intermediate code of the Sharp first
                    if (radio1500.Checked) { Log("Software Serial (Saver/Loader) does not exist for the PC-1500 yet. Use RS-232 or the Convert button."); return; }
                    Log("Tokenizing to the PC-1600 intermediate code format...");
                    string problem;
                    byte[] tok = Pc1600Basic.Tokenize(Encoding.GetEncoding(28591).GetString(data), out problem);
                    if (tok == null) { Log("Tokenizer error: " + problem); return; }
                    data = tok;
                    Log("Tokenized: " + Sl16.CountLines(data) + " lines, " + data.Length + " B (program image + checksum).");
                }
                if (!Sl16.Check(data, out err)) { Log("File rejected: " + err); return; }
            }
            abortRequested = false;
            sending = true;
            btnSend.Enabled = btnSendOther.Enabled = false;
            progText = "SENDING " + Path.GetFileName(path) + " (" + data.Length + " B)";
            progStart = txtLog.TextLength;
            Log(progText + " (0%)");
            progEnd = txtLog.TextLength;
            int total = data.Length, lastPct = -1;
            Action<int> report = pct =>
            {
                if (pct == lastPct) return;
                lastPct = pct;
                try { BeginInvoke(new Action(() => SetProgress(pct))); } catch { }
            };
            try
            {
                var p = port;
                await Task.Run(() =>
                {
                    p.DiscardInBuffer();
                    if (!text)
                    {
                        p.Write(data, 0, data.Length);
                        while (p.BytesToWrite > 0 && !abortRequested)
                        {
                            report(100 * (total - p.BytesToWrite) / total);
                            System.Threading.Thread.Sleep(50);
                        }
                    }
                    else
                    {
                        // text: line by line, every line ends with the selected line end
                        var line = new List<byte>();
                        for (int i = 0; i <= data.Length && !abortRequested; i++)
                        {
                            if (i == data.Length || data[i] == 0x0D || data[i] == 0x0A)
                            {
                                if (line.Count > 0)
                                {
                                    line.AddRange(eol);
                                    if (dsrFlow)
                                    {
                                        // DSR/DTR: wait (up to 10 s) while the Sharp does not hold DSR
                                        for (int w = 0; w < 10000 && !p.DsrHolding && !abortRequested; w += 10) System.Threading.Thread.Sleep(10);
                                    }
                                    p.Write(line.ToArray(), 0, line.Count);
                                    line.Clear();
                                    while (p.BytesToWrite > 0 && !abortRequested) System.Threading.Thread.Sleep(5);
                                    report(100 * i / total);
                                    for (int w = 0; w < delay && !abortRequested; w += 10) System.Threading.Thread.Sleep(10);
                                }
                            }
                            else line.Add(data[i]);
                        }
                    }
                    if (abortRequested) return;
                    if (text && eof) p.Write(new byte[] { 0x1A }, 0, 1);
                    while (p.BytesToWrite > 0) System.Threading.Thread.Sleep(10);
                    report(100);
                });
                if (abortRequested) Log("Transmission aborted."); else Log(text ? "Sent." : "Sent. The PC-1600 finishes after 0.5 s of silence; check X=1 (PRINT X) and LIST.");
            }
            catch (Exception ex) { Log("Send error: " + ex.Message); }
            finally
            {
                lock (rxLock) { rx.Clear(); }
                sending = false;
                btnSendOther.Enabled = true;
                SelectionChanged();
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
        public bool PortOpen, Sending, Receiving, Blink;   // Blink toggles ~5x per second while data flows
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

        static void Arrow(Graphics g, int x1, int x2, int y, bool right, bool active, bool blink)
        {
            Color c = active && blink ? Color.FromArgb(255, 150, 30) : Color.FromArgb(70, 120, 175);   // orange flash while data flows
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
            Arrow(g, ax1, ax2, top + 44, true, Sending, Blink);
            Arrow(g, ax1, ax2, top + 74, false, Receiving, Blink);
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
            var tt = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
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
            ClientSize = new Size(380, 372);

            Controls.Add(new PictureBox { Image = Icons.Comet(72), SizeMode = PictureBoxSizeMode.AutoSize, Left = (380 - 72) / 2, Top = 16 });
            AddLine("SerComet", 100, new Font("Segoe UI", 16F, FontStyle.Bold), Color.FromArgb(30, 70, 120));
            AddLine("File transfer tool for PC and Sharp pocket computers", 138, new Font("Segoe UI", 9F), Color.Black);
            AddLine("Author: Alex F4VTS", 170, new Font("Segoe UI", 10F), Color.Black);
            AddLine("Copyright \u00A9 2026", 194, new Font("Segoe UI", 10F), Color.Black);
            AddLine("License: GNU General Public License v3.0", 218, new Font("Segoe UI", 10F), Color.Black);
            AddLine("BASIC token tables: \"Das Systemhandbuch fuer den PC-1600\"", 266, new Font("Segoe UI", 8.5F), Color.Black);
            AddLine("by David von Oheimb. Thank you!", 286, new Font("Segoe UI", 8.5F), Color.Black);
            AddLine("PC-1500 token table: \"Tips PC-1500\" document", 304, new Font("Segoe UI", 8.5F), Color.Black);
            var web = new LinkLabel { Text = "Web: github.com/Ante1000", Left = 0, Top = 242, Width = 380, Height = 20, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10F) };
            web.Links.Add(5, 19, "https://github.com/Ante1000");
            web.LinkClicked += delegate (object s, LinkLabelLinkClickedEventArgs e)
            {
                try { System.Diagnostics.Process.Start((string)e.Link.LinkData); } catch { }
            };
            Controls.Add(web);
            var tt = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
            tt.SetToolTip(web, "Open the project page in the browser.");
            var ok = new Button { Text = "OK", Left = (380 - 80) / 2, Top = 336, Width = 80, DialogResult = DialogResult.OK };
            Controls.Add(ok);
            AcceptButton = ok;
            tt.SetToolTip(ok, "Close this window.");
        }

        void AddLine(string text, int top, Font font, Color color)
        {
            Controls.Add(new Label { Text = text, Left = 0, Top = top, Width = 380, Height = 24, TextAlign = ContentAlignment.MiddleCenter, Font = font, ForeColor = color });
        }
    }

    // PC-1600 BASIC program <-> text. Written from scratch using the token tables of
    // "Das Systemhandbuch fuer den PC-1600" by David von Oheimb (appendix 5, Tokentabelle) and checked against
    // programs saved by a real PC-1600. Program format: [line no. hi, lo][length][tokens, text, 0D]..., end FFh.
    // Tokens are two bytes (E3/E6/E7/E8/F0/F1/F2 + code); a line number constant is stored as 1Fh hi lo 00h.
    public static class Pc1600Basic
    {
        static readonly object[] TokenList = new object[] {
            "PAPER", 0xE381, "CSIZE", 0xE680, "GRAPH", 0xE681, "GLCURSOR", 0xE682, "SORGN", 0xE684, "ROTATE", 0xE685,
            "TEXT", 0xE686, "RMT", 0xE7A9, "DEV$", 0xE857, "COM$", 0xE858, "INSTAT", 0xE859, "RINKEY$", 0xE85A,
            "OUTSTAT", 0xE880, "SETCOM", 0xE882, "TERMINAL", 0xE883, "DTE", 0xE884, "TRANSMIT", 0xE885,
            "SETDEV", 0xE886, "ERN", 0xF052, "ERL", 0xF053, "SPACE$", 0xF061, "CURSOR", 0xF084, "USING", 0xF085,
            "WDTH", 0xF087, "CLS", 0xF088, "CLOAD", 0xF089, "MERGE", 0xF08F, "LIST", 0xF090, "INPUT", 0xF091,
            "GCURSOR", 0xF093, "CSAVE", 0xF095, "PRINT", 0xF097, "FILES", 0xF098, "LINE", 0xF099, "PRESET", 0xF09A,
            "PSET", 0xF09B, "GPRINT", 0xF09F, "LFILES", 0xF0A0, "PITCH", 0xF0A4, "LCURSOR", 0xF0A5, "FEED", 0xF0B0,
            "CONSOLE", 0xF0B1, "CHAIN", 0xF0B2, "BREAK", 0xF0B3, "ZONE", 0xF0B4, "COLOR", 0xF0B5, "LF", 0xF0B6,
            "LLINE", 0xF0B7, "LLIST", 0xF0B8, "LPRINT", 0xF0B9, "RLINE", 0xF0BA, "TAB", 0xF0BB, "TEST", 0xF0BC,
            "AND", 0xF150, "OR", 0xF151, "MEM", 0xF158, "TIME", 0xF15B, "INKEY$", 0xF15C, "PI", 0xF15D,
            "ASC", 0xF160, "STR$", 0xF161, "VAL", 0xF162, "CHR$", 0xF163, "LEN", 0xF164, "DEG", 0xF165,
            "DMS", 0xF166, "STATUS", 0xF167, "POINT", 0xF168, "SQR", 0xF16B, "NOT", 0xF16D, "XPEEK#", 0xF16E,
            "XPEEK", 0xF16F, "ABS", 0xF170, "INT", 0xF171, "RIGHT$", 0xF172, "ASN", 0xF173, "ACS", 0xF174,
            "ATN", 0xF175, "LN", 0xF176, "LOG", 0xF177, "EXP", 0xF178, "SGN", 0xF179, "LEFT$", 0xF17A,
            "MID$", 0xF17B, "RND", 0xF17C, "SIN", 0xF17D, "COS", 0xF17E, "TAN", 0xF17F, "AREAD", 0xF180,
            "ARUN", 0xF181, "BEEP", 0xF182, "CONT", 0xF183, "GRAD", 0xF186, "CLEAR", 0xF187, "XCALL", 0xF18A,
            "DIM", 0xF18B, "DEGREE", 0xF18C, "DATA", 0xF18D, "END", 0xF18E, "GOTO", 0xF192, "GOSUB", 0xF194,
            "IF", 0xF196, "LET", 0xF198, "RETURN", 0xF199, "NEXT", 0xF19A, "NEW", 0xF19B, "ON", 0xF19C,
            "OPN", 0xF19D, "OFF", 0xF19E, "XPOKE#", 0xF1A0, "XPOKE", 0xF1A1, "PAUSE", 0xF1A2, "RUN", 0xF1A4,
            "FOR", 0xF1A5, "READ", 0xF1A6, "RESTORE", 0xF1A7, "RANDOM", 0xF1A8, "RADIAN", 0xF1AA, "REM", 0xF1AB,
            "STOP", 0xF1AC, "STEP", 0xF1AD, "THEN", 0xF1AE, "TRON", 0xF1AF, "TROFF", 0xF1B0, "TO", 0xF1B1,
            "WAIT", 0xF1B3, "ERROR", 0xF1B4, "LOCK", 0xF1B5, "UNLOCK", 0xF1B6, "EOF", 0xF271, "LOC", 0xF272,
            "LOF", 0xF273, "DSKF", 0xF274, "MOD", 0xF250, "XOR", 0xF251, "RXD$", 0xF256, "DATE$", 0xF257,
            "TIME$", 0xF258, "AIN", 0xF25A, "ALARM$", 0xF25C, "WAKE$", 0xF261, "ACNV$", 0xF262, "HEX$", 0xF265,
            "INP", 0xF266, "INSTR", 0xF267, "PEEK", 0xF26D, "PEEK#", 0xF26E, "ADIN", 0xF280, "CALL", 0xF282,
            "ELSE", 0xF283, "KBUFF$", 0xF284, "KEY", 0xF285, "KEYSTAT", 0xF286, "KILL", 0xF287, "MAXFILES", 0xF288,
            "OUT", 0xF28A, "POWER", 0xF28B, "POKE", 0xF28C, "RESUME", 0xF28D, "RETI", 0xF28E, "BLOAD", 0xF290,
            "BSAVE", 0xF291, "CLOSE", 0xF292, "COPY", 0xF293, "INIT", 0xF294, "LOAD", 0xF295, "OPEN", 0xF296,
            "NAME", 0xF297, "SET", 0xF298, "SAVE", 0xF299, "PHONE", 0xF2A0, "SNDBRK", 0xF2A1, "SNDSTAT", 0xF2A2,
            "COM", 0xF2A3, "RCVSTAT", 0xF2A4, "PCONSOLE", 0xF2B1, "MODE", 0xF2B3, "PZONE", 0xF2B4, "RENUM", 0xF2B5,
            "AUTO", 0xF2B6, "ERASE", 0xF2B7, "PASS", 0xF2B8, "DELETE", 0xF2B9, "TITLE", 0xF2BA, "AOFF", 0xF2BC,
            "AS", 0xF2BD, "OUTPUT", 0xF2BE, "APPEND", 0xF2BF,
        };

        // PC-1500 (and CE-150 / CE-158): the PC-1500 token table, from the table in "Tips PC-1500" ("SHARP PC 1500(A) und CE 150
        // TOKEN-Tabelle"), completed with the interface commands from the PC-1600 handbook. The PC-1500 uses the same codes for
        // most words; PEEK/POKE/CALL are F16F/F1A1/F18A there (X-versions in the PC-1600).
        static readonly object[] TokenList1500 = new object[] {
            "CSIZE", 0xE680, "GRAPH", 0xE681, "GLCURSOR", 0xE682, "LCURSOR", 0xE683, "SORGN", 0xE684,
            "ROTATE", 0xE685, "TEXT", 0xE686, "RMT", 0xE7A9, "DEV$", 0xE857, "INSTAT", 0xE859, "RINKEY$", 0xE85A,
            "OUTSTAT", 0xE880, "SETCOM", 0xE882, "TERMINAL", 0xE883, "DTE", 0xE884, "TRANSMIT", 0xE885,
            "SETDEV", 0xE886, "ERN", 0xF052, "ERL", 0xF053, "SPACE$", 0xF061, "CURSOR", 0xF084, "USING", 0xF085,
            "CLS", 0xF088, "CLOAD", 0xF089, "MERGE", 0xF08F, "LIST", 0xF090, "INPUT", 0xF091, "GCURSOR", 0xF093,
            "CSAVE", 0xF095, "PRINT", 0xF097, "GPRINT", 0xF09F, "FEED", 0xF0B0, "CONSOLE", 0xF0B1, "CHAIN", 0xF0B2,
            "BREAK", 0xF0B3, "ZONE", 0xF0B4, "COLOR", 0xF0B5, "LF", 0xF0B6, "LINE", 0xF0B7, "LLIST", 0xF0B8,
            "LPRINT", 0xF0B9, "RLINE", 0xF0BA, "TAB", 0xF0BB, "TEST", 0xF0BC, "AND", 0xF150, "OR", 0xF151,
            "MEM", 0xF158, "TIME", 0xF15B, "INKEY$", 0xF15C, "PI", 0xF15D, "ASC", 0xF160, "STR$", 0xF161,
            "VAL", 0xF162, "CHR$", 0xF163, "LEN", 0xF164, "DEG", 0xF165, "DMS", 0xF166, "STATUS", 0xF167,
            "POINT", 0xF168, "SQR", 0xF16B, "NOT", 0xF16D, "PEEK#", 0xF16E, "PEEK", 0xF16F, "ABS", 0xF170,
            "INT", 0xF171, "RIGHT$", 0xF172, "ASN", 0xF173, "ACS", 0xF174, "ATN", 0xF175, "LN", 0xF176,
            "LOG", 0xF177, "EXP", 0xF178, "SGN", 0xF179, "LEFT$", 0xF17A, "MID$", 0xF17B, "RND", 0xF17C,
            "SIN", 0xF17D, "COS", 0xF17E, "TAN", 0xF17F, "AREAD", 0xF180, "ARUN", 0xF181, "BEEP", 0xF182,
            "CONT", 0xF183, "GRAD", 0xF186, "CLEAR", 0xF187, "CALL", 0xF18A, "DIM", 0xF18B, "DEGREE", 0xF18C,
            "DATA", 0xF18D, "END", 0xF18E, "GOTO", 0xF192, "GOSUB", 0xF194, "IF", 0xF196, "LET", 0xF198,
            "RETURN", 0xF199, "NEXT", 0xF19A, "NEW", 0xF19B, "ON", 0xF19C, "OPN", 0xF19D, "OFF", 0xF19E,
            "POKE#", 0xF1A0, "POKE", 0xF1A1, "PAUSE", 0xF1A2, "RUN", 0xF1A4, "FOR", 0xF1A5, "READ", 0xF1A6,
            "RESTORE", 0xF1A7, "RANDOM", 0xF1A8, "RADIAN", 0xF1AA, "REM", 0xF1AB, "STOP", 0xF1AC, "STEP", 0xF1AD,
            "THEN", 0xF1AE, "TRON", 0xF1AF, "TROFF", 0xF1B0, "TO", 0xF1B1, "WAIT", 0xF1B3, "ERROR", 0xF1B4,
            "LOCK", 0xF1B5, "UNLOCK", 0xF1B6,
        };

        sealed class Tab
        {
            public readonly Dictionary<int, string> ByCode = new Dictionary<int, string>();
            public readonly List<KeyValuePair<string, int>> ByLength = new List<KeyValuePair<string, int>>();
            public Tab(object[] list)
            {
                for (int i = 0; i < list.Length; i += 2)
                {
                    string n = (string)list[i];
                    int c = (int)list[i + 1];
                    ByCode[c] = n;
                    ByLength.Add(new KeyValuePair<string, int>(n, c));
                }
                ByLength.Sort((x, y) => y.Key.Length != x.Key.Length ? y.Key.Length.CompareTo(x.Key.Length) : string.CompareOrdinal(x.Key, y.Key));
            }
        }

        static readonly Tab T1600 = new Tab(TokenList);
        static readonly Tab T1500 = new Tab(TokenList1500);
        // keywords after which a line number constant (binary form 1F hi lo 00 on the PC-1600) can follow
        static readonly HashSet<string> LineKeywords = new HashSet<string> { "GOTO", "GOSUB", "THEN", "ELSE", "RESTORE", "RUN", "RESUME", "LIST", "LLIST", "DELETE", "RENUM", "AUTO" };

        // ---------------------------------------------------------------- program image -> text
        // data: the program image; a trailing checksum byte (.sl16) is checked and removed, a trailing FFh is accepted
        public static string Detokenize(byte[] data, out string problem) { return Detokenize(data, false, out problem); }

        // pc1500: image = lines + FFh end marker (no checksum); PC-1600: .sl16 = lines + checksum (a trailing FFh is accepted too)
        public static string Detokenize(byte[] data, bool pc1500, out string problem)
        {
            problem = null;
            Tab t = pc1500 ? T1500 : T1600;
            int n = data.Length;
            string err;
            if (!pc1500 && Sl16.Check(data, out err)) n--;           // .sl16 = program + checksum
            else if (n > 0 && data[n - 1] == 0xFF) n--;                // image with the end marker
            var sb = new StringBuilder();
            int p = 0;
            while (p < n)
            {
                if (p + 3 > n) { problem = "truncated line at byte " + p; break; }
                int no = data[p] * 256 + data[p + 1];
                int len = data[p + 2];
                if (len < 1 || p + 3 + len > n) { problem = "bad line length at byte " + p; break; }
                sb.Append(no).Append(' ').Append(DetokenizeLine(t, data, p + 3, p + 3 + len)).Append("\r\n");
                p += 3 + len;
            }
            return sb.ToString();
        }

        static string DetokenizeLine(Tab t, byte[] d, int p, int end)
        {
            var sb = new StringBuilder();
            bool inString = false;
            while (p < end)
            {
                byte b = d[p];
                if (b == 0x0D) break;
                if (inString)
                {
                    sb.Append((char)b); p++;
                    if (b == 0x22) inString = false;
                    continue;
                }
                if (b == 0x22) { inString = true; sb.Append('"'); p++; continue; }
                if (b == 0x1F && p + 3 < end + 1)                       // line number constant
                {
                    sb.Append(d[p + 1] * 256 + d[p + 2]);
                    p += 3;
                    if (p < end && d[p] == 0x00) p++;
                    continue;
                }
                if (b >= 0xE0 && p + 1 < end)
                {
                    int code = b * 256 + d[p + 1];
                    string name;
                    if (!t.ByCode.TryGetValue(code, out name)) name = "<" + code.ToString("X4") + ">";
                    p += 2;
                    // a space before the keyword when the previous character would run into it
                    if (sb.Length > 0 && (char.IsLetterOrDigit(sb[sb.Length - 1]) || sb[sb.Length - 1] == ')' || sb[sb.Length - 1] == '$' || sb[sb.Length - 1] == '"' || sb[sb.Length - 1] == '#')) sb.Append(' ');
                    sb.Append(name);
                    if (name == "REM" || name == "DATA")
                    {
                        sb.Append(' ');
                        while (p < end && d[p] != 0x0D) { sb.Append((char)d[p]); p++; }
                        break;
                    }
                    // a space after the keyword when the next thing is a name, number, string or another keyword
                    if (p < end)
                    {
                        byte nb = d[p];
                        if (nb >= 0xE0 || nb == 0x1F || nb == 0x22 || char.IsLetterOrDigit((char)nb) || nb == 0x26) sb.Append(' ');
                    }
                    continue;
                }
                sb.Append((char)b); p++;
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- text -> program image (.sl16)
        public static byte[] Tokenize(string text, out string problem) { return Tokenize(text, false, out problem); }

        // PC-1600: program + checksum (.sl16), line numbers after GOTO etc. in binary form. PC-1500: program + FFh (numbers stay ASCII)
        public static byte[] Tokenize(string text, bool pc1500, out string problem)
        {
            problem = null;
            Tab t = pc1500 ? T1500 : T1600;
            var lines = new SortedDictionary<int, byte[]>();
            int ln = 0;
            foreach (string raw in text.Split('\n'))
            {
                ln++;
                string s = raw.TrimEnd('\r', ' ', '\t', (char)0x1A);
                if (s.Trim().Length == 0) continue;
                int i = 0;
                while (i < s.Length && s[i] == ' ') i++;
                int start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i == start) { problem = "text line " + ln + ": no line number"; return null; }
                int no = int.Parse(s.Substring(start, i - start));
                if (no < 1 || no > 65279) { problem = "text line " + ln + ": line number out of range"; return null; }
                var body = new List<byte>();
                string err = TokenizeBody(t, !pc1500, s.Substring(i), body);
                if (err != null) { problem = "line " + no + ": " + err; return null; }
                body.Add(0x0D);
                if (body.Count > 255) { problem = "line " + no + ": too long (" + body.Count + " bytes)"; return null; }
                var line = new List<byte> { (byte)(no >> 8), (byte)(no & 255), (byte)body.Count };
                line.AddRange(body);
                lines[no] = line.ToArray();                              // a repeated number replaces the earlier line
            }
            var all = new List<byte>();
            foreach (var kv in lines) all.AddRange(kv.Value);
            if (pc1500) { all.Add(0xFF); return all.ToArray(); }
            int sum = 0;
            foreach (byte b in all) sum += b;
            all.Add((byte)(sum & 255));
            return all.ToArray();
        }

        static string TokenizeBody(Tab t, bool binaryNumbers, string s, List<byte> o)
        {
            int i = 0;
            bool lineMode = false;                  // a line number constant is expected here (after GOTO, THEN, ...)
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t') { i++; continue; }
                if (c == '"')                                            // string: copied as it is
                {
                    o.Add(0x22); i++;
                    while (i < s.Length) { char q = s[i++]; o.Add((byte)q); if (q == '"') break; }
                    lineMode = false;
                    continue;
                }
                if (binaryNumbers && lineMode && char.IsDigit(c))
                {
                    int j = i;
                    while (j < s.Length && char.IsDigit(s[j])) j++;
                    int v;
                    if (!int.TryParse(s.Substring(i, j - i), out v) || v > 65535) return "bad line number " + s.Substring(i, j - i);
                    o.Add(0x1F); o.Add((byte)(v >> 8)); o.Add((byte)(v & 255)); o.Add(0x00);
                    i = j;
                    // another number may follow after a comma (ON ... GOTO 10,20,30)
                    int k = i;
                    while (k < s.Length && s[k] == ' ') k++;
                    if (k < s.Length && s[k] == ',') { o.Add((byte)','); i = k + 1; }
                    else lineMode = false;
                    continue;
                }
                // keyword: longest match, case-insensitive (like the PC-1600, also inside longer names)
                string kw = null; int code = 0;
                foreach (var kv in t.ByLength)
                {
                    if (i + kv.Key.Length <= s.Length && string.Compare(s, i, kv.Key, 0, kv.Key.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    { kw = kv.Key; code = kv.Value; break; }
                }
                if (kw != null)
                {
                    o.Add((byte)(code >> 8)); o.Add((byte)(code & 255));
                    i += kw.Length;
                    if (kw == "REM" || kw == "DATA")                     // the rest of the line is text
                    {
                        if (i < s.Length && s[i] == ' ') i++;
                        for (; i < s.Length; i++) o.Add((byte)s[i]);
                        return null;
                    }
                    lineMode = LineKeywords.Contains(kw);
                    continue;
                }
                lineMode = false;
                o.Add((byte)(c >= 'a' && c <= 'z' ? c - 32 : c));
                i++;
            }
            return null;
        }
    }

    // ToolTip that wraps its text at about 30 characters per line
    public class WrapToolTip : ToolTip
    {
        const int Width = 30;

        public new void SetToolTip(Control control, string caption)
        {
            base.SetToolTip(control, Wrap(caption));
        }

        public static string Wrap(string text)
        {
            if (text == null) return null;
            var result = new StringBuilder();
            foreach (string para in text.Replace("\r\n", "\n").Split('\n'))
            {
                int col = 0;
                foreach (string word in para.Split(' '))
                {
                    if (word.Length == 0) { if (col > 0) { result.Append(' '); col++; } continue; }
                    if (col > 0 && col + 1 + word.Length > Width) { result.Append("\r\n"); col = 0; }
                    else if (col > 0) { result.Append(' '); col++; }
                    result.Append(word);
                    col += word.Length;
                }
                result.Append("\r\n");
            }
            return result.ToString().TrimEnd('\r', '\n');
        }
    }

    // Window for the folders of the programs (one per target computer)
    public class LocationsForm : Form
    {
        readonly TextBox[] boxes = new TextBox[3];
        public string[] Result;

        public LocationsForm(string[] current, string[] defaults)
        {
            Text = "Locations of the programs";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 170);
            try { Icon = Icons.AppIcon(); } catch { }
            var tt = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
            string[] names = { "PC-1500", "PC-1600", "Other 15-pin" };
            for (int i = 0; i < 3; i++)
            {
                int row = i;
                Controls.Add(new Label { Text = names[i] + ":", Left = 12, Top = 19 + i * 36, Width = 85 });
                boxes[i] = new TextBox { Left = 100, Top = 15 + i * 36, Width = 365, Text = current[i] };
                var browse = new Button { Text = "Browse...", Left = 472, Top = 14 + i * 36, Width = 78, Height = 24 };
                browse.Click += delegate
                {
                    using (var f = new FolderBrowserDialog())
                    {
                        f.SelectedPath = boxes[row].Text;
                        if (f.ShowDialog() == DialogResult.OK) boxes[row].Text = f.SelectedPath;
                    }
                };
                tt.SetToolTip(boxes[i], "Folder of the programs for this computer. It is selected together with the target computer.");
                tt.SetToolTip(browse, "Choose the folder.");
                Controls.AddRange(new Control[] { boxes[i], browse });
            }
            var defs = new Button { Text = "Defaults", Left = 12, Top = 128, Width = 90, Height = 26 };
            defs.Click += delegate { for (int i = 0; i < 3; i++) boxes[i].Text = defaults[i]; };
            tt.SetToolTip(defs, "Restore the standard folders (in Documents).");
            var ok = new Button { Text = "OK", Left = 370, Top = 128, Width = 85, Height = 26, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 465, Top = 128, Width = 85, Height = 26, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { defs, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            FormClosing += delegate
            {
                if (DialogResult != DialogResult.OK) return;
                Result = new string[3];
                for (int i = 0; i < 3; i++) Result[i] = boxes[i].Text.Trim().Length > 0 ? boxes[i].Text.Trim() : defaults[i];
            };
        }
    }

    // Confirmation before a file is deleted: "Delete file" / "Cancel"
    public class DeleteConfirmForm : Form
    {
        public DeleteConfirmForm(string fileName)
        {
            Text = "Delete file";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 120);
            try { Icon = Icons.AppIcon(); } catch { }
            Controls.Add(new Label { Text = "Delete this file permanently?", Left = 14, Top = 14, Width = 330, Height = 20 });
            Controls.Add(new Label { Text = fileName, Left = 14, Top = 38, Width = 330, Height = 36, Font = new Font("Segoe UI", 9F, FontStyle.Bold) });
            var del = new Button { Text = "Delete file", Left = 150, Top = 82, Width = 95, Height = 26, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 252, Top = 82, Width = 95, Height = 26, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { del, cancel });
            AcceptButton = cancel;      // Enter = the safe choice
            CancelButton = cancel;
        }
    }

    // Preview window (read only). With an original text too (program images) two radio buttons switch the view.
    public class PreviewForm : Form
    {
        readonly TextBox box;

        public PreviewForm(string title, string text) : this(title, text, null) { }

        public PreviewForm(string title, string text, string original)
        {
            Text = title;
            ClientSize = new Size(640, 460);
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = Icons.AppIcon(); } catch { }
            box = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = new Font("Consolas", 9.5F), Text = text, BackColor = Color.White, SelectionStart = 0
            };
            Controls.Add(box);
            if (original != null)
            {
                var top = new Panel { Dock = DockStyle.Top, Height = 28 };
                var rbOrig = new RadioButton { Text = "Original content", Left = 8, Top = 5, AutoSize = true };
                var rbDetok = new RadioButton { Text = "Detokenized", Left = 140, Top = 5, AutoSize = true, Checked = true };
                rbOrig.CheckedChanged += delegate { if (rbOrig.Checked) { box.Text = original; box.SelectionStart = 0; } };
                rbDetok.CheckedChanged += delegate { if (rbDetok.Checked) { box.Text = text; box.SelectionStart = 0; } };
                top.Controls.AddRange(new Control[] { rbOrig, rbDetok });
                var tt = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
                tt.SetToolTip(rbOrig, "The bytes of the file (hex dump).");
                tt.SetToolTip(rbDetok, "The program as BASIC text.");
                Controls.Add(top);
            }
        }
    }
}
