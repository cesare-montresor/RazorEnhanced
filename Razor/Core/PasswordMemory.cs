using System;
using System.Xml;
using System.Text;
using System.Collections.Generic;
using System.Net;

namespace Assistant
{
	internal class PasswordMemory
	{
		private class Entry
		{
			internal Entry() { }
			internal Entry(string u, string p, IPAddress a) { User = u; Pass = p; Address = a; }
			internal string User;
			internal string Pass;
			internal IPAddress Address;
		}

		private static List<Entry> m_List = new List<Entry>();

		internal static string Encrypt(string source)
		{
			byte[] buff = ASCIIEncoding.ASCII.GetBytes(source);
			int kidx = 0;
			string key = ClientCommunication.GetWindowsUserName();
			if (key == String.Empty)
				return String.Empty;
			StringBuilder sb = new StringBuilder(source.Length * 2 + 2);
			sb.Append("1+");
			for (int i = 0; i < buff.Length; i++)
			{
				sb.AppendFormat("{0:X2}", (byte)(buff[i] ^ ((byte)key[kidx++])));
				if (kidx >= key.Length)
					kidx = 0;
			}
			return sb.ToString();
		}

		internal static string Decrypt(string source)
		{
			byte[] buff = null;

			if (source.Length > 2 && source[0] == '1' && source[1] == '+')
			{
				buff = new byte[(source.Length - 2) / 2];
				string key = ClientCommunication.GetWindowsUserName();
				if (key == String.Empty)
					return String.Empty;
				int kidx = 0;
				for (int i = 2; i < source.Length; i += 2)
				{
					byte c;
					try
					{
						c = Convert.ToByte(source.Substring(i, 2), 16);
					}
					catch
					{
						continue;
					}
					buff[(i - 2) / 2] = (byte)(c ^ ((byte)key[kidx++]));
					if (kidx >= key.Length)
						kidx = 0;
				}
			}
			else
			{
				byte key = (byte)(source.Length / 2);
				buff = new byte[key];

				for (int i = 0; i < source.Length; i += 2)
				{
					byte c;
					try
					{
						c = Convert.ToByte(source.Substring(i, 2), 16);
					}
					catch
					{
						continue;
					}
					buff[i / 2] = (byte)(c ^ key++);
				}
			}
			return ASCIIEncoding.ASCII.GetString(buff);
		}

		internal static void Load(XmlElement xml)
		{
			ClearAll();

			if (xml == null)
				return;

			foreach (XmlElement el in xml.GetElementsByTagName("password"))
			{
				try
				{
					string user = el.GetAttribute("user");
					string addr = el.GetAttribute("ip");

					if (el.InnerText == null)
						continue;

					m_List.Add(new Entry(user, el.InnerText, IPAddress.Parse(addr)));
				}
				catch
				{
				}
			}
		}

		internal static void Save(XmlTextWriter xml)
		{
			if (m_List == null)
				return;

			foreach (Entry e in m_List)
			{
				if (e.Pass != String.Empty)
				{
					xml.WriteStartElement("password");
					try
					{
						xml.WriteAttributeString("user", e.User);
						xml.WriteAttributeString("ip", e.Address.ToString());
						xml.WriteString(e.Pass);
					}
					catch
					{
					}
					xml.WriteEndElement();
				}
			}
		}

		internal static void ClearAll()
		{
			m_List.Clear();
		}

		internal static void Add(string user, string pass, IPAddress addr)
		{
			if (pass == "")
				return;

			user = user.ToLower();
			for (int i = 0; i < m_List.Count; i++)
			{
				Entry e = m_List[i];
				if (e.User == user && e.Address.Equals(addr))
				{
					e.Pass = Encrypt(pass);
					return;
				}
			}

			m_List.Add(new Entry(user, Encrypt(pass), addr));
		}

		internal static string Find(string user, IPAddress addr)
		{
			user = user.ToLower();
			for (int i = 0; i < m_List.Count; i++)
			{
				Entry e = m_List[i];
				if (e.User == user && e.Address.Equals(addr))
					return Decrypt(e.Pass);
			}

			return String.Empty;
		}
	}
}

