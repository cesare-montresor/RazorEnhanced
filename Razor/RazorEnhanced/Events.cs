using Assistant.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RazorEnhanced
{
    public class Events
    {
        /// <summary>
        /// Register a callback function for a specific PacketID
        /// </summary>
        /// <returns>The path to the saved file.</returns>
        public static void OnPacket(IronPython.Runtime.PythonFunction callback, int packetID)
        {
            var script = Scripts.CurrentScript();
            EventsHandler.Instance.RegisterCallback(packetID, (path, packetData) =>
            {
                script.ScriptEngine.pyEngine.Call(callback, PacketLogger.PathToString[path], packetData);
            });
        }

        /// <summary>
        /// Register a callback function for a specific PacketID
        /// </summary>
        /// <returns>The path to the saved file.</returns>
        public static void OnHotkey(IronPython.Runtime.PythonFunction callback, Keys hotkey)
        {
            var script = Scripts.CurrentScript();
            /*
            Assistant.PacketLogger.SharedInstance.RegisterCallback(hotkey, (path, packetData) =>
            {
                script.PythonEngine.Call(callback, PacketLogger.PathToString[path], packetData);
            });
            */
        }
    }
}
                                                                  