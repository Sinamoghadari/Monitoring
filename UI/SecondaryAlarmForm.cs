using System;
using System.Drawing;
using System.Windows.Forms;
using Ergonomy.Configuration;

namespace Ergonomy.UI
{
    public partial class SecondaryAlarmForm : Form
    {
        private System.Windows.Forms.Timer _unclosableTimer;
        private System.Windows.Forms.Timer _autoCloseTimer;
        private bool _isClosable = false;
        private bool _isCustomMaximized = false;
        private Rectangle _originalBounds;

        // دریافت مستقیم عکس رندوم از ورودی
        public SecondaryAlarmForm(AppSettings settings, Image? alarmImage)
        {
            InitializeComponent();
            this.TopMost = true;
            this.Resize += new System.EventHandler(this.AlarmForm_Resize);
            this.Load += new System.EventHandler(this.AlarmForm_Load);
            this.StartPosition = FormStartPosition.Manual;

            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(workingArea.Right - this.Width, workingArea.Bottom - this.Height);

            // قرار دادن عکس درون پیکچرباکس
            if (alarmImage != null)
            {
                this.pictureBox1.Image = alarmImage;
            }

            _unclosableTimer = new System.Windows.Forms.Timer();
            _unclosableTimer.Interval = settings.SecondaryAlarmUnclosableSeconds * 1000;
            _unclosableTimer.Tick += (sender, e) => {
                _isClosable = true;
                _unclosableTimer.Stop();
                _autoCloseTimer.Start();
            };
            _unclosableTimer.Start();

            _autoCloseTimer = new System.Windows.Forms.Timer();
            _autoCloseTimer.Interval = settings.SecondaryAlarmAutoCloseSeconds * 1000;
            _autoCloseTimer.Tick += (sender, e) => {
                this.Close();
            };
        }

        private void AlarmForm_Load(object sender, EventArgs e)
        {
            _originalBounds = this.Bounds;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MAXIMIZE = 0xF030;
            const int SC_CLOSE = 0xF060;

            if (m.Msg == WM_SYSCOMMAND)
            {
                if ((int)m.WParam == SC_MAXIMIZE)
                {
                    if (!_isCustomMaximized)
                    {
                        _originalBounds = this.Bounds;
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
                if ((int)m.WParam == SC_CLOSE && !_isClosable)
                {
                    return; // Ignore close command if not closable yet
                }
            }
            base.WndProc(ref m);
        }

        private void AlarmForm_Resize(object sender, EventArgs e)
        {
            float newSize = this.ClientSize.Height / 15.0F;
            if (newSize < 12.0F) newSize = 12.0F;
            this.label1.Font = new Font(this.label1.Font.FontFamily, newSize, this.label1.Font.Style);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _unclosableTimer.Stop();
            _autoCloseTimer.Stop();
            _unclosableTimer.Dispose();
            _autoCloseTimer.Dispose();
            base.OnFormClosed(e);
        }
    }
}
