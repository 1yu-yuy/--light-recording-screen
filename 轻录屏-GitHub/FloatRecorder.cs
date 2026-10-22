using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class Program {
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread] static void Main(string[] args) {
        bool created;
        using (var mutex = new Mutex(true, "Local\\QingLuPingFloatingRecorder", out created)) {
            if (!created) { MessageBox.Show("轻录屏已经在运行，请查看桌面悬浮球或系统托盘。", "轻录屏"); return; }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Recorder(args.Length > 0 && args[0] == "--self-test"));
        }
    }
}

sealed class Recorder : Form {
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    readonly Color ink = Color.FromArgb(22, 29, 46);
    readonly Color accent = Color.FromArgb(71, 216, 196);
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
    readonly StringBuilder errors = new StringBuilder();
    readonly string folder;
    readonly bool test;
    Form panel;
    Label status;
    Button recordButton;
    ComboBox soundChoice;
    int soundMode;
    AudioCapture systemAudio, micAudio;
    string rawVideo, systemPath, micPath;
    bool finalizing;
    System.Media.SoundPlayer testPlayer;
    Process encoder;
    DateTime started;
    string videoPath;
    bool stopping, closing, dragging;
    Point down, origin;
    int testStage;

    public Recorder(bool selfTest) {
        test = selfTest;
        folder = test ? Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "work", "checks")) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "录屏与截图");
        Directory.CreateDirectory(folder);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(64, 64);
        BackColor = ink;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - 90, area.Top + area.Height / 3);
        using (var shape = new GraphicsPath()) { shape.AddEllipse(1, 1, 62, 62); Region = new Region(shape); }
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示悬浮球", null, delegate { Show(); BringToFront(); });
        menu.Items.Add("打开保存目录", null, delegate { OpenFolder(); });
        menu.Items.Add("退出轻录屏", null, delegate { Close(); });
        tray.Icon = SystemIcons.Application;
        tray.Text = "轻录屏 · 点击悬浮球开始";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += delegate { Show(); BringToFront(); TogglePanel(); };
        MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = MousePosition; origin = Location; dragging = false; Capture = true; } };
        MouseMove += delegate(object s, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            var p = MousePosition;
            if (Math.Abs(p.X-down.X)+Math.Abs(p.Y-down.Y)>5) dragging=true;
            if (dragging) { HidePanel(); Location = new Point(origin.X+p.X-down.X, origin.Y+p.Y-down.Y); }
        };
        MouseUp += delegate(object s, MouseEventArgs e) {
            Capture = false;
            if (e.Button == MouseButtons.Left && !dragging) TogglePanel();
            if (e.Button == MouseButtons.Right) menu.Show(this, e.Location);
            var r = Screen.FromPoint(Location).WorkingArea;
            Location = new Point(Math.Max(r.Left, Math.Min(Left,r.Right-Width)), Math.Max(r.Top,Math.Min(Top,r.Bottom-Height)));
        };
        clock.Interval = 500;
        clock.Tick += delegate {
            UpdateStatus();
            if (test && encoder != null && !stopping && (DateTime.Now-started).TotalSeconds > 3) StopRecording();
        };
        clock.Start();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        SetWindowDisplayAffinity(Handle, 0x11);
        bool r = RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.R);
        bool s = RegisterHotKey(Handle, 2, 0x4003, (uint)Keys.S);
        if (!r || !s) Notify("部分快捷键已被其他软件占用，仍可点击悬浮球操作。");
        if (test) BeginInvoke(new Action(async delegate {
            await Screenshot(); testStage=1; StartRecording();
            if (encoder == null) { Environment.ExitCode=1; Close(); }
            else if(File.Exists(Path.Combine(folder,"test-tone.wav"))) {
                testPlayer=new System.Media.SoundPlayer(Path.Combine(folder,"test-tone.wav")); testPlayer.Play();
            }
        }));
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0312) {
            if (m.WParam.ToInt32() == 1) { if (encoder == null) StartRecording(); else StopRecording(); }
            if (m.WParam.ToInt32() == 2) { var ignored = Screenshot(); }
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g=e.Graphics;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        using (var pen=new Pen(encoder==null ? accent : Color.FromArgb(255,96,111),2)) g.DrawEllipse(pen,3,3,57,57);
        if (encoder != null) {
            using(var brush=new SolidBrush(Color.FromArgb(255,96,111))) g.FillRectangle(brush,23,22,18,18);
        } else {
            using (var pen=new Pen(accent,2.5f)) { g.DrawRectangle(pen,18,22,27,21); g.DrawEllipse(pen,26,27,10,10); g.DrawLine(pen,24,18,36,18); }
        }
    }

    Button AddButton(string text, int y, EventHandler action, bool primary) {
        var b=new Button { Text=text, Location=new Point(16,y), Size=new Size(248,36), FlatStyle=FlatStyle.Flat, BackColor=primary ? accent : Color.FromArgb(39,48,66), ForeColor=primary ? ink : Color.White, Cursor=Cursors.Hand, Font=new Font("Microsoft YaHei UI",10) };
        b.FlatAppearance.BorderSize=0; b.Click+=action; panel.Controls.Add(b); return b;
    }
    void TogglePanel() {
        if (panel!=null && panel.Visible) { HidePanel(); return; }
        if (panel == null) {
            panel=new Form { FormBorderStyle=FormBorderStyle.None, ShowInTaskbar=false, TopMost=true, BackColor=ink, Size=new Size(280,330), StartPosition=FormStartPosition.Manual };
            panel.Controls.Add(new Label {Text="轻录屏", ForeColor=Color.White, Font=new Font("Microsoft YaHei UI",14,FontStyle.Bold), Location=new Point(16,14), Size=new Size(190,30)});
            status=new Label { ForeColor=Color.FromArgb(173,186,206), Font=new Font("Microsoft YaHei UI",9), Location=new Point(16,49), Size=new Size(250,36) }; panel.Controls.Add(status);
            soundChoice=new ComboBox { Location=new Point(16,87), Size=new Size(248,30), DropDownStyle=ComboBoxStyle.DropDownList, Font=new Font("Microsoft YaHei UI",10) };
            soundChoice.Items.AddRange(new object[] { "声音：电脑声音（默认）", "声音：麦克风", "声音：电脑声音 + 麦克风", "声音：静音" });
            soundChoice.SelectedIndex=soundMode;
            soundChoice.SelectedIndexChanged+=delegate { soundMode=soundChoice.SelectedIndex; };
            panel.Controls.Add(soundChoice);
            recordButton=AddButton("开始录屏   Ctrl+Alt+R",133,delegate { if(encoder==null) StartRecording(); else StopRecording(); },true);
            AddButton("截屏保存   Ctrl+Alt+S",177,async delegate { await Screenshot(); },false);
            AddButton("打开保存目录",221,delegate { OpenFolder(); },false);
            AddButton("退出",265,delegate { Close(); },false);
            panel.Deactivate+=delegate { HidePanel(); };
        }
        var area=Screen.FromControl(this).WorkingArea;
        panel.Location=new Point(Math.Max(area.Left,Math.Min(Left-panel.Width-10,area.Right-panel.Width)),Math.Max(area.Top,Math.Min(Top,area.Bottom-panel.Height)));
        panel.Show(this); SetWindowDisplayAffinity(panel.Handle,0x11); UpdateStatus();
    }
    void HidePanel() { if(panel!=null) panel.Hide(); }
    void UpdateStatus() {
        if(status!=null) status.Text=encoder==null ? "当前屏幕 · 30 帧/秒 · 可录声音" : stopping ? "正在保存视频和声音，请稍候…" : "● 正在录制  "+(DateTime.Now-started).ToString(@"hh\:mm\:ss");
        if(soundChoice!=null) soundChoice.Enabled=encoder==null;
        if(recordButton!=null) { recordButton.Text=encoder==null ? "开始录屏   Ctrl+Alt+R" : stopping ? "正在保存…" : "停止并保存   Ctrl+Alt+R"; recordButton.Enabled=!stopping; }
        Invalidate();
    }
    void Notify(string message) { if(!test) { tray.BalloonTipTitle="轻录屏"; tray.BalloonTipText=message; tray.ShowBalloonTip(2500); } }
    void OpenFolder() { try { Process.Start("explorer.exe", "\""+folder+"\""); } catch(Exception ex) { Notify(ex.Message); } }

    async Task Screenshot() {
        bool visible=Visible;
        try {
            var bounds=Screen.FromControl(this).Bounds;
            HidePanel(); Hide(); await Task.Delay(200);
            string path=Path.Combine(folder,"截图_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".png");
            using(var bitmap=new Bitmap(bounds.Width,bounds.Height)) {
                using(var g=Graphics.FromImage(bitmap)) g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
                bitmap.Save(path,ImageFormat.Png);
            }
            if(test) File.WriteAllText(Path.Combine(folder,"screenshot-ok.txt"),path);
            Notify("截图已保存到："+path);
        } catch(Exception ex) { Notify("截屏失败："+ex.Message); if(test) Environment.ExitCode=1; }
        finally { if(visible && !IsDisposed && !closing) Show(); }
    }

    void StartRecording() {
        if(encoder!=null || closing) return;
        try {
            string ffmpeg=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ffmpeg.exe");
            if(!File.Exists(ffmpeg)) throw new FileNotFoundException("请将 ffmpeg.exe 放在轻录屏程序旁边。");
            var bounds=Screen.FromControl(this).Bounds;
            videoPath=Path.Combine(folder,"录屏_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".mp4");
            rawVideo=videoPath+".video.mp4"; systemPath=videoPath+".system.wav"; micPath=videoPath+".mic.wav";
            if(soundMode==0 || soundMode==2) systemAudio=new AudioCapture(systemPath,true);
            if(soundMode==1 || soundMode==2) micAudio=new AudioCapture(micPath,false);
            var info=new ProcessStartInfo(ffmpeg,"-hide_banner -loglevel warning -y -f gdigrab -framerate 30 -draw_mouse 1 -offset_x "+bounds.Left+" -offset_y "+bounds.Top+" -video_size "+bounds.Width+"x"+bounds.Height+" -i desktop -an -vf \"pad=ceil(iw/2)*2:ceil(ih/2)*2\" -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p \""+rawVideo+"\"") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardError=true };
            errors.Clear();
            var p=new Process {StartInfo=info,EnableRaisingEvents=true};
            p.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs a) { if(a.Data!=null) lock(errors) { if(errors.Length>6000) errors.Remove(0,3000); errors.AppendLine(a.Data); } };
            p.Exited+=delegate { if(!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { FinishRecording(p); })); };
            encoder=p; started=DateTime.Now; stopping=false;
            p.Start(); p.BeginErrorReadLine(); HidePanel(); UpdateStatus();
            tray.Text="轻录屏 · 正在录制 · Ctrl+Alt+R 停止";
        } catch(Exception ex) {
            if(systemAudio!=null) { systemAudio.Dispose(); systemAudio=null; }
            if(micAudio!=null) { micAudio.Dispose(); micAudio=null; }
            if(encoder!=null) { encoder.Dispose(); encoder=null; }
            UpdateStatus();
            if(test) { File.WriteAllText(Path.Combine(folder,"audio-start-error.txt"),ex.ToString()); Environment.ExitCode=1; }
            else MessageBox.Show("无法开始录制："+ex.Message+"\n请检查声音设备，或选择其他声音模式。","轻录屏");
        }
    }
    async void StopRecording() {
        var p=encoder;
        if(p==null || stopping) return;
        stopping=true; HidePanel(); UpdateStatus();
        try {
            await p.StandardInput.WriteLineAsync("q"); await p.StandardInput.FlushAsync();
            // The Exited event completes saving without blocking the UI.
        } catch(InvalidOperationException) { }
        catch(IOException) { }
    }
    async void FinishRecording(Process p) {
        if(encoder!=p || finalizing) return;
        finalizing=true; stopping=true; UpdateStatus();
        int code=p.ExitCode;
        bool success=false;
        try {
            await Task.Run(delegate {
                if(systemAudio!=null) systemAudio.Finish();
                if(micAudio!=null) micAudio.Finish();
                if(code!=0) throw new Exception("视频录制进程异常退出。");
                if(soundMode==3) { File.Move(rawVideo,videoPath); return; }
                string inputs="-i \""+rawVideo+"\"";
                if(systemAudio!=null) inputs+=" -i \""+systemPath+"\"";
                if(micAudio!=null) inputs+=" -i \""+micPath+"\"";
                string mapping=soundMode==2 ? " -filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95,apad[a]\" -map 0:v:0 -map \"[a]\"" : " -map 0:v:0 -map 1:a:0 -af apad";
                var info=new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ffmpeg.exe"),"-hide_banner -loglevel error -y "+inputs+mapping+" -c:v copy -c:a aac -b:a 192k -ar 48000 -ac 2 -shortest -movflags +faststart \""+videoPath+"\"") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
                using(var mux=Process.Start(info)) {
                    string detail=mux.StandardError.ReadToEnd(); mux.WaitForExit();
                    if(mux.ExitCode!=0) throw new Exception("声音合成失败："+detail);
                }
                File.Delete(rawVideo); if(File.Exists(systemPath)) File.Delete(systemPath); if(File.Exists(micPath)) File.Delete(micPath);
            });
            success=File.Exists(videoPath) && new FileInfo(videoPath).Length>0;
        } catch(Exception ex) { lock(errors) errors.AppendLine(ex.ToString()); }
        finally {
            if(systemAudio!=null) { systemAudio.Dispose(); systemAudio=null; }
            if(micAudio!=null) { micAudio.Dispose(); micAudio=null; }
            encoder=null; stopping=false; finalizing=false; p.Dispose();
        }
        if(!success) {
            string detail; lock(errors) detail=errors.ToString();
            File.WriteAllText(Path.Combine(folder,"录屏错误.log"),detail);
        }
        UpdateStatus(); tray.Text="轻录屏 · 点击悬浮球开始";
        Notify(success ? "视频已保存到："+videoPath : "录屏失败，请查看保存目录中的录屏错误.log。");
        if(test) { File.WriteAllText(Path.Combine(folder,"recording-result.txt"),"success="+success+"; stage="+testStage+"; video="+videoPath); if(!success) Environment.ExitCode=1; closing=true; Close(); }
        else if(closing) Close();
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if(encoder!=null) { e.Cancel=true; closing=true; StopRecording(); return; }
        UnregisterHotKey(Handle,1); UnregisterHotKey(Handle,2);
        clock.Stop(); clock.Dispose(); tray.Visible=false; tray.Dispose();
        if(testPlayer!=null) { testPlayer.Stop(); testPlayer.Dispose(); }
        if(panel!=null) panel.Dispose();
        base.OnFormClosing(e);
    }
}
