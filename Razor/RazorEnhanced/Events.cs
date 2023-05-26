﻿using Assistant;
using System.Collections.Generic;
using System.Linq;

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
        Events.OnAnimation(animationid, callback)
        Events.OnEffect(effectid, callback)
        Events.OnCast(spellid, callback)
        Events.OnTimeout(millisec, callback, repeat)
        Events.OnTrade(serial, callback)

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
                var pyPacketData = new IronPython.Runtime.PythonList();
                pyPacketData.extend(packetData);
                var result = script.ScriptEngine.pyEngine.Call(callback, path, pyPacketData);

                //TODO: ability drop the packet using the return ? 
                // if (result == null) { return false; } //don't drop
                // return (bool)result;
            });
        }

        /// <summary>
        /// Register a Python function to be called when a journal entry get added.
        /// </summary>
        /// <param name="callback">Python function to be called.</param>
        /// <param name="textMatchs">Texts to be matched in the journal (Empty "": Match all, "/regex/": Match Regexpr)</param>
        public static void OnJournal(IronPython.Runtime.PythonFunction callback, IronPython.Runtime.PythonList textMatchs)
        {
            var script = Scripts.CurrentScript();
            textMatchs.ToList().ForEach(textMatch =>
            {
                EventManager.Instance.OnJournal(textMatch.ToString(), (journalEntry) =>
                {
                    script.ScriptEngine.pyEngine.Call(callback, journalEntry);
                    //TODO: ability suppress journal ? 
                    // if (result == null) { return false; } //drop suppress
                    // return (bool)result;
                });
            });
        }
        public static void OnJournal(IronPython.Runtime.PythonFunction callback, string textMatch)
        {
            var textMatchs = new IronPython.Runtime.PythonList();
            textMatchs.append(textMatch);
            OnJournal(callback, textMatchs);
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
                //TODO: ability stop propagation of the event journal ? 
                // if (result == null) { return false; } //don't stop propagation
                // return (bool)result;
            });
        }
    }
}
                                                                  