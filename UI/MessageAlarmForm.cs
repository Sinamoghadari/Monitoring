using System;
using System.Drawing;
using System.Windows.Forms;

namespace Ergonomy.UI
{
    public class MessageAlarmForm : Form 
    {
        private Label labelMessage;
        private Button btnClose;

        public MessageAlarmForm(string message)
        {
            SetupUI(message);
            
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimizeBox = true;
            this.Text = "پیام سیستم مدیریت";
            this.ShowInTaskbar = true;
            
            // تنظیمات شفافیت و رنگ پس‌زمینه
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.Opacity = 0.85D;

            this.Load += new System.EventHandler(this.MessageAlarmForm_Load);
        }

        private void SetupUI(string message)
        {
            this.Size = new Size(350, 200);
            this.RightToLeft = RightToLeft.Yes;
            this.RightToLeftLayout = true;

            // ساخت دکمه بستن با ظاهر هماهنگ
            btnClose = new Button();
            btnClose.Text = "متوجه شدم";
            btnClose.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            btnClose.Dock = DockStyle.Bottom;
            btnClose.Height = 45;
            btnClose.BackColor = Color.FromArgb(60, 60, 60);
            btnClose.ForeColor = Color.White;
            btnClose.FlatStyle = FlatStyle.Flat;
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Cursor = Cursors.Hand;
            btnClose.Click += (s, e) => { this.Close(); };

            // ساخت لیبل برای نمایش متن با رنگ سفید
            labelMessage = new Label();
            labelMessage.Text = message;
            labelMessage.Font = new Font("Tahoma", 11F, FontStyle.Regular);
            labelMessage.ForeColor = Color.White;
            labelMessage.Dock = DockStyle.Fill;
            labelMessage.TextAlign = ContentAlignment.MiddleCenter;
            labelMessage.Padding = new Padding(10);

            this.Controls.Add(labelMessage);
            this.Controls.Add(btnClose);
        }

        private void MessageAlarmForm_Load(object sender, EventArgs e)
        {
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(workingArea.Right - this.Width - 10, workingArea.Top + 10);
        }
    }
}
