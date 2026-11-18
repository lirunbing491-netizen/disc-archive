using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DiscArchive
{
    internal sealed class VisionSettingsPanel : UserControl
    {
        private readonly Action<VisionOptions> onSave;
        private readonly ComboBox mode = new ComboBox();
        private readonly TextBox endpoint = new TextBox(), model = new TextBox(), apiKey = new TextBox();
        private readonly CheckBox allowFullPhoto = new CheckBox();
        private readonly GroupBox modelGroup = new GroupBox();
        private readonly Label explanation = new Label(), result = new Label();
        private readonly Button save = new Button(), test = new Button();
        private CancellationTokenSource testing;
        private bool editable = true;

        public VisionSettingsPanel(VisionOptions current, Action<VisionOptions> onSave)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (onSave == null) throw new ArgumentNullException("onSave");
            this.onSave = onSave;
            Dock = DockStyle.Fill; AutoScroll = true;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mode.DropDownStyle = ComboBoxStyle.DropDownList; mode.Width = 360;
            mode.Items.AddRange(new object[] { "仅卷标与元数据（默认）", "本机文字识别", "使用自配看图模型" });
            layout.Controls.Add(new Label { Text = "光盘识别方式", AutoSize = true, Margin = new Padding(3, 3, 3, 8) });
            layout.Controls.Add(mode);
            explanation.AutoSize = true; explanation.MaximumSize = new Size(820, 0); explanation.Margin = new Padding(3, 10, 3, 12);
            layout.Controls.Add(explanation);
            modelGroup.Text = "自配看图模型"; modelGroup.Dock = DockStyle.Top; modelGroup.AutoSize = true; modelGroup.Padding = new Padding(10, 20, 10, 10);
            TableLayoutPanel fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddField(fields, "服务地址", endpoint);
            AddField(fields, "准确模型名", model);
            AddField(fields, "密钥（可空）", apiKey);
            apiKey.UseSystemPasswordChar = true;
            Label note = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Text = "填写服务地址及支持图片输入的模型名。登记新盘或重读旧盘时会调用该服务，费用按服务商规则计算。", Margin = new Padding(3, 10, 3, 10) };
            fields.Controls.Add(note, 0, 3); fields.SetColumnSpan(note, 2);
            allowFullPhoto.AutoSize = true;
            allowFullPhoto.MaximumSize = new Size(760, 0);
            allowFullPhoto.Text = "识别不到盘的位置时，允许发送整张照片（可能包含人和背景）";
            allowFullPhoto.Margin = new Padding(3, 3, 3, 10);
            fields.Controls.Add(allowFullPhoto, 0, 4); fields.SetColumnSpan(allowFullPhoto, 2);
            test.Text = "测试看图接口（此次可能计费）"; test.AutoSize = true;
            fields.Controls.Add(test, 0, 5); fields.SetColumnSpan(test, 2);
            Label testNote = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Text = "测试只发送临时生成的随机字符图片，不读取或上传你的盘面照片。返回文字将与图片中的随机字符比对。", Margin = new Padding(3, 8, 3, 8) };
            fields.Controls.Add(testNote, 0, 6); fields.SetColumnSpan(testNote, 2);
            modelGroup.Controls.Add(fields); layout.Controls.Add(modelGroup);
            save.Text = "保存识别设置"; save.AutoSize = true; save.Margin = new Padding(3, 12, 3, 8); layout.Controls.Add(save);
            result.AutoSize = true; result.MaximumSize = new Size(820, 0); result.Margin = new Padding(3, 6, 3, 12); layout.Controls.Add(result);
            Controls.Add(layout);
            endpoint.Text = current.Endpoint ?? ""; model.Text = current.Model ?? ""; apiKey.Text = current.ApiKey ?? "";
            allowFullPhoto.Checked = current.AllowFullPhoto;
            mode.SelectedIndex = current.Mode == "model" ? 2 : current.Mode == "ocr" ? 1 : 0;
            mode.SelectedIndexChanged += delegate { UpdateMode(); result.Text = "识别方式已更改，请保存后生效。"; };
            endpoint.TextChanged += delegate { MarkEdited(); }; model.TextChanged += delegate { MarkEdited(); }; apiKey.TextChanged += delegate { MarkEdited(); };
            allowFullPhoto.CheckedChanged += delegate { MarkEdited(); };
            save.Click += delegate { SaveOptions(); };
            test.Click += delegate { TestEndpoint(); };
            UpdateMode();
        }

        private static void AddField(TableLayoutPanel fields, string label, TextBox box)
        {
            int row = fields.RowCount++; box.Dock = DockStyle.Top;
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 3, 6) }, 0, row);
            fields.Controls.Add(box, 1, row);
        }

        public void SetEditable(bool enabled)
        {
            editable = enabled;
            if (!enabled && testing != null) testing.Cancel();
            UpdateEnabled();
        }

        private void UpdateMode()
        {
            modelGroup.Visible = mode.SelectedIndex == 2;
            explanation.Text = mode.SelectedIndex == 2
                ? "保存后，拍照、选图、拖入、自动接收照片和重读已登记光盘时，均会将盘面图发送到你指定的服务。"
                : mode.SelectedIndex == 1
                ? "本机文字识别：在这台电脑上识别盘面文字，不调用你填写的看图模型。"
                : "仅卷标与元数据：使用光盘卷标与文件信息，不请求模型，也不运行文字识别。";
            UpdateEnabled();
        }

        private void UpdateEnabled()
        {
            bool allow = editable && testing == null;
            mode.Enabled = allow; endpoint.Enabled = allow; model.Enabled = allow; apiKey.Enabled = allow;
            allowFullPhoto.Enabled = allow && mode.SelectedIndex == 2;
            save.Enabled = allow; test.Enabled = allow && mode.SelectedIndex == 2;
        }

        private void MarkEdited() { result.Text = "设置已修改，请保存后生效。"; }

        private VisionOptions ReadOptions()
        {
            VisionOptions options = new VisionOptions();
            options.Mode = mode.SelectedIndex == 2 ? "model" : mode.SelectedIndex == 1 ? "ocr" : "metadata";
            options.Endpoint = endpoint.Text.Trim(); options.Model = model.Text.Trim(); options.ApiKey = apiKey.Text;
            options.AllowFullPhoto = options.Mode == "model" && allowFullPhoto.Checked;
            if (options.Mode == "model") ValidateModelOptions(options);
            return options;
        }

        internal static void ValidateModelOptions(VisionOptions options)
        {
            VisionEndpoint.ValidateEndpoint(options.Endpoint);
            if (String.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("请填写服务提供的准确模型名。");
        }

        private void SaveOptions()
        {
            if (!editable || testing != null) return;
            try
            {
                VisionOptions options = ReadOptions(); onSave(options.Clone());
                result.Text = options.Mode == "model" ? "已启用。后续登记或重读光盘时会调用所填服务。" : "已保存。新设置用于后续的光盘识别。";
            }
            catch (Exception ex) { result.Text = "未保存：" + SafeMessage(ex.Message, apiKey.Text); }
        }

        private void TestEndpoint()
        {
            if (!editable || testing != null) return;
            VisionOptions options;
            try { options = ReadOptions(); }
            catch (Exception ex) { result.Text = "无法测试：" + SafeMessage(ex.Message, apiKey.Text); return; }
            if (options.Mode != "model") return;
            CancellationTokenSource cancellation = new CancellationTokenSource(); testing = cancellation;
            UpdateEnabled(); result.Text = "正在发送随机字符测试图，可能按服务费率计费；不会上传你的盘面照片。";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = null, message;
                try
                {
                    string challenge = NewChallenge(); path = CreateChallengeImage(challenge);
                    cancellation.Token.ThrowIfCancellationRequested();
                    string answer = VisionEndpoint.Read(options.Clone(), path, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    message = MatchesChallenge(answer, challenge)
                        ? "接口已返回，且随机字符匹配：看图测试通过。此结果只验证本次合成图，不代表盘面识别一定准确。"
                        : "接口已返回，但随机字符未匹配，尚不能确认看图成功。返回：" + Limit(SafeMessage(answer, options.ApiKey), 500);
                }
                catch (OperationCanceledException) { message = "测试已取消。已发出的请求仍可能由服务计费。"; }
                catch (Exception ex) { message = "看图测试未通过：" + Limit(SafeMessage(ex.Message, options.ApiKey), 650); }
                finally { if (path != null) try { File.Delete(path); } catch { } }
                string finalMessage = message;
                try
                {
                    if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate
                    {
                        if (IsDisposed || testing != cancellation) return;
                        testing = null; cancellation.Dispose(); result.Text = finalMessage; UpdateEnabled();
                    });
                    else cancellation.Dispose();
                }
                catch (InvalidOperationException) { cancellation.Dispose(); }
            });
        }

        internal static string NewChallenge()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] bytes = new byte[9]; using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            char[] text = new char[bytes.Length]; for (int i = 0; i < text.Length; i++) text[i] = alphabet[bytes[i] % alphabet.Length];
            return new string(text);
        }

        internal static bool MatchesChallenge(string answer, string challenge)
        {
            if (String.IsNullOrEmpty(answer) || String.IsNullOrEmpty(challenge)) return false;
            StringBuilder normalized = new StringBuilder();
            foreach (char c in answer.ToUpperInvariant()) if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) normalized.Append(c);
            return normalized.ToString().Contains(challenge.ToUpperInvariant());
        }

        internal static string CreateChallengeImage(string challenge)
        {
            string directory = Path.Combine(Path.GetTempPath(), "DiscArchive", "接口合成图测试"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (Bitmap image = new Bitmap(960, 960))
                using (Graphics graphics = Graphics.FromImage(image))
                using (Font font = new Font(FontFamily.GenericMonospace, 58, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    graphics.Clear(Color.FromArgb(20, 20, 20));
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.FillEllipse(Brushes.WhiteSmoke, 80, 80, 800, 800);
                    graphics.FillEllipse(Brushes.Black, 412, 412, 136, 136);
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.DrawString(challenge, font, Brushes.Black, new PointF(300, 245));
                    using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) image.Save(stream, ImageFormat.Png);
                }
                return path;
            }
            catch { try { File.Delete(path); } catch { } throw; }
        }

        private static string SafeMessage(string text, string key)
        {
            string result = text ?? "（空返回）";
            return String.IsNullOrEmpty(key) ? result : result.Replace(key, "***");
        }
        private static string Limit(string text, int count) { return text.Length > count ? text.Substring(0, count) + "…" : text; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && testing != null) testing.Cancel();
            base.Dispose(disposing);
        }
    }
}
