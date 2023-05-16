using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Assistant.PacketLogger;

namespace Assistant.Network
{
    public class EventManager
    {
        static readonly public EventManager Instance = new EventManager();

        private Dictionary<Thread, Dictionary<int, HashSet<OnLogPacketDataCallBack>>> m_Callbacks = new Dictionary<Thread, Dictionary<int, HashSet<OnLogPacketDataCallBack>>>();


        public void RegisterCallback(int packetID, OnLogPacketDataCallBack callback)
        {
            Thread thread = Thread.CurrentThread;
            if (!m_Callbacks.ContainsKey(thread))
            {
                m_Callbacks[thread] = new Dictionary<int, HashSet<OnLogPacketDataCallBack>>();
            }
            if (!m_Callbacks[thread].ContainsKey(packetID))
            {
                m_Callbacks[thread][packetID] = new HashSet<OnLogPacketDataCallBack>();
            }
            m_Callbacks[thread][packetID].Add(callback);
        }

        public void didRecievePacket(PacketPath path, byte[] packetData)
        {
            var notify = new Task(() =>
            {
                int packetID = packetData[0];

                //var running = m_Callbacks.Where(thread => thread.Key.ThreadState == ThreadState.Running);
                foreach (var pairs in m_Callbacks)
                {
                    var thread = pairs.Key;
                    var packet = pairs.Value;
                    if (!packet.ContainsKey(packetID)) { continue; }
                    var callbacks = packet[packetID];
                    callbacks.ToList().ForEach(callback =>
                    {
                        try { callback(path, packetData); } catch { }
                    });
                }
            });
            notify.Start();
        }
    }
}
