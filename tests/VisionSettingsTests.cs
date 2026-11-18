using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using DiscArchive;

internal static class VisionSettingsTests
{
    private static int checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static T Field<T>(object panel, string name) { return (T)panel.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel); }
    private static void Save(VisionSettingsPanel panel) { panel.GetType().GetMethod("SaveOptions", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, null); }
    [STAThread]
    public static void Main()
    {
        string imagePath = null;
        try
        {
            string first = VisionSettingsPanel.NewChallenge(), second = VisionSettingsPanel.NewChallenge();
            Check(first.Length == 9 && second.Length == 9 && first != second, "each test has a fresh random image challenge");
            Check(VisionSettingsPanel.MatchesChallenge("识别文字：" + first.ToLowerInvariant(), first), "challenge matches image transcription");
            Check(!VisionSettingsPanel.MatchesChallenge("接口已正常连接", first), "a generic successful response is not a successful vision test");
            Check(!VisionSettingsPanel.MatchesChallenge(second, first), "different challenge cannot validate image understanding");
            imagePath = VisionSettingsPanel.CreateChallengeImage(first);
            using (Bitmap image = new Bitmap(imagePath))
            {
                Check(image.Width == 960 && image.Height == 960, "test sends a generated image only");
                double[] disc = DiscVision.Locate(image);
                Check(disc != null && disc[2] > 350 && disc[2] < 450, "synthetic challenge passes the real privacy disc locator");
            }
            VisionOptions saved = null; int saveCount = 0;
            using (VisionSettingsPanel panel = new VisionSettingsPanel(new VisionOptions(), delegate(VisionOptions options) { saved = options; saveCount++; }))
            {
                Check(Field<ComboBox>(panel,"mode").SelectedIndex == 0, "default is metadata with no model or OCR");
                Check(!Field<CheckBox>(panel,"allowFullPhoto").Checked, "full photo upload is off by default");
                Save(panel); Check(saved.Mode == "metadata", "metadata can be saved without model credentials");
                Field<ComboBox>(panel,"mode").SelectedIndex = 1; Save(panel); Check(saved.Mode == "ocr", "local OCR selection can be saved");
                Field<ComboBox>(panel,"mode").SelectedIndex = 2;
                Field<TextBox>(panel,"endpoint").Text = "https://example.invalid/v1";
                Field<TextBox>(panel,"model").Text = "user-exact-vision-model";
                Check(Field<TextBox>(panel,"apiKey").UseSystemPasswordChar, "optional API key is masked");
                Save(panel); Check(saved.Mode == "model" && saved.ApiKey == "" && saved.Model == "user-exact-vision-model", "user model and an empty API key save without a request");
                Check(!saved.AllowFullPhoto, "saving model mode alone does not authorize whole-photo upload");
                Field<CheckBox>(panel,"allowFullPhoto").Checked = true;
                Save(panel); Check(saved.AllowFullPhoto, "explicitly checking full photo consent saves true");
                Field<ComboBox>(panel,"mode").SelectedIndex = 0; Save(panel);
                Check(saved.Mode == "metadata" && !saved.AllowFullPhoto, "metadata mode never carries whole-photo upload consent");
                Field<ComboBox>(panel,"mode").SelectedIndex = 1; Save(panel);
                Check(saved.Mode == "ocr" && !saved.AllowFullPhoto, "local OCR never carries whole-photo upload consent");
                Field<ComboBox>(panel,"mode").SelectedIndex = 2;
                int before = saveCount; panel.SetEditable(false); Save(panel);
                Check(saveCount == before && !Field<ComboBox>(panel,"mode").Enabled && !Field<Button>(panel,"test").Enabled, "busy processing locks edits, saves and endpoint tests");
                panel.SetEditable(true); Check(Field<Button>(panel,"save").Enabled, "editing resumes after processing");
                Field<TextBox>(panel,"endpoint").Text = "not a valid address"; Save(panel);
                Check(saveCount == before, "invalid settings do not replace saved options");
            }
            Console.WriteLine("PASS " + checks + " checks. No external endpoint or real photo was used.");
        }
        finally { if (imagePath != null && File.Exists(imagePath)) File.Delete(imagePath); }
    }
}
