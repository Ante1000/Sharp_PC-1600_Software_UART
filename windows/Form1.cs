using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UartSlTest
{
    // Format pliku .sl16 = dokladnie strumien z SAVE: bajty programu BASIC + 1 bajt sumy kontrolnej (suma mod 256).
    // Linia: [nr linii 2B][dlugosc 1B][tresc, na koncu 0D]. Znacznika konca (FF) w pliku nie ma.
    public static class Sl16
    {
        public static bool Check(byte[] s, out string err)
        {
            err = null;
            if (s.Length < 2) { err = "strumien za krotki (pusty program?)"; return false; }
            int n = s.Length - 1, sum = 0;
            for (int i = 0; i < n; i++) sum += s[i];
            if ((sum & 0xFF) != s[n]) { err = "zla suma kontrolna"; return false; }
            int p = 0;
            while (p < n)
            {
                if (p + 3 > n) { err = "ucieta linia"; return false; }
                int len = s[p + 2];
                if (len < 2 || p + 3 + len > n) { err = "zla dlugosc linii przy bajcie " + p; return false; }
                if (s[p + 2 + len] != 0x0D) { err = "brak CR na koncu linii przy bajcie " + p; return false; }
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

        // nazwa z pierwszej linii zaczynajacej sie od REM (F1 AB)
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

        static string Clean(string t)
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

    public partial class Form1 : Form
    {
        SerialPort port;
        readonly List<byte> rx = new List<byte>();
        readonly object rxLock = new object();
        int lastRx;
        bool sending;

        ComboBox comboPorts;
        Button btnRefresh, btnOpen, btnBrowse, btnOpenDir, btnSend, btnSendOther, btnRefreshFiles;
        TextBox txtDir, txtLog;
        ListBox lstFiles;
        Label lblStatus;
        Timer timer;

        public Form1()
        {
            InitializeComponent();
            BuildUi();
            FormClosing += delegate { ClosePort(); };
            RefreshPorts();
            RefreshFiles();
            Log("Gotowe. Wybierz port i kliknij 'Otworz port' (19200 8N1).");
        }

        void BuildUi()
        {
            var lbl1 = new Label { Text = "Port:", Left = 10, Top = 14, Width = 40 };
            comboPorts = new ComboBox { Left = 55, Top = 10, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            btnRefresh = new Button { Text = "Odswiez", Left = 165, Top = 9, Width = 70 };
            btnOpen = new Button { Text = "Otworz port", Left = 245, Top = 9, Width = 100 };
            lblStatus = new Label { Text = "port zamkniety", Left = 355, Top = 14, Width = 330, ForeColor = Color.DarkRed };

            var lbl2 = new Label { Text = "Katalog:", Left = 10, Top = 46, Width = 50 };
            txtDir = new TextBox
            {
                Left = 65, Top = 42, Width = 460,
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC1600")
            };
            btnBrowse = new Button { Text = "Wybierz...", Left = 535, Top = 41, Width = 70 };
            btnOpenDir = new Button { Text = "Otworz", Left = 610, Top = 41, Width = 60 };

            var lbl3 = new Label
            {
                Text = "ODBIOR: na PC-1600 wykonaj SAVE (CALL adres,X). Plik zapisze sie sam, nazwa z pierwszego REM.   WYSYLANIE: najpierw LOAD na PC-1600 (CALL adres+176,X), potem przycisk (30 s na start).",
                Left = 10, Top = 74, Width = 680, Height = 32
            };

            lstFiles = new ListBox { Left = 10, Top = 110, Width = 520, Height = 150 };
            btnSend = new Button { Text = "Wyslij wybrany do PC-1600", Left = 540, Top = 110, Width = 150, Height = 30 };
            btnSendOther = new Button { Text = "Wyslij plik z dysku...", Left = 540, Top = 146, Width = 150, Height = 30 };
            btnRefreshFiles = new Button { Text = "Odswiez liste", Left = 540, Top = 182, Width = 150, Height = 30 };

            txtLog = new TextBox
            {
                Left = 10, Top = 270, Width = 680, Height = 240, Multiline = true,
                ScrollBars = ScrollBars.Vertical, ReadOnly = true, Font = new Font("Consolas", 9F)
            };

            Controls.AddRange(new Control[] { lbl1, comboPorts, btnRefresh, btnOpen, lblStatus, lbl2, txtDir,
                btnBrowse, btnOpenDir, lbl3, lstFiles, btnSend, btnSendOther, btnRefreshFiles, txtLog });
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            btnRefresh.Click += delegate { RefreshPorts(); };
            btnOpen.Click += delegate { TogglePort(); };
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
                catch (Exception ex) { Log("Blad: " + ex.Message); }
            };
            btnRefreshFiles.Click += delegate { RefreshFiles(); };
            btnSend.Click += delegate
            {
                if (lstFiles.SelectedItem == null) { Log("Zaznacz plik na liscie."); return; }
                SendFile(Path.Combine(txtDir.Text, (string)lstFiles.SelectedItem));
            };
            btnSendOther.Click += delegate
            {
                using (var d = new OpenFileDialog())
                {
                    d.Filter = "Pliki PC-1600 (*.sl16)|*.sl16|Wszystkie|*.*";
                    if (d.ShowDialog() == DialogResult.OK) SendFile(d.FileName);
                }
            };

            timer = new Timer { Interval = 100 };
            timer.Tick += TimerTick;
            timer.Start();
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
                    foreach (string f in Directory.GetFiles(txtDir.Text, "*.sl16"))
                        lstFiles.Items.Add(Path.GetFileName(f));
            }
            catch (Exception ex) { Log("Blad listy: " + ex.Message); }
        }

        void TogglePort()
        {
            if (port != null) { ClosePort(); return; }
            string name = comboPorts.SelectedItem as string;
            if (name == null) { Log("Brak wybranego portu."); return; }
            try
            {
                port = new SerialPort(name, 19200, Parity.None, 8, StopBits.One);
                port.Handshake = Handshake.None;
                port.ReadBufferSize = 65536;
                port.DataReceived += OnData;
                port.Open();
                btnOpen.Text = "Zamknij port";
                lblStatus.Text = name + " otwarty, 19200 8N1";
                lblStatus.ForeColor = Color.DarkGreen;
                Log("Port " + name + " otwarty.");
            }
            catch (Exception ex)
            {
                Log("Nie mozna otworzyc portu: " + ex.Message);
                port = null;
            }
        }

        void ClosePort()
        {
            if (port == null) return;
            try { port.DataReceived -= OnData; if (port.IsOpen) port.Close(); } catch { }
            port = null;
            btnOpen.Text = "Otworz port";
            lblStatus.Text = "port zamkniety";
            lblStatus.ForeColor = Color.DarkRed;
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

        // 0,8 s ciszy po odebraniu bajtow = koniec transmisji (SAVE wysyla bez przerw)
        void TimerTick(object sender, EventArgs e)
        {
            byte[] data = null;
            lock (rxLock)
            {
                if (rx.Count > 0 && unchecked(Environment.TickCount - lastRx) > 800)
                {
                    data = rx.ToArray();
                    rx.Clear();
                }
            }
            if (data != null) Received(data);
        }

        void Received(byte[] data)
        {
            try
            {
                Directory.CreateDirectory(txtDir.Text);
                string err;
                if (Sl16.Check(data, out err))
                {
                    string name = Sl16.NameFromRem(data);
                    if (name == null) name = "PROGRAM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string path = UniquePath(name, ".sl16");
                    File.WriteAllBytes(path, data);
                    Log("ODEBRANO " + data.Length + " B, " + Sl16.CountLines(data) + " linii, suma OK -> " + Path.GetFileName(path));
                    RefreshFiles();
                }
                else
                {
                    string path = UniquePath("BLAD_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), ".bad");
                    File.WriteAllBytes(path, data);
                    Log("BLAD ODBIORU (" + err + "), " + data.Length + " B zapisano jako " + Path.GetFileName(path) + ". Powtorz SAVE.");
                }
            }
            catch (Exception ex) { Log("Blad zapisu: " + ex.Message); }
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
            if (port == null || !port.IsOpen) { Log("Najpierw otworz port."); return; }
            if (sending) return;
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { Log("Blad odczytu: " + ex.Message); return; }
            string err;
            if (!Sl16.Check(data, out err)) { Log("Plik odrzucony: " + err); return; }
            sending = true;
            btnSend.Enabled = btnSendOther.Enabled = false;
            Log("WYSYLAM " + Path.GetFileName(path) + " (" + data.Length + " B)...");
            try
            {
                var p = port;
                await Task.Run(() =>
                {
                    p.DiscardInBuffer();
                    p.Write(data, 0, data.Length);
                    while (p.BytesToWrite > 0) System.Threading.Thread.Sleep(10);
                });
                Log("Wyslano. PC-1600 konczy po 0,5 s ciszy; sprawdz X=1 (PRINT X) i LIST.");
            }
            catch (Exception ex) { Log("Blad wysylania: " + ex.Message); }
            finally
            {
                lock (rxLock) { rx.Clear(); }
                sending = false;
                btnSend.Enabled = btnSendOther.Enabled = true;
            }
        }
    }
}
