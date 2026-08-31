using System;
using System.Drawing;
using System.Windows.Forms;
using Ergonomy.Configuration;

namespace Ergonomy.UI
{
    public partial class PrimaryAlarmForm : Form
    {
        public event Action<bool> FormClosedCallback;
        private System.Windows.Forms.Timer _autoCloseTimer;
        private bool _isAutoClosing = false;
        private bool _isCustomMaximized = false;
        private Rectangle _originalBounds;

        /// <summary>
        /// فرم هشدار اولیه را با تصویر اختیاری و تایمر بستن خودکار بر اساس تنظیمات می‌سازد.
        /// </summary>
        /// <param name="settings">تنظیمات مدت نمایش خودکار هشدار اولیه.</param>
        /// <param name="alarmImage">تصویر تمرین یا null در صورت نبود تصویر.</param>
        // پارامتر ورودی از string به Image تغییر کرد
        public PrimaryAlarmForm(AppSettings settings, Image? alarmImage)
        {
            InitializeComponent();
            this.TopMost = true;
            this.Resize += new System.EventHandler(this.AlarmForm_Resize);
            this.Load += new System.EventHandler(this.AlarmForm_Load);
            this.StartPosition = FormStartPosition.Manual;

            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(workingArea.Right - this.Width, workingArea.Bottom - this.Height);

            // اعمال مستقیم عکس به پیکچرباکس
            if (alarmImage != null)
            {
                this.pictureBox1.Image = alarmImage;
            }

            _autoCloseTimer = new System.Windows.Forms.Timer();
            _autoCloseTimer.Interval = settings.PrimaryAlarmAutoCloseSeconds * 1000;
            _autoCloseTimer.Tick += (sender, e) => {
                _isAutoClosing = true;
                this.Close();
            };
            _autoCloseTimer.Start();
        }

        /// <summary>
        /// محدوده اولیه فرم را برای بازگرداندن از حالت بیشینه‌سازی سفارشی ذخیره می‌کند.
        /// </summary>
        /// <param name="sender">منبع رویداد بارگذاری.</param>
        /// <param name="e">آرگومان رویداد.</param>
        private void AlarmForm_Load(object sender, EventArgs e)
        {
            _originalBounds = this.Bounds;
        }

        /// <summary>
        /// فرمان بیشینه‌سازی ویندوز را به تغییر اندازه سفارشی نصف صفحه تبدیل می‌کند.
        /// </summary>
        /// <param name="m">پیام بومی پنجره.</param>
        protected override void WndProc(ref Message m)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MAXIMIZE = 0xF030;
            if (m.Msg == WM_SYSCOMMAND && (int)m.WParam == SC_MAXIMIZE)
            {
                if (!_isCustomMaximized)
                {
                    Rectangle screen = Screen.PrimaryScreen.WorkingArea;
                    int newWidth = screen.Width / 2;
                    int newHeight = screen.Height / 2;
                    this.Size = new Size(newWidth, newHeight);
                    this.Location = new Point((screen.Width - newWidth) / 2, (screen.Height - newHeight) / 2);
                    _isCustomMaximized = true;
                }
                else
                {
                    this.Bounds = _originalBounds;
                    _isCustomMaximized = false;
                }
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// اندازه قلم پیام هشدار را متناسب با ارتفاع فرم تنظیم می‌کند.
        /// </summary>
        /// <param name="sender">منبع رویداد تغییر اندازه.</param>
        /// <param name="e">آرگومان رویداد.</param>
        private void AlarmForm_Resize(object sender, EventArgs e)
        {
            float newSize = this.ClientSize.Height / 15.0F;
            if (newSize < 12.0F) newSize = 12.0F;
            this.label1.Font = new Font(this.label1.Font.FontFamily, newSize, this.label1.Font.Style);
        }

        /// <summary>
        /// تایمر بستن خودکار را آزاد کرده و به مدیر هشدار اعلام می‌کند که بستن توسط کاربر بوده یا خودکار.
        /// </summary>
        /// <param name="e">اطلاعات بسته شدن فرم.</param>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _autoCloseTimer.Stop();
            _autoCloseTimer.Dispose();
            bool isUserClose = !_isAutoClosing;
            FormClosedCallback?.Invoke(isUserClose);
            base.OnFormClosed(e);
        }
    }
}
