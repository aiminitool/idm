// ============================================================================
// AddDownloadForm.cs - Hộp thoại "Thêm liên kết tải mới"
// ============================================================================
using System;
using System.IO;
using System.Windows.Forms;

namespace IDMLikeDownloaderGui
{
    public class AddDownloadForm : Form
    {
        private readonly TextBox _txtUrl = new() { Left = 110, Top = 15, Width = 380 };
        private readonly TextBox _txtFolder = new() { Left = 110, Top = 50, Width = 300 };
        private readonly Button _btnBrowse = new() { Left = 415, Top = 48, Width = 75, Text = "Chọn..." };
        private readonly TextBox _txtFileName = new() { Left = 110, Top = 85, Width = 380 };
        private readonly NumericUpDown _numThreads = new()
        {
            Left = 110, Top = 120, Width = 60, Minimum = 1, Maximum = 32, Value = 8
        };
        private readonly Button _btnOk = new() { Left = 330, Top = 160, Width = 80, Text = "Bắt đầu tải", DialogResult = DialogResult.OK };
        private readonly Button _btnCancel = new() { Left = 415, Top = 160, Width = 75, Text = "Huỷ", DialogResult = DialogResult.Cancel };

        public string Url => _txtUrl.Text.Trim();
        public string OutputPath => Path.Combine(_txtFolder.Text.Trim(), _txtFileName.Text.Trim());
        public int Threads => (int)_numThreads.Value;

        public AddDownloadForm()
        {
            Text = "Thêm liên kết tải mới";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new System.Drawing.Size(510, 200);
            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            _txtFolder.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            Controls.Add(new Label { Left = 15, Top = 18, Width = 90, Text = "Địa chỉ URL:" });
            Controls.Add(_txtUrl);

            Controls.Add(new Label { Left = 15, Top = 53, Width = 90, Text = "Lưu vào thư mục:" });
            Controls.Add(_txtFolder);
            Controls.Add(_btnBrowse);

            Controls.Add(new Label { Left = 15, Top = 88, Width = 90, Text = "Tên file:" });
            Controls.Add(_txtFileName);

            Controls.Add(new Label { Left = 15, Top = 123, Width = 90, Text = "Số luồng tải:" });
            Controls.Add(_numThreads);

            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);

            _btnBrowse.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = _txtFolder.Text };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _txtFolder.Text = dlg.SelectedPath;
            };

            _txtUrl.TextChanged += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_txtFileName.Text) && !string.IsNullOrWhiteSpace(_txtUrl.Text))
                {
                    try { _txtFileName.Text = MultiThreadDownloader.GuessFileName(_txtUrl.Text); }
                    catch { /* bỏ qua khi URL chưa hợp lệ */ }
                }
            };

            _btnOk.Click += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(Url) || !Uri.TryCreate(Url, UriKind.Absolute, out _))
                {
                    MessageBox.Show(this, "URL không hợp lệ.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                if (string.IsNullOrWhiteSpace(_txtFileName.Text))
                {
                    MessageBox.Show(this, "Vui lòng nhập tên file.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                try { Directory.CreateDirectory(_txtFolder.Text.Trim()); }
                catch
                {
                    MessageBox.Show(this, "Không thể tạo thư mục lưu.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                }
            };
        }
    }
}
