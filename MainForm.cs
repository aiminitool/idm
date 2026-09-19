// ============================================================================
// MainForm.cs - Cửa sổ chính: danh sách tải kiểu IDM, có thanh tiến độ trong
// từng dòng, nút Thêm / Bắt đầu / Tạm dừng / Tiếp tục / Xoá / Mở thư mục.
// ============================================================================
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IDMLikeDownloaderGui
{
    public class MainForm : Form
    {
        private readonly ListView _list = new()
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            OwnerDraw = true,
            HideSelection = false
        };

        private readonly Button _btnAdd = new() { Text = "+ Thêm liên kết", Width = 120 };
        private readonly Button _btnStart = new() { Text = "▶ Bắt đầu", Width = 90 };
        private readonly Button _btnPause = new() { Text = "⏸ Tạm dừng", Width = 90 };
        private readonly Button _btnRemove = new() { Text = "✕ Xoá", Width = 80 };
        private readonly Button _btnOpenFolder = new() { Text = "Mở thư mục", Width = 100 };

        public MainForm()
        {
            Text = "NetDM - Trình tải file (kiểu IDM)";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(920, 480);
            MinimumSize = new Size(700, 350);

            BuildToolbar();
            BuildListView();

            _btnAdd.Click += (_, _) => OnAddClicked();
            _btnStart.Click += (_, _) => OnStartClicked();
            _btnPause.Click += (_, _) => OnPauseClicked();
            _btnRemove.Click += (_, _) => OnRemoveClicked();
            _btnOpenFolder.Click += (_, _) => OnOpenFolderClicked();

            FormClosing += (_, _) => PauseAllRunning();
        }

        private void BuildToolbar()
        {
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(8, 6, 8, 6),
                FlowDirection = FlowDirection.LeftToRight
            };
            foreach (var b in new[] { _btnAdd, _btnStart, _btnPause, _btnRemove, _btnOpenFolder })
            {
                b.Height = 30;
                b.Margin = new Padding(0, 0, 8, 0);
                toolbar.Controls.Add(b);
            }
            Controls.Add(toolbar);
            Controls.Add(_list); // ListView thêm sau để nằm dưới toolbar (Dock=Fill)
        }

        private void BuildListView()
        {
            _list.Columns.Add("Tên file", 260);
            _list.Columns.Add("Kích thước", 100);
            _list.Columns.Add("Tiến độ", 220);
            _list.Columns.Add("Tốc độ", 110);
            _list.Columns.Add("Trạng thái", 150);

            _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
            _list.DrawItem += (_, e) => { /* để trống, DrawSubItem lo phần vẽ */ };
            _list.DrawSubItem += List_DrawSubItem;
        }

        // --------------------------------------------------------------
        // Vẽ cột "Tiến độ" thành thanh progress bar giống IDM
        // --------------------------------------------------------------
        private void List_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 2 || e.Item?.Tag is not MultiThreadDownloader dl)
            {
                e.DrawDefault = true;
                return;
            }

            var bounds = e.Bounds;
            e.Graphics.FillRectangle(SystemBrushes.Window, bounds);

            double percent = dl.TotalSize > 0
                ? Math.Min(100.0, dl.TotalDownloaded * 100.0 / dl.TotalSize)
                : 0;

            var barRect = new Rectangle(bounds.Left + 3, bounds.Top + 3, bounds.Width - 6, bounds.Height - 6);
            using (var borderPen = new Pen(Color.Gray))
                e.Graphics.DrawRectangle(borderPen, barRect);

            int fillWidth = (int)(barRect.Width * percent / 100.0);
            if (fillWidth > 0)
            {
                var fillRect = new Rectangle(barRect.Left, barRect.Top, fillWidth, barRect.Height);
                Color barColor = dl.Status switch
                {
                    DownloadStatus.Completed => Color.MediumSeaGreen,
                    DownloadStatus.Error => Color.IndianRed,
                    DownloadStatus.Paused => Color.Goldenrod,
                    _ => Color.DodgerBlue
                };
                using var fillBrush = new SolidBrush(barColor);
                e.Graphics.FillRectangle(fillBrush, fillRect);
            }

            string text = $"{percent:0.0}%";
            var textSize = e.Graphics.MeasureString(text, _list.Font);
            var textPos = new PointF(
                barRect.Left + (barRect.Width - textSize.Width) / 2,
                barRect.Top + (barRect.Height - textSize.Height) / 2);
            e.Graphics.DrawString(text, _list.Font, SystemBrushes.WindowText, textPos);
        }

        // --------------------------------------------------------------
        // Xử lý nút bấm
        // --------------------------------------------------------------
        private void OnAddClicked()
        {
            using var dlg = new AddDownloadForm();
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var downloader = new MultiThreadDownloader(dlg.Url, dlg.OutputPath, dlg.Threads);
            var item = new ListViewItem(new[] { downloader.FileName, "?", "", "", "Chờ" })
            {
                Tag = downloader
            };
            downloader.Updated += OnDownloaderUpdated;

            _list.Items.Add(item);
            item.Selected = true;

            _ = downloader.StartAsync(); // chạy nền, không chặn UI
        }

        private void OnStartClicked()
        {
            foreach (ListViewItem item in _list.SelectedItems)
                if (item.Tag is MultiThreadDownloader dl)
                    _ = dl.StartAsync();
        }

        private void OnPauseClicked()
        {
            foreach (ListViewItem item in _list.SelectedItems)
                if (item.Tag is MultiThreadDownloader dl)
                    dl.Pause();
        }

        private void OnRemoveClicked()
        {
            var toRemove = new System.Collections.Generic.List<ListViewItem>();
            foreach (ListViewItem item in _list.SelectedItems)
                toRemove.Add(item);

            foreach (var item in toRemove)
            {
                if (item.Tag is MultiThreadDownloader dl)
                {
                    dl.Pause();
                    dl.DeleteFiles();
                }
                _list.Items.Remove(item);
            }
        }

        private void OnOpenFolderClicked()
        {
            if (_list.SelectedItems.Count == 0) return;
            if (_list.SelectedItems[0].Tag is not MultiThreadDownloader dl) return;

            try
            {
                if (File.Exists(dl.OutputPath))
                    Process.Start("explorer.exe", $"/select,\"{dl.OutputPath}\"");
                else
                    Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(dl.OutputPath)}\"");
            }
            catch { /* bỏ qua nếu không mở được */ }
        }

        private void PauseAllRunning()
        {
            foreach (ListViewItem item in _list.Items)
                if (item.Tag is MultiThreadDownloader dl)
                    dl.Pause();
        }

        // --------------------------------------------------------------
        // Cập nhật dòng khi downloader báo tiến độ (có thể gọi từ thread nền)
        // --------------------------------------------------------------
        private void OnDownloaderUpdated(MultiThreadDownloader dl)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => OnDownloaderUpdated(dl))); return; }

            ListViewItem? item = FindItem(dl);
            if (item == null) return;

            item.SubItems[1].Text = MultiThreadDownloader.FormatSize(dl.TotalSize);
            item.SubItems[3].Text = dl.Status == DownloadStatus.Downloading
                ? MultiThreadDownloader.FormatSize((long)dl.CurrentSpeedBytesPerSec) + "/s"
                : "";
            item.SubItems[4].Text = dl.Status switch
            {
                DownloadStatus.Waiting => "Chờ",
                DownloadStatus.Probing => "Đang chuẩn bị...",
                DownloadStatus.Downloading => "Đang tải",
                DownloadStatus.Paused => "Đã tạm dừng",
                DownloadStatus.Completed => "Hoàn tất",
                DownloadStatus.Error => "Lỗi: " + dl.ErrorMessage,
                _ => ""
            };

            _list.Invalidate(item.Bounds); // vẽ lại thanh tiến độ
        }

        private ListViewItem? FindItem(MultiThreadDownloader dl)
        {
            foreach (ListViewItem item in _list.Items)
                if (ReferenceEquals(item.Tag, dl))
                    return item;
            return null;
        }
    }
}
