using System;
using System.Drawing;
using System.Windows.Forms;

namespace Ergonomy.UI
{
    // حذف کلمه partial چون فایل دیزاینر نداریم
    public class MessageAlarmForm : Form 
    {
        private Label labelMessage;
        private Button btnClose;

        public MessageAlarmForm(string message)
        {
            // جایگزین InitializeComponent برای ساخت رابط کاربری
            SetupUI(message);
            
            // تنظیمات فرم
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Text = "پیام سیستم مدیریت";
            this.ShowInTaskbar = true;

            this.Load += new System.EventHandler(this.MessageAlarmForm_Load);
        }

        private void SetupUI(string message)
        {
            // ابعاد و ظاهر کلی فرم
            this.Size = new Size(350, 200);
            this.RightToLeft = RightToLeft.Yes;
            this.RightToLeftLayout = true;
            this.BackColor = Color.White;

            // ساخت دکمه بستن
            btnClose = new Button();
            btnClose.Text = "متوجه شدم";
            btnClose.Font = new Font("Tahoma", 10F, FontStyle.Bold);
            btnClose.Dock = DockStyle.Bottom;
            btnClose.Height = 45;
            btnClose.BackColor = Color.LightGray;
            btnClose.Cursor = Cursors.Hand;
            btnClose.Click += (s, e) => { this.Close(); };

            // ساخت لیبل برای نمایش متن
            labelMessage = new Label();
            labelMessage.Text = message;
            labelMessage.Font = new Font("Tahoma", 11F, FontStyle.Regular);
            labelMessage.Dock = DockStyle.Fill;
            labelMessage.TextAlign = ContentAlignment.MiddleCenter;
            labelMessage.Padding = new Padding(10);

            // اضافه کردن کنترل‌ها به فرم
            this.Controls.Add(labelMessage);
            this.Controls.Add(btnClose);
        }

        private void MessageAlarmForm_Load(object sender, EventArgs e)
        {
            // محاسبه موقعیت برای بالای صفحه، سمت راست
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(workingArea.Right - this.Width - 10, workingArea.Top + 10);
        }
    }
}
