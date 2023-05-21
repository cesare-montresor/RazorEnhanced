using Assistant;
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
        /*
        - DONE:
        Events.OnPacket(packetid, callback)
        Events.OnJournal(pattern, callback)
        Events.OnHotKey(hotkeyid, callback)

        - TODO:
        Events.OnDamage(serial, callback)
        Events.OnSound(soundid, callback)
        Events.OnCast(spellid, callback)
        Events.onTimeout(millisec, callback, repeatOnce)
        */


        /// <summary>
        /// Register a Python function to be called when a packet with a specific PacketID arrives.
        /// </summary>
        /// <param name="callback">Python function to be called.</param>
        /// <param name="packetID">PacketID to filter (-1: Match all)</param>
        public static void OnPacket(IronPython.Runtime.PythonFunction callback, int packetID)
        {
            var script = Scripts.CurrentScript();
            EventManager.Instance.OnPacket(packetID, (path, packetData) =>
            {
                script.ScriptEngine.pyEngine.Call(callback, path, packetData);
            });
        }

        /// <summary>
        /// Register a Python function to be called when a journal entry get added.
        /// </summary>
        /// <param name="callback">Python function to be called.</param>
        /// <param name="textMatch">Text to be matched in the journal (Empty "": Match all, "/regex/": Match Regexpr)</param>
        public static void OnJournal(IronPython.Runtime.PythonFunction callback, string textMatch)
        {
            var script = Scripts.CurrentScript();
            EventManager.Instance.OnJournal(textMatch, (hotkeyMatch) =>
            {
                script.ScriptEngine.pyEngine.Call(callback, hotkeyMatch);
            });
        }

        /// <summary>
        /// Register a Python function to be called when a hotkey get pressed
        /// </summary>
        /// <param name="callback">Python function to be called.</param>
        /// <param name="hotkey">Text to be matched in the journal (Empty "": Match all, "/regex/": Match Regexpr)</param>

        public static void OnHotkey(IronPython.Runtime.PythonFunction callback, string hotkey)
        {
            var script = Scripts.CurrentScript();
            EventManager.Instance.OnHotkey(hotkey, (hotkeyMatch) =>
            {
                script.ScriptEngine.pyEngine.Call(callback, hotkeyMatch);
            });
        }
    }
}
                                                                  