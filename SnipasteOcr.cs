using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class SnipasteOcr
{
    private const int TranslationByteLimit = 500;
    private static Icon appIcon;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [STAThread]
    private static void Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        try
        {
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            if (args.Length == 1 && args[0] == "--hotkey")
            {
                RunHotkey();
                return;
            }
            if (args.Length == 1 && args[0] == "--settings")
            {
                EditSettings(null, LoadSettings());
                return;
            }
            byte[] png;
            using (Image image = args.Length == 1 ? Image.FromFile(args[0]) : Clipboard.GetImage())
            using (var buffer = new MemoryStream())
            {
                if (image == null)
                    throw new InvalidOperationException("请先把截图复制到剪贴板，再按识字快捷键。");
                image.Save(buffer, ImageFormat.Png);
                png = buffer.ToArray();
            }
            ShowText(png);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Snipaste 截图识字", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static void RunHotkey()
    {
        using (var hotkey = new ClipboardHotkey(LoadSettings().Hotkey))
        using (var menu = new ContextMenuStrip())
        using (var tray = new NotifyIcon { Icon = appIcon,
            Text = "Snipaste 截图识字", ContextMenuStrip = menu, Visible = true })
        {
            menu.Items.Add("设置", null, (sender, args) => Process.Start(Application.ExecutablePath, "--settings").Dispose());
            menu.Items.Add("退出识字热键", null, (sender, args) => Application.ExitThread());
            Application.Run();
        }
    }

    private sealed class ClipboardHotkey : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr value, IntPtr unused);

        private const int HotkeyMessage = 0x0312;
        private const int ChangeMessage = 0x8001;
        private static readonly IntPtr MessageWindows = new IntPtr(-3);
        private static string WindowTitle { get { return Application.ExecutablePath + " Hotkey"; } }
        private int registeredId;
        private int registeredKeys;

        public ClipboardHotkey(int keys)
        {
            CreateHandle(new CreateParams { Parent = MessageWindows, Caption = WindowTitle });
            if (Register(keys))
                return;
            DestroyHandle();
            throw new InvalidOperationException(Conflict(keys));
        }

        private static string Conflict(int keys)
        {
            return new KeysConverter().ConvertToInvariantString((Keys)keys) + " 无法注册，请选择其他快捷键。";
        }

        public static void Apply(int keys)
        {
            IntPtr window = FindWindowEx(MessageWindows, IntPtr.Zero, null, WindowTitle);
            if (window != IntPtr.Zero && SendMessage(window, ChangeMessage, new IntPtr(keys), IntPtr.Zero) == IntPtr.Zero)
                throw new InvalidOperationException(Conflict(keys));
        }

        private bool Register(int keys)
        {
            if (registeredId != 0 && keys == registeredKeys) return true;
            int nextId = registeredId == 1 ? 2 : 1;
            uint modifiers = 0x4000; // MOD_NOREPEAT
            if ((keys & (int)Keys.Alt) != 0) modifiers |= 1;
            if ((keys & (int)Keys.Control) != 0) modifiers |= 2;
            if ((keys & (int)Keys.Shift) != 0) modifiers |= 4;
            if (!RegisterHotKey(Handle, nextId, modifiers, (uint)(keys & (int)Keys.KeyCode))) return false;
            if (registeredId != 0) UnregisterHotKey(Handle, registeredId);
            registeredId = nextId;
            registeredKeys = keys;
            return true;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == ChangeMessage)
            {
                message.Result = Register(message.WParam.ToInt32()) ? new IntPtr(1) : IntPtr.Zero;
                return;
            }
            if (message.Msg == HotkeyMessage && message.WParam.ToInt32() == registeredId)
            {
                Process.Start(Application.ExecutablePath).Dispose();
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, registeredId);
            DestroyHandle();
        }
    }

    private static string Recognize(byte[] png, string language)
    {
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        string command;
        using (var script = new StreamReader(typeof(SnipasteOcr).Assembly.GetManifestResourceStream("SnipasteOcr.recognize.ps1")))
            command = "& {\r\n" + script.ReadToEnd() + "\r\n} -LanguageTag '" + language + "'";
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var start = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encodedCommand)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using (var worker = Process.Start(start))
        {
            using (Stream input = worker.StandardInput.BaseStream)
                input.Write(png, 0, png.Length);
            string output = worker.StandardOutput.ReadToEnd().TrimEnd();
            worker.WaitForExit();
            if (worker.ExitCode != 0)
                throw new InvalidOperationException("本地识字失败。" + output);
            return output;
        }
    }

    private static void ShowText(byte[] png)
    {
        var preferences = LoadSettings();
        var ocr = preferences.Ocr;
        string[] languages = { "auto", "en-US", "zh-Hans-CN" };
        var picker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
        picker.Items.AddRange(new[] { "自动（中英）", "英语", "简体中文" });
        picker.SelectedIndex = 0;
        picker.Enabled = !ocr.UseModel;
        var translate = new Button { Text = "翻译", AutoSize = true };
        var settings = new Button { Text = "设置", AutoSize = true };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        toolbar.Controls.Add(new Label { Text = "识别语言", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        toolbar.Controls.Add(picker);
        toolbar.Controls.Add(translate);
        toolbar.Controls.Add(settings);

        var source = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, HideSelection = false, AccessibleName = "识别原文" };
        var result = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, ReadOnly = true, AccessibleName = "译文" };
        var sourceGroup = new GroupBox { Text = "识别原文", Dock = DockStyle.Fill };
        var resultGroup = new GroupBox { Text = "译文", Dock = DockStyle.Fill, Visible = false };
        sourceGroup.Controls.Add(source);
        resultGroup.Controls.Add(result);
        var status = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(1, 5, 1, 2) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(sourceGroup, 0, 1);
        layout.Controls.Add(resultGroup, 0, 2);
        layout.Controls.Add(status, 0, 3);

        using (var form = new Form { Text = "Snipaste 截图识字", Icon = appIcon, Size = new Size(620, 410),
            MinimumSize = new Size(420, 270), StartPosition = FormStartPosition.CenterScreen, TopMost = true,
            KeyPreview = true, Font = SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96) })
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) })
        {
            form.Controls.Add(layout);
            form.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Escape)
                    form.Close();
            };
            Func<Task> recognize = async () =>
            {
                layout.RowStyles[2].SizeType = SizeType.Absolute;
                layout.RowStyles[2].Height = 0;
                resultGroup.Visible = false;
                picker.Enabled = translate.Enabled = settings.Enabled = false;
                status.Text = ocr.UseModel ? "正在使用视觉模型识字…" : "正在本机识字…";
                string language = languages[picker.SelectedIndex];
                try
                {
                    string text = ocr.UseModel ? await RecognizeWithModel(client, ocr, png)
                        : await Task.Run(() => Recognize(png, language));
                    if (form.IsDisposed) return;
                    source.Text = text;
                    status.Text = text.Length == 0 ? "没有识别到文字。" : "点击「翻译」后才会发送文字。";
                }
                catch (Exception error)
                {
                    if (form.IsDisposed) return;
                    source.Clear();
                    status.Text = error is InvalidOperationException ? error.Message : "识字失败，请检查识字设置。";
                }
                finally
                {
                    if (!form.IsDisposed)
                    {
                        picker.Enabled = !ocr.UseModel;
                        translate.Enabled = settings.Enabled = true;
                    }
                }
            };
            picker.SelectedIndexChanged += async (sender, args) => await recognize();
            settings.Click += (sender, args) =>
            {
                var updated = EditSettings(form, preferences);
                if (ReferenceEquals(updated, preferences)) return;
                preferences = updated;
                status.Text = "设置已保存，新的识字接口用于下一次截图。";
            };
            translate.Click += async (sender, args) =>
            {
                string text = (source.SelectionLength > 0 ? source.SelectedText : source.Text).Trim();
                if (text.Length == 0)
                {
                    status.Text = "请先识别或输入要翻译的文字。";
                    return;
                }
                if (!preferences.Translation.UseModel && Encoding.UTF8.GetByteCount(text) > TranslationByteLimit)
                {
                    status.Text = "内容超过免费接口的单次上限。请在原文中选中较短一段再翻译。";
                    return;
                }
                picker.Enabled = translate.Enabled = settings.Enabled = false;
                source.ReadOnly = true;
                status.Text = "正在翻译…";
                try
                {
                    string translated = await TranslateAsync(client, preferences, text, languages[picker.SelectedIndex]);
                    if (form.IsDisposed) return;
                    result.Text = translated;
                    layout.RowStyles[2].SizeType = SizeType.Percent;
                    layout.RowStyles[2].Height = 100;
                    resultGroup.Visible = true;
                    status.Text = "翻译完成。";
                }
                catch (Exception error)
                {
                    if (!form.IsDisposed)
                        status.Text = error is InvalidOperationException ? error.Message : "翻译失败，请检查网络和翻译设置。";
                }
                finally
                {
                    if (!form.IsDisposed)
                    {
                        picker.Enabled = !ocr.UseModel;
                        translate.Enabled = settings.Enabled = true;
                        source.ReadOnly = false;
                    }
                }
            };
            form.Shown += async (sender, args) => await recognize();
            Application.Run(form);
        }
    }

    private static async Task<string> TranslateAsync(HttpClient client, Preferences preferences, string text, string languageTag)
    {
        var service = preferences.Translation;
        bool chinese = languageTag == "zh-Hans-CN" || (languageTag == "auto"
            && !Regex.IsMatch(text, @"[\u3040-\u30ff\uac00-\ud7af]")
            && Regex.Matches(text, @"[\u3400-\u9fff]").Count > Regex.Matches(text, "[A-Za-z]").Count);
        var source = GetLanguage(preferences.SourceLanguage);
        var target = GetLanguage(preferences.TargetLanguage);
        if (source.Code != "auto") chinese = source.Code.StartsWith("zh-", StringComparison.Ordinal);
        if (target.Code == "auto") target = GetLanguage(chinese ? "en" : "zh-CN");
        if (service.UseModel)
            return await TranslateWithModel(client, service, text, source, target);
        if (source.Code == "auto") source = GetLanguage(chinese ? "zh-CN" : "en");
        if (source.Code == target.Code)
            throw new InvalidOperationException("原文和目标语种相同，请在设置里换一个语种。");
        string pair = source.Code + "|" + target.Code;
        string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text)
            + "&langpair=" + Uri.EscapeDataString(pair);
        var serializer = new JavaScriptSerializer();
        var response = serializer.Deserialize<TranslationResponse>(await client.GetStringAsync(url));
        if (response == null || response.responseStatus != 200 || response.responseData == null
            || string.IsNullOrWhiteSpace(response.responseData.translatedText))
            throw new InvalidOperationException("MyMemory 没有返回可用译文。");
        return WebUtility.HtmlDecode(response.responseData.translatedText);
    }

    public sealed class TranslationResponse
    {
        public int responseStatus { get; set; }
        public TranslationData responseData { get; set; }
    }

    public sealed class TranslationData
    {
        public string translatedText { get; set; }
    }
}
