using System;
using System.Drawing;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading;

namespace Assistant.MapUO
{
	/// <summary>
	/// Summary description for MapWindow.
	/// </summary>
	internal class MapWindow : System.Windows.Forms.Form
	{
		internal const int WM_NCLBUTTONDOWN = 0xA1;
		internal const int HT_CAPTION = 0x2;
		private UOMapControl uoMapControl1;
        internal static UOMapControl uoMapControlstatic;

		[DllImport("user32.dll")]
		private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
		[DllImport("user32.dll")]
		internal static extern bool ReleaseCapture();

		/// <summary>
		/// Required designer variable.
		/// </summary>
		private System.ComponentModel.Container components = null;

		internal MapWindow()
		{
			//
			// Required for Windows Form Designer support
			//

			InitializeComponent();
			this.ContextMenu = new ContextMenu();
			this.ContextMenu.Popup += new EventHandler(ContextMenu_Popup);
            this.Location = new Point(RazorEnhanced.Settings.General.ReadInt("MapX"), RazorEnhanced.Settings.General.ReadInt("MapY"));
            this.ClientSize = new Size(RazorEnhanced.Settings.General.ReadInt("MapW"), RazorEnhanced.Settings.General.ReadInt("MapH"));

			if (this.Location.X < -10 || this.Location.Y < -10)
				this.Location = Point.Empty;

			if (this.Width < 50)
				this.Width = 50;
			if (this.Height < 50)
				this.Height = 50;

			//
			// TODO: Add any constructor code after InitializeComponent call
			//

			this.uoMapControl1.FullUpdate();
			ClientCommunication.SetMapWndHandle(this);
            uoMapControlstatic = this.uoMapControl1;
		}

		internal class MapMenuItem : MenuItem
		{
			internal MapMenuItem(System.String text, System.EventHandler onClick)
				: base(text, onClick)
			{
				Tag = null;
			}
		}

		private void ContextMenu_Popup(object sender, EventArgs e)
		{
			ContextMenu cm = this.ContextMenu;
			cm.MenuItems.Clear();
			if (World.Player != null && PacketHandlers.Party.Count > 0)
			{
				MapMenuItem mi = new MapMenuItem("You", new EventHandler(FocusChange));
				mi.Tag = World.Player.Serial;
				cm.MenuItems.Add(mi);
				foreach (Serial s in PacketHandlers.Party)
				{
					Mobile m = World.FindMobile(s);
					if (m.Name != null)
					{
						mi = new MapMenuItem(m.Name, new EventHandler(FocusChange));
						mi.Tag = s;
						if (this.uoMapControl1.FocusMobile == m)
							mi.Checked = true;
						cm.MenuItems.Add(mi);

					}
				}
			}
			this.ContextMenu = cm;
		}


		private void FocusChange(object sender, System.EventArgs e)
		{
			if (sender != null)
			{
				MapMenuItem mItem = sender as MapMenuItem;

				if (mItem != null)
				{
					Serial s = (Serial)mItem.Tag;
					Mobile m = World.FindMobile(s);
					this.uoMapControl1.FocusMobile = m;
					this.uoMapControl1.FullUpdate();
				}
			}
		}
		public static void Initialize()
		{
			new ReqPartyLocTimer().Start();

			//HotKey.Add(HKCategory.Misc, LocString.ToggleMap, new HotKeyCallback(ToggleMap));
		}

		internal static void ToggleMap()
		{
			if (World.Player != null && Engine.MainWindow != null)
			{
				if (Engine.MainWindow.MapWindow == null)
				{
					Engine.MainWindow.MapWindow = new Assistant.MapUO.MapWindow();
					Engine.MainWindow.MapWindow.Show();
					Engine.MainWindow.MapWindow.BringToFront();
				}
				else
				{
					if (Engine.MainWindow.MapWindow.Visible)
					{
						Engine.MainWindow.MapWindow.Hide();
						Engine.MainWindow.BringToFront();
						ClientCommunication.BringToFront(ClientCommunication.FindUOWindow());
					}
					else
					{
						Engine.MainWindow.MapWindow.Show();
						Engine.MainWindow.MapWindow.BringToFront();
						Engine.MainWindow.MapWindow.TopMost = true;
						ClientCommunication.SetMapWndHandle(Engine.MainWindow.MapWindow);
					}
				}
			}
		}

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (components != null)
				{
					components.Dispose();
				}
			}
			base.Dispose(disposing);
		}

		#region Windows Form Designer generated code
		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
			this.SuspendLayout();
			// 
			// uoMapControl1
			// 
			this.uoMapControl1 = new UOMapControl();
			this.uoMapControl1.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
			this.uoMapControl1.Dock = System.Windows.Forms.DockStyle.Fill;
			this.uoMapControl1.Location = new System.Drawing.Point(0, 0);
			this.uoMapControl1.Name = "uoMapControl1";
			this.uoMapControl1.Size = new System.Drawing.Size(292, 266);
			this.uoMapControl1.TabIndex = 0;
			// 
			// MapWindow
			// 
			this.AutoScaleBaseSize = new System.Drawing.Size(5, 13);
			this.BackColor = System.Drawing.SystemColors.Control;
			this.ClientSize = new System.Drawing.Size(292, 266);
			this.Controls.Add(this.uoMapControl1);
			this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.SizableToolWindow;
			this.MaximizeBox = false;
			this.Name = "MapWindow";
			this.ShowInTaskbar = false;
			this.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
			this.Text = "UO Positioning System";
			this.TopMost = true;
			this.Closing += new System.ComponentModel.CancelEventHandler(this.MapWindow_Closing);
			this.Deactivate += new System.EventHandler(this.MapWindow_Deactivate);
			this.Load += new System.EventHandler(this.MapWindow_Load);
			this.MouseDown += new System.Windows.Forms.MouseEventHandler(this.MapWindow_MouseDown);
			this.Move += new System.EventHandler(this.MapWindow_Move);
			this.Resize += new System.EventHandler(this.MapWindow_Resize);
			this.ResumeLayout(false);

		}
		#endregion

		internal void CheckLocalUpdate(Mobile mob)
		{
			if (mob.InParty)
				this.uoMapControl1.FullUpdate();
		}

		private static Font m_RegFont = new Font("Courier New", 8);
		/*private   int ButtonRows;
		protected override void OnPaint(PaintEventArgs e)
		{
		base.OnPaint(e);
		if ( PacketHandlers.Party.Count > 0 )
		{
		//75x15
		int xcount = 0;
		int ycount = 0;
		Point org = new Point(0, (ButtonRows * 15));
		if (this.FormBorderStyle == FormBorderStyle.None)
		{
		org = new Point(0,  (ButtonRows * 15) + 32);
		}

		foreach ( Serial s in PacketHandlers.Party )
		{
		Mobile mob = World.FindMobile( s );
		if ( mob == null )
			continue;

		if (((75 * (xcount+1)) - this.Width) > 0)
		{
			xcount = 0;
			ycount++;
		}
		string name = mob.Name;
		if ( name != null && name.Length > 8)
		{
			name = name.Substring(0, 8);
			name += "...";
		}
		else if ( name == null || name.Length < 1 )
		{
			name = "(Not Seen)";
		}

		Point drawPoint = new Point(org.X + (75 * xcount), org.Y + (15*ycount));
		mob.ButtonPoint = new Point2D( drawPoint.X, drawPoint.Y );
		e.Graphics.FillRectangle( Brushes.Black, drawPoint.X, drawPoint.Y, 75, 15 );
		e.Graphics.DrawRectangle(Pens.Gray, drawPoint.X, drawPoint.Y, 75, 15 );
		e.Graphics.DrawString(name, m_RegFont, Brushes.White, drawPoint);
		xcount++;
		}
		if(ycount > 0)
		ButtonRows = ycount;
		}

		}*/

		private class ReqPartyLocTimer : Timer
		{
			internal ReqPartyLocTimer()
				: base(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.0))
			{
			}

			protected override void OnTick()
			{
				// never send this packet to encrypted servers (could lead to OSI detecting razor)
				if (ClientCommunication.ServerEncrypted)
				{
					Stop();
					return;
				}

				if (Engine.MainWindow == null || Engine.MainWindow.MapWindow == null || !Engine.MainWindow.MapWindow.Visible)
					return; // don't bother when the map window isnt visible

				if (World.Player != null && PacketHandlers.Party.Count > 0)
				{
					if (PacketHandlers.SpecialPartySent > PacketHandlers.SpecialPartyReceived)
					{
						// If we sent more than we received then the server stopped responding
						// in that case, wait a long while before trying again
						PacketHandlers.SpecialPartySent = PacketHandlers.SpecialPartyReceived = 0;
						this.Interval = TimeSpan.FromSeconds(5.0);
						return;
					}
					else
					{
						this.Interval = TimeSpan.FromSeconds(1.0);
					}

					bool send = false;
					foreach (Serial s in PacketHandlers.Party)
					{
						Mobile m = World.FindMobile(s);

						if (m == World.Player)
							continue;

						if (m == null || Utility.Distance(World.Player.Position, m.Position) > World.Player.VisRange || !m.Visible)
						{
							send = true;
							break;
						}
					}

					if (send)
					{
						PacketHandlers.SpecialPartySent++;
						ClientCommunication.SendToServer(new QueryPartyLocs());
					}
				}
				else
				{
					this.Interval = TimeSpan.FromSeconds(1.0);
				}
			}
		}

		private void RequestPartyLocations()
		{
			if (World.Player != null && PacketHandlers.Party.Count > 0)
				ClientCommunication.SendToServer(new QueryPartyLocs());
		}

		internal void UpdateMap()
		{
			ClientCommunication.SetMapWndHandle(this);
			this.uoMapControl1.UpdateMap();
		}

		private void MapWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
		{
			if (Assistant.Engine.Running)
			{
				e.Cancel = true;
				this.Hide();
				Engine.MainWindow.BringToFront();
				ClientCommunication.BringToFront(ClientCommunication.FindUOWindow());
			}
		}

		internal void PlayerMoved()
		{
			if (this.Visible && this.uoMapControl1 != null)
				this.uoMapControl1.FullUpdate();
		}

		private void MapWindow_Resize(object sender, System.EventArgs e)
		{
			this.uoMapControl1.Height = this.Height;
			this.uoMapControl1.Width = this.Width;

			if (this.Width < 50)
				this.Width = 50;
			if (this.Height < 50)
				this.Height = 50;

			this.Refresh();

			RazorEnhanced.Settings.General.WriteInt("MapX", this.Location.X);
            RazorEnhanced.Settings.General.WriteInt("MapY", this.Location.Y);
            RazorEnhanced.Settings.General.WriteInt("MapW", this.ClientSize.Width);
            RazorEnhanced.Settings.General.WriteInt("MapH", this.ClientSize.Height);
		}

		private void MapWindow_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
		{
			if (e.Clicks == 2)
			{
				if (this.FormBorderStyle == FormBorderStyle.None)
					this.FormBorderStyle = FormBorderStyle.SizableToolWindow;
				else
					this.FormBorderStyle = FormBorderStyle.None;
			}

			if (e.Button == MouseButtons.Left)
			{
				ReleaseCapture();
				SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, IntPtr.Zero);
				/*foreach ( Serial s in PacketHandlers.Party )
				{       
				Mobile m = World.FindMobile( s );
				if ( m == null )
					continue;
				Rectangle rec = new Rectangle( m.ButtonPoint.X, m.ButtonPoint.Y, 75, 15 );
				if ( rec.Contains( e.X, e.Y ) )
				{
					this.Map.FocusMobile = m;
					this.Map.Refresh();
				}
				}*/
			}
			this.uoMapControl1.MapClick(e);
		}

		private void Map_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
		{
			MapWindow_MouseDown(sender, e);
		}

		private void MapWindow_Move(object sender, System.EventArgs e)
		{
            RazorEnhanced.Settings.General.WriteInt("MapX", this.Location.X);
            RazorEnhanced.Settings.General.WriteInt("MapY", this.Location.Y);
            RazorEnhanced.Settings.General.WriteInt("MapW", this.ClientSize.Width);
            RazorEnhanced.Settings.General.WriteInt("MapH", this.ClientSize.Height);
		}

		private void MapWindow_Deactivate(object sender, System.EventArgs e)
		{
			if (this.TopMost)
				this.TopMost = false;
		}

		private void MapWindow_Load(object sender, EventArgs e)
		{

		}
	}
}
