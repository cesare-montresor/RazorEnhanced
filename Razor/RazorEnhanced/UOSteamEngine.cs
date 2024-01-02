using Accord.Math;
using IronPython.Runtime;
using Microsoft.Scripting.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RazorEnhanced.UOS
{
    //
    public class UOSteamEngine
    {
        static bool DEFAULT_ISOLATION = false;

        private int m_lastMount;
        private int m_toggle_LeftSave;
        private int m_toggle_RightSave;
        private Journal m_journal;
        private Mutex m_mutex;
        private readonly Lexer m_Lexer = new Lexer();

        private bool m_Loaded = false;
        private bool m_UseIsolation = DEFAULT_ISOLATION;
        private Namespace m_Namespace;
        private Interpreter m_Interpreter;
        private Script m_Script;

        public void SetTrace(UOSTracebackDelegate traceDelegate) {
            m_Script.OnTraceback += traceDelegate;
        }

        public Script Script { get { return m_Script; } }
        public Interpreter Interpreter { get { return m_Interpreter; } }
        public Namespace Namespace { 
            get { return m_Namespace; } 
            set {
                if (value != m_Namespace){
                    m_Namespace = value;
                }
            } 
        }

        public bool UseIsolation {
            get { return m_UseIsolation; }
            set {
                if (value != m_UseIsolation) {
                    m_UseIsolation = value;
                    UpdateNamespace();
                }
            }
        }

        public class StopException : Exception
        {
            public StopException() : base("Stop encountered") { }
        }

        // useOnceIgnoreList
        private readonly List<int> m_serialUseOnceIgnoreList;

        private static readonly string DEFAULT_FRIEND_LIST = "UOS";


        internal List<string> AllAliases()
        {
            var list = Interpreter._aliasHandlers.Keys.ToList();
            list.AddRange(m_Interpreter. _alias.Keys.ToArray());
            return list;
        }

        internal List<string> AllKeywords()
        {
            List<string> retlist = new List<string>();
            foreach (var cmd in m_Interpreter._commandHandlers)
            {
                var handler = cmd.Value;
                if (handler.Method.Name == "ExpressionCommand")
                {
                    continue;
                }
                var documentation = XMLCommentReader.GetDocumentation(handler.Method);
                documentation = XMLCommentReader.RemoveBaseIndentation(documentation);
                var methodSummary = XMLCommentReader.ExtractXML(documentation, "summary");
                var returnDesc = XMLCommentReader.ExtractXML(documentation, "returns");
                if (methodSummary == "")
                {
                    retlist.Add(cmd.Key);
                }
                else
                {
                    retlist.Add(methodSummary);
                }
            }

            foreach (var expr in m_Interpreter._exprHandlers)
            {
                var handler = expr.Value;
                if (handler.Method.Name == "ExpressionCommand")
                {
                    continue;
                }
                var documentation = XMLCommentReader.GetDocumentation(handler.Method);
                documentation = XMLCommentReader.RemoveBaseIndentation(documentation);
                var methodSummary = XMLCommentReader.ExtractXML(documentation, "summary");
                var returnDesc = XMLCommentReader.ExtractXML(documentation, "returns");
                if (methodSummary == "")
                {
                    retlist.Add(expr.Key);
                }
                else
                {
                    retlist.Add(methodSummary);
                }
            }
            return retlist;
        }

        

        public UOSteamEngine()
        {
            m_mutex = new Mutex();
            m_serialUseOnceIgnoreList = new List<int>();

            m_Loaded = false;
            m_toggle_LeftSave = 0;
            m_toggle_RightSave = 0;
            m_lastMount = 0;

            string configFile = System.IO.Path.Combine(Assistant.Engine.RootPath, "Config", "UOS.config.json");
            UpdateNamespace();
        }

                                          
        private void RegisterAlias()
        {
            m_Interpreter.RegisterAliasHandler("ground", AliasHandler);
            m_Interpreter.RegisterAliasHandler("any", AliasHandler);
            m_Interpreter.RegisterAliasHandler("backpack", AliasHandler);
            m_Interpreter.RegisterAliasHandler("self", AliasHandler);
            m_Interpreter.RegisterAliasHandler("bank", AliasHandler);
            m_Interpreter.RegisterAliasHandler("lasttarget", AliasHandler);
            m_Interpreter.RegisterAliasHandler("last", AliasHandler);
            m_Interpreter.RegisterAliasHandler("mount", AliasHandler);
            m_Interpreter.RegisterAliasHandler("lefthand", AliasHandler);
            m_Interpreter.RegisterAliasHandler("righthand", AliasHandler);
            //m_Interpreter.RegisterAliasHandler("lastobject", AliasHandler);  //TODO: how to you get the "last object" in razor ?
            m_Interpreter.SetAlias("lastobject", 0); //TODO: not implemented

            m_Interpreter.SetAlias("found", 0);
            m_Interpreter.SetAlias("enemy", 0);
            m_Interpreter.SetAlias("friend", 0);


        }

        static uint AliasHandler(string alias)
        {
            int everywhere = -1;
            try
            {
                switch (alias.ToLower())
                {
                    case "ground": return (uint)everywhere;
                    case "any": return (uint)everywhere;
                    case "backpack": return (uint)Player.Backpack.Serial;
                    case "self": return (uint)Player.Serial;
                    case "bank": return (uint)Player.Bank.Serial;
                    case "mount": return (uint)Player.Mount.Serial; //TODO: is this the real mount serial? in every server ?
                    case "lefthand":
                        {
                            Item i = Player.GetItemOnLayer("LeftHand");
                            if (i != null)
                                return (uint)i.Serial;
                            return 0;
                        }
                    case "righthand":
                        {
                            Item i = Player.GetItemOnLayer("RightHand");
                            if (i != null)
                                return (uint)i.Serial;
                            return 0;
                        }
                    case "lasttarget": return (uint)RazorEnhanced.Target.GetLast();
                    case "last": return (uint)RazorEnhanced.Target.GetLast();
                        //case "lastobject": return (uint)Items.LastLobject(); // TODO: Doesn't look like RE there is a way in RE to get the "last object" Serial
                }
            }
            catch (Exception e)
            { }
            return 0;
        }

        public void InitInterpreter(bool force=false) {
            if (force || m_Interpreter == null) { 
                m_Interpreter = new(this);
                RegisterCommands();
                RegisterAlias();
            }
        }

        public bool Load(string filename)
        {
            try
            {
                var root = m_Lexer.Lex(filename);
                return Load(root, Misc.SendMessage, filename);
            }
            catch (Exception e){
                Misc.SendMessage($"UOSEngine: fail to parse script:\n{e.Message}");
            }
            return false;
        }

        public bool Load(string[] textLines, Action<string> writer, string filename = "")
        {
            try
            {
                var root = m_Lexer.Lex(textLines);
                return Load(root, writer, filename);
            }
            catch (Exception e){
                Misc.SendMessage($"UOSEngine: fail to parse script:\n{e.Message}");
            }
            return false;
        }

        public bool Load(ASTNode root, Action<string> writer, string filename = "")
        {
            m_mutex.WaitOne();

            m_Loaded = false;
            m_Script = null;
            try
            {
                InitInterpreter(true);

                m_Script = new Script(root, writer, this);
                m_Script.Filename = filename;


                UpdateNamespace();
                m_Loaded = true;
            }
            catch (Exception e){
                Misc.SendMessage($"UOSEngine: fail to load script:\n{e.Message}");
            }

            m_mutex.ReleaseMutex();
            return m_Loaded;
        }
        
        public bool Execute(bool editorMode = false)
        {
            if (!m_Loaded) { return false; }

            m_mutex.WaitOne();
            try
            {
                Execute();
            }
            catch (UOSteamEngine.StopException)
            {
                if (!editorMode && m_Script.Filename != "") {
                    Misc.ScriptStop(m_Script.Filename);
                }
            }
            catch (Exception e)
            {
                if (!editorMode && m_Script.Filename != "") {
                    Misc.ScriptStop(m_Script.Filename);
                }
                throw;
            }
            finally
            {
                m_mutex.ReleaseMutex();
            }
            return true;
        }

        private void UpdateNamespace(){
            var ns = Namespace.GlobalNamespace;
            if (m_UseIsolation && m_Namespace == Namespace.GlobalNamespace)
            {
                 ns = Namespace.Get(Path.GetFileNameWithoutExtension(Script.Filename));
            }
            if (ns != m_Namespace) {
                m_Namespace = ns;
            }
        }
        
        private void Execute()
        {
            m_journal = new Journal(100);
                
            m_Interpreter.StartScript();
            try
            {
                while (m_Interpreter.ExecuteScript()) { };
            }
            catch (Exception)
            {
                m_Interpreter.StopScript();
                throw;
            }
            finally
            {
                m_journal.Active = false;
            }
        }

        public class IllegalArgumentException : Exception
        {
            public IllegalArgumentException(string msg) : base(msg) { }
        }

        public static void WrongParameterCount(string commands, int expected, int given, string message = "")
        {
            var msg = String.Format("{0} expect {1} parameters, {2} given. {3}", commands, expected, given, message);
            throw new IllegalArgumentException(msg);
        }
        
        private void RegisterCommands()
        {
            // Commands. From UOSteam Documentation
            m_Interpreter.RegisterCommandHandler("fly", FlyCommand);
            m_Interpreter.RegisterCommandHandler("land", LandCommand);
            m_Interpreter.RegisterCommandHandler("setability", SetAbility);
            m_Interpreter.RegisterCommandHandler("attack", Attack);
            m_Interpreter.RegisterCommandHandler("clearhands", ClearHands);
            m_Interpreter.RegisterCommandHandler("clickobject", ClickObject);
            m_Interpreter.RegisterCommandHandler("bandageself", BandageSelf);
            //m_Interpreter.RegisterCommandHandler("useobject", UseObject);
            m_Interpreter.RegisterCommandHandler("useonce", UseOnce);
            m_Interpreter.RegisterCommandHandler("clearusequeue", CleanUseQueue);
            m_Interpreter.RegisterCommandHandler("moveitem", MoveItem);
            m_Interpreter.RegisterCommandHandler("moveitemoffset", MoveItemOffset);
            m_Interpreter.RegisterCommandHandler("movetypeoffset", MoveTypeOffset);
            m_Interpreter.RegisterCommandHandler("walk", Walk);
            m_Interpreter.RegisterCommandHandler("turn", Turn);
            m_Interpreter.RegisterCommandHandler("pathfindto", PathFindTo);
            m_Interpreter.RegisterCommandHandler("run", Run);
            m_Interpreter.RegisterCommandHandler("useskill", UseSkill);
            m_Interpreter.RegisterCommandHandler("feed", Feed);
            m_Interpreter.RegisterCommandHandler("rename", RenamePet);
            m_Interpreter.RegisterCommandHandler("shownames", ShowNames);
            m_Interpreter.RegisterCommandHandler("togglehands", ToggleHands);
            m_Interpreter.RegisterCommandHandler("equipitem", EquipItem);
            m_Interpreter.RegisterCommandHandler("togglemounted", ToggleMounted);
            m_Interpreter.RegisterCommandHandler("equipwand", EquipWand); //TODO: This method is a stub. Remove after successful testing.
            m_Interpreter.RegisterCommandHandler("buy", Buy);
            m_Interpreter.RegisterCommandHandler("sell", Sell);
            m_Interpreter.RegisterCommandHandler("clearbuy", ClearBuy);
            m_Interpreter.RegisterCommandHandler("location", Location);
            m_Interpreter.RegisterCommandHandler("clearsell", ClearSell);
            m_Interpreter.RegisterCommandHandler("organizer", Organizer);
            m_Interpreter.RegisterCommandHandler("restock", Restock);
            m_Interpreter.RegisterCommandHandler("autoloot", Autoloot); //TODO: This method is a stub. Remove after successful testing.
            m_Interpreter.RegisterCommandHandler("autotargetobject", AutoTargetObject);
            m_Interpreter.RegisterCommandHandler("dress", Dress);
            m_Interpreter.RegisterCommandHandler("undress", Undress);
            m_Interpreter.RegisterCommandHandler("dressconfig", DressConfig); // I can't tell what this is intended to do in UOS
            m_Interpreter.RegisterCommandHandler("toggleautoloot", ToggleAutoloot);
            m_Interpreter.RegisterCommandHandler("togglescavenger", ToggleScavenger);
            m_Interpreter.RegisterCommandHandler("counter", Counter); //This has no meaning in RE
            m_Interpreter.RegisterCommandHandler("unsetalias", UnSetAlias);
            m_Interpreter.RegisterCommandHandler("setalias", SetAlias);
            m_Interpreter.RegisterCommandHandler("promptalias", PromptAlias);
            m_Interpreter.RegisterCommandHandler("waitforgump", WaitForGump);
            m_Interpreter.RegisterCommandHandler("replygump", ReplyGump);
            m_Interpreter.RegisterCommandHandler("closegump", CloseGump); // kind of done, RE can't do paperdolls ...
            m_Interpreter.RegisterCommandHandler("clearjournal", ClearJournal);
            m_Interpreter.RegisterCommandHandler("waitforjournal", WaitForJournal);
            m_Interpreter.RegisterCommandHandler("poplist", PopList);
            m_Interpreter.RegisterCommandHandler("pushlist", PushList);
            m_Interpreter.RegisterCommandHandler("removelist", RemoveList);
            m_Interpreter.RegisterCommandHandler("createlist", CreateList);
            m_Interpreter.RegisterCommandHandler("clearlist", ClearList);
            m_Interpreter.RegisterCommandHandler("info", Info);
            m_Interpreter.RegisterCommandHandler("pause", Pause);
            m_Interpreter.RegisterCommandHandler("ping", Ping);
            m_Interpreter.RegisterCommandHandler("playmacro", PlayMacro);
            m_Interpreter.RegisterCommandHandler("playsound", PlaySound);
            m_Interpreter.RegisterCommandHandler("resync", Resync);
            m_Interpreter.RegisterCommandHandler("snapshot", Snapshot);
            m_Interpreter.RegisterCommandHandler("hotkeys", Hotkeys); //toggles hot keys .. not going to implement
            m_Interpreter.RegisterCommandHandler("where", Where);
            m_Interpreter.RegisterCommandHandler("messagebox", MessageBox);
            m_Interpreter.RegisterCommandHandler("mapuo", MapUO); // not going to implement
            m_Interpreter.RegisterCommandHandler("clickscreen", ClickScreen);
            m_Interpreter.RegisterCommandHandler("paperdoll", Paperdoll);
            m_Interpreter.RegisterCommandHandler("helpbutton", HelpButton); //not going to implement
            m_Interpreter.RegisterCommandHandler("guildbutton", GuildButton);
            m_Interpreter.RegisterCommandHandler("questsbutton", QuestsButton);
            m_Interpreter.RegisterCommandHandler("logoutbutton", LogoutButton);
            m_Interpreter.RegisterCommandHandler("virtue", Virtue);
            m_Interpreter.RegisterCommandHandler("msg", MsgCommand);
            m_Interpreter.RegisterCommandHandler("playmacro", PlayMacro);
            m_Interpreter.RegisterCommandHandler("headmsg", HeadMsg);
            m_Interpreter.RegisterCommandHandler("partymsg", PartyMsg);
            m_Interpreter.RegisterCommandHandler("guildmsg", GuildMsg);
            m_Interpreter.RegisterCommandHandler("allymsg", AllyMsg);
            m_Interpreter.RegisterCommandHandler("whispermsg", WhisperMsg);
            m_Interpreter.RegisterCommandHandler("yellmsg", YellMsg);
            m_Interpreter.RegisterCommandHandler("sysmsg", SysMsg);
            m_Interpreter.RegisterCommandHandler("chatmsg", ChatMsg);
            m_Interpreter.RegisterCommandHandler("emotemsg", EmoteMsg);
            m_Interpreter.RegisterCommandHandler("promptmsg", PromptMsg);
            m_Interpreter.RegisterCommandHandler("timermsg", TimerMsg);
            m_Interpreter.RegisterCommandHandler("waitforprompt", WaitForPrompt);
            m_Interpreter.RegisterCommandHandler("cancelprompt", CancelPrompt);
            m_Interpreter.RegisterCommandHandler("addfriend", AddFriend); //not so much
            m_Interpreter.RegisterCommandHandler("removefriend", RemoveFriend); // not implemented, use the gui
            m_Interpreter.RegisterCommandHandler("contextmenu", ContextMenu);
            m_Interpreter.RegisterCommandHandler("waitforcontext", WaitForContext);
            m_Interpreter.RegisterCommandHandler("ignoreobject", IgnoreObject);
            m_Interpreter.RegisterCommandHandler("clearignorelist", ClearIgnoreList);
            m_Interpreter.RegisterCommandHandler("setskill", SetSkill);
            m_Interpreter.RegisterCommandHandler("waitforproperties", WaitForProperties);
            m_Interpreter.RegisterCommandHandler("autocolorpick", AutoColorPick);
            m_Interpreter.RegisterCommandHandler("waitforcontents", WaitForContents);
            m_Interpreter.RegisterCommandHandler("miniheal", MiniHeal);
            m_Interpreter.RegisterCommandHandler("bigheal", BigHeal);
            m_Interpreter.RegisterCommandHandler("cast", Cast);
            m_Interpreter.RegisterCommandHandler("chivalryheal", ChivalryHeal);
            m_Interpreter.RegisterCommandHandler("waitfortarget", WaitForTarget);
            m_Interpreter.RegisterCommandHandler("canceltarget", CancelTarget);
            m_Interpreter.RegisterCommandHandler("cancelautotarget", CancelAutoTarget);
            m_Interpreter.RegisterCommandHandler("target", Target);
            m_Interpreter.RegisterCommandHandler("targettype", TargetType);
            m_Interpreter.RegisterCommandHandler("targetground", TargetGround);
            m_Interpreter.RegisterCommandHandler("targettile", TargetTile);
            m_Interpreter.RegisterCommandHandler("targettileoffset", TargetTileOffset);
            m_Interpreter.RegisterCommandHandler("targettilerelative", TargetTileRelative);
            m_Interpreter.RegisterCommandHandler("targetresource", TargetResource);
            m_Interpreter.RegisterCommandHandler("cleartargetqueue", ClearTargetQueue);
            m_Interpreter.RegisterCommandHandler("warmode", WarMode);
            m_Interpreter.RegisterCommandHandler("settimer", SetTimer);
            m_Interpreter.RegisterCommandHandler("removetimer", RemoveTimer);
            m_Interpreter.RegisterCommandHandler("createtimer", CreateTimer);
            m_Interpreter.RegisterCommandHandler("getenemy", GetEnemy); //TODO: add "transformations" list
            m_Interpreter.RegisterCommandHandler("getfriend", GetFriend); //TODO: add "transformations" list
            m_Interpreter.RegisterCommandHandler("namespace", ManageNamespaces); //TODO: add "transformations" list

            // Expressions
            m_Interpreter.RegisterExpressionHandler("usetype", UseType);
            m_Interpreter.RegisterExpressionHandler("movetype", MoveType);

            m_Interpreter.RegisterExpressionHandler("findalias", FindAlias);
            m_Interpreter.RegisterExpressionHandler("x", LocationX);
            m_Interpreter.RegisterExpressionHandler("y", LocationY);
            m_Interpreter.RegisterExpressionHandler("z", LocationZ);
            m_Interpreter.RegisterExpressionHandler("organizing", Organizing);
            m_Interpreter.RegisterExpressionHandler("restock", Restocking);

            m_Interpreter.RegisterExpressionHandler("contents", CountContents);
            m_Interpreter.RegisterExpressionHandler("inregion", InRegion);
            m_Interpreter.RegisterExpressionHandler("skill", Skill);
            m_Interpreter.RegisterExpressionHandler("findobject", FindObject);
            m_Interpreter.RegisterExpressionHandler("useobject", UseObjExp);
            m_Interpreter.RegisterExpressionHandler("distance", Distance);
            m_Interpreter.RegisterExpressionHandler("graphic", Graphic);
            m_Interpreter.RegisterExpressionHandler("inrange", InRange);
            m_Interpreter.RegisterExpressionHandler("buffexists", BuffExists);
            m_Interpreter.RegisterExpressionHandler("property", Property);
            m_Interpreter.RegisterExpressionHandler("findtype", FindType);
            m_Interpreter.RegisterExpressionHandler("findlayer", FindLayer);
            m_Interpreter.RegisterExpressionHandler("skillstate", SkillState);
            m_Interpreter.RegisterExpressionHandler("counttype", CountType);
            m_Interpreter.RegisterExpressionHandler("counttypeground", CountTypeGround);
            m_Interpreter.RegisterExpressionHandler("findwand", FindWand); //TODO: This method is a stub. Remove after successful testing.
            m_Interpreter.RegisterExpressionHandler("inparty", InParty); //TODO: This method is a stub. Remove after successful testing.
            m_Interpreter.RegisterExpressionHandler("infriendlist", InFriendList);
            m_Interpreter.RegisterExpressionHandler("ingump", InGump);
            m_Interpreter.RegisterExpressionHandler("gumpexists", GumpExists);
            m_Interpreter.RegisterExpressionHandler("injournal", InJournal);
            m_Interpreter.RegisterExpressionHandler("listexists", ListExists);
            m_Interpreter.RegisterExpressionHandler("list", ListCount);
            m_Interpreter.RegisterExpressionHandler("inlist", InList);
            m_Interpreter.RegisterExpressionHandler("timer", Timer);
            m_Interpreter.RegisterExpressionHandler("timerexists", TimerExists);
            m_Interpreter.RegisterExpressionHandler("targetexists", TargetExists);



            // Player Attributes
            m_Interpreter.RegisterExpressionHandler("weight", (string expression, Argument[] args, bool quiet) => Player.Weight);
            m_Interpreter.RegisterExpressionHandler("maxweight", (string expression, Argument[] args, bool quiet) => Player.MaxWeight);
            m_Interpreter.RegisterExpressionHandler("diffweight", (string expression, Argument[] args, bool quiet) => Player.MaxWeight - Player.Weight);
            m_Interpreter.RegisterExpressionHandler("mana", (string expression, Argument[] args, bool quiet) => Player.Mana);
            m_Interpreter.RegisterExpressionHandler("maxmana", (string expression, Argument[] args, bool quiet) => Player.ManaMax);
            m_Interpreter.RegisterExpressionHandler("stam", (string expression, Argument[] args, bool quiet) => Player.Stam);
            m_Interpreter.RegisterExpressionHandler("maxstam", (string expression, Argument[] args, bool quiet) => Player.StamMax);
            m_Interpreter.RegisterExpressionHandler("dex", (string expression, Argument[] args, bool quiet) => Player.Dex);
            m_Interpreter.RegisterExpressionHandler("int", (string expression, Argument[] args, bool quiet) => Player.Int);
            m_Interpreter.RegisterExpressionHandler("str", (string expression, Argument[] args, bool quiet) => Player.Str);
            m_Interpreter.RegisterExpressionHandler("physical", (string expression, Argument[] args, bool quiet) => Player.AR);
            m_Interpreter.RegisterExpressionHandler("fire", (string expression, Argument[] args, bool quiet) => Player.FireResistance);
            m_Interpreter.RegisterExpressionHandler("cold", (string expression, Argument[] args, bool quiet) => Player.ColdResistance);
            m_Interpreter.RegisterExpressionHandler("poison", (string expression, Argument[] args, bool quiet) => Player.PoisonResistance);
            m_Interpreter.RegisterExpressionHandler("energy", (string expression, Argument[] args, bool quiet) => Player.EnergyResistance);

            m_Interpreter.RegisterExpressionHandler("followers", (string expression, Argument[] args, bool quiet) => Player.Followers);
            m_Interpreter.RegisterExpressionHandler("maxfollowers", (string expression, Argument[] args, bool quiet) => Player.FollowersMax);
            m_Interpreter.RegisterExpressionHandler("gold", (string expression, Argument[] args, bool quiet) => Player.Gold);
            m_Interpreter.RegisterExpressionHandler("hidden", (string expression, Argument[] args, bool quiet) => !Player.Visible);
            m_Interpreter.RegisterExpressionHandler("luck", (string expression, Argument[] args, bool quiet) => Player.Luck);
            m_Interpreter.RegisterExpressionHandler("waitingfortarget", WaitingForTarget); //TODO: loose approximation, see inside

            m_Interpreter.RegisterExpressionHandler("hits", Hits);
            m_Interpreter.RegisterExpressionHandler("diffhits", DiffHits);
            m_Interpreter.RegisterExpressionHandler("maxhits", MaxHits);

            m_Interpreter.RegisterExpressionHandler("name", Name);
            m_Interpreter.RegisterExpressionHandler("dead", IsDead);
            m_Interpreter.RegisterExpressionHandler("direction", Direction); //Dalamar: Fix original UOS direction, in numbers
            m_Interpreter.RegisterExpressionHandler("directionname", DirectionName);  //Dalamar: Added RE style directions with names
            m_Interpreter.RegisterExpressionHandler("flying", IsFlying);
            m_Interpreter.RegisterExpressionHandler("paralyzed", IsParalyzed);
            m_Interpreter.RegisterExpressionHandler("poisoned", IsPoisoned);
            m_Interpreter.RegisterExpressionHandler("mounted", IsMounted);
            m_Interpreter.RegisterExpressionHandler("yellowhits", YellowHits);
            m_Interpreter.RegisterExpressionHandler("war", InWarMode);
            m_Interpreter.RegisterExpressionHandler("criminal", IsCriminal);
            m_Interpreter.RegisterExpressionHandler("enemy", IsEnemy);
            m_Interpreter.RegisterExpressionHandler("friend", IsFriend);
            m_Interpreter.RegisterExpressionHandler("gray", IsGray);
            m_Interpreter.RegisterExpressionHandler("innocent", IsInnocent);
            m_Interpreter.RegisterExpressionHandler("murderer", IsMurderer);

            m_Interpreter.RegisterExpressionHandler("bandage", Bandage);


            // Object attributes
        }

        #region Dummy and Placeholders

        private static IComparable ExpressionNotImplemented(string expression, Argument[] args, bool _)
        {
            Console.WriteLine("Expression Not Implemented {0} {1}", expression, args);
            return 0;
        }

        private static bool NotImplemented(string command, Argument[] args, bool quiet, bool force)
        {
            Console.WriteLine("UOS: NotImplemented {0} {1}", command, args);
            return true;
        }


        #endregion

        #region Expressions


        /// <summary>
        /// if contents (serial) ('operator') ('value')
        /// </summary>
        private static IComparable CountContents(string expression, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                Item container = Items.FindBySerial((int)serial);
                if (container != null)
                {
                    if (!container.IsContainer)
                        return 0;
                    List<Item> list = container.Contains;
                    return list.Count;
                }
                return 0;
            }

            return false;
        }

        /// <summary>
        /// counttype (graphic) (color) (source) (operator) (value)
        /// </summary>
        private static IComparable CountType(string expression, Argument[] args, bool quiet)
        {

            if (args.Length == 3)
            {
                int graphic = args[0].AsInt();
                int color = args[1].AsInt();
                uint container = args[2].AsSerial();
                int count = Items.ContainerCount((int)container, graphic, color, true);
                return count;
            }

            return false;
        }

        /// <summary>
        /// if injournal ('text') ['author'/'system']
        /// </summary>
        private IComparable InJournal(string expression, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                string text = args[0].AsString();
                return m_journal.Search(text);
            }
            if (args.Length == 2)
            {
                string text = args[0].AsString();
                string texttype = args[1].AsString();
                texttype = texttype.Substring(0, 1).ToUpper() + texttype.Substring(1).ToLower();  // syStEm -> System
                return m_journal.SearchByType(text, texttype);
            }


            return false;
        }

        /// <summary>
        /// if listexists ('list name')
        /// </summary>
        private IComparable ListExists(string expression, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                string list = args[0].AsString();
                return m_Interpreter.ListExists(list);
            }

            return false;
        }

        /// <summary>
        /// useobject (serial)
        /// </summary>
        private static IComparable UseObjExp(string expression, Argument[] args, bool quiet)
        {
            UseObject(expression, args, quiet, false);
            return true;
        }

        /// <summary>
        /// findalias ('alias name')
        /// </summary>
        private IComparable FindAlias(string expression, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                string alias = args[0].AsString();
                return m_Interpreter.FindAlias(alias);
            }

            return false;
        }

        /// <summary>
        /// x (serial)
        /// </summary>
        private static IComparable LocationX(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "X location requires a serial");
                // return 0;
            }

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.X;
                }
            }

            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.X;
                }
            }

            throw new RunTimeError(null, "X location serial not found");
            // return 0;
        }

        /// <summary>
        /// y (serial)
        /// </summary>
        private static IComparable LocationY(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "Y location requires a serial");
                // return 0;
            }

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);
            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.Y;
                }
            }

            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.Y;
                }
            }

            throw new RunTimeError(null, "Y location serial not found");
            // return 0;
        }

        /// <summary>
        /// z (serial)
        /// </summary>
        private static IComparable LocationZ(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "Z location requires a serial");
                // return 0;
            }

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);
            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.Z;
                }
            }

            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item != null)
                {
                    return item.Position.Z;
                }
            }

            throw new RunTimeError(null, "Z location serial not found");
            // return 0;
        }


        /// <summary>
        /// if not organizing
        /// </summary>
        private static IComparable Organizing(string expression, Argument[] args, bool quiet)
        {
            return RazorEnhanced.Organizer.Status();
        }

        /// <summary>
        /// if not restock
        /// </summary>
        private static IComparable Restocking(string expression, Argument[] args, bool quiet)
        {
            return RazorEnhanced.Restock.Status();
        }



        /// The problem is UOS findbyid will find either mobil or item, but RE seperates them
        /// So this function will look for both and return the list
        ///   if it is an item it can't be a mobile and vica-versa
        private static List<int> FindByType_ground(int graphic, int color, int amount, int range)
        {
            List<int> retList = new List<int>();
            // Search for items first
            Items.Filter itemFilter = new Items.Filter
            {
                Enabled = true
            };
            itemFilter.Graphics.Add(graphic);
            itemFilter.RangeMax = range;
            itemFilter.OnGround = 1;
            itemFilter.CheckIgnoreObject = true;
            if (color != -1)
                itemFilter.Hues.Add(color);
            List<Item> items = RazorEnhanced.Items.ApplyFilter(itemFilter);

            if (items.Count > 0)
            {
                foreach (var i in items)
                {
                    if ((amount <= 0) || (i.Amount >= amount))
                        retList.Add(i.Serial);
                }
                //return retList;
            }

            Mobiles.Filter mobileFilter = new Mobiles.Filter
            {
                Enabled = true
            };
            mobileFilter.Bodies.Add(graphic);
            mobileFilter.RangeMax = range;
            mobileFilter.CheckIgnoreObject = true;
            if (color != -1)
                mobileFilter.Hues.Add(color);
            List<Mobile> mobiles = RazorEnhanced.Mobiles.ApplyFilter(mobileFilter);

            if (mobiles.Count > 0)
            {
                foreach (var m in mobiles)
                {
                    retList.Add(m.Serial);
                }
                //return retList;
            }

            return retList;
        }

        internal static int CountType_ground(int graphic, int color, int range)
        {
            int retCount = 0;

            // Search for items first
            Items.Filter itemFilter = new Items.Filter
            {
                Enabled = true
            };
            itemFilter.Graphics.Add(graphic);
            itemFilter.RangeMax = range;
            itemFilter.OnGround = 1;
            if (color != -1)
                itemFilter.Hues.Add(color);
            List<Item> items = RazorEnhanced.Items.ApplyFilter(itemFilter);

            if (items.Count > 0)
            {
                foreach (var i in items)
                {
                    retCount += i.Amount;
                }
            }

            Mobiles.Filter mobileFilter = new Mobiles.Filter
            {
                Enabled = true
            };
            mobileFilter.Bodies.Add(graphic);
            mobileFilter.RangeMax = range;
            if (color != -1)
                mobileFilter.Hues.Add(color);
            List<Mobile> mobiles = RazorEnhanced.Mobiles.ApplyFilter(mobileFilter);

            if (mobiles.Count > 0)
            {
                foreach (var m in mobiles)
                {
                    retCount += 1;
                }
            }

            return retCount;
        }

        /// <summary>
        /// findtype (graphic) [color] [source] [amount] [range or search level]
        /// </summary>
        private IComparable FindType(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "FindType requires parameters");
                // return false;
            }

            string listname = args[0].AsString();
            if (m_Interpreter.ListExists(listname))
            {
                foreach (Argument arg in m_Interpreter.ListContents(listname))
                {
                    int type = arg.AsInt();
                    if (FindByType(type, args))
                        return true;
                }
            }
            else
            {
                int type = args[0].AsInt();
                return FindByType(type, args);
            }
            return false;
        }

        internal bool FindByType(int type, Argument[] args)
        {
            int serial = -1;
            if (args.Length == 1 || args.Length == 2)
            {
                int color = -1;
                if (args.Length == 2)
                    color = args[1].AsInt();

                List<int> results = FindByType_ground(type, color, -1, -1);
                if (results.Count > 0)
                {
                    serial = results[0];
                }
                else
                {
                    Item item = Items.FindByID(type, color, -1, true);
                    if (item != null)
                    {
                        serial = item.Serial;
                    }
                }
            }
            if (args.Length >= 3)
            {
                m_Interpreter.UnSetAlias("found");
                int color = args[1].AsInt();
                string groundCheck = args[2].AsString().ToLower();
                uint source = args[2].AsSerial();
                int amount = -1;
                if (args.Length >= 4)
                    args[3].AsInt();
                int range = -1;
                if (args.Length == 5)
                    range = args[4].AsInt();
                if (groundCheck == "ground")
                {
                    List<int> results = FindByType_ground(type, color, amount, range);
                    if (results.Count > 0)
                    {
                        serial = results[0];
                    }
                }
                else
                {
                    Item item = Items.FindByID(type, color, (int)source, range);
                    if (item != null)
                    {
                        if (amount != -1 && item.Amount < amount)
                        {
                            item = null;
                        }
                        if (item != null)
                        {
                            serial = item.Serial;
                        }
                    }
                }
            }

            if (serial != -1)
            {
                m_Interpreter.SetAlias("found", (uint)serial);
                return true;
            }

            return false;

        }

        /// <summary>
        /// property ('name') (serial) [operator] [value]
        /// </summary>
        private static IComparable Property(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 2)
            {
                throw new RunTimeError(null, "Property requires 2 parameters");
                // return false;
            }

            string findProp = args[0].AsString();
            uint serial = args[1].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    List<String> props = Items.GetPropStringList((int)serial);
                    foreach (String prop in props)
                    {
                        if (0 == String.Compare(findProp, prop, true))
                        {
                            return true;
                        }
                    }
                }
            }

            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item != null)
                {
                    foreach (var prop in item.Properties)
                    {
                        if (0 == String.Compare(findProp, prop.ToString(), true))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// ingump (gump id/'any') ('text')
        /// </summary>
        private static IComparable InGump(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 2)
            {
                uint gumpid = args[0].AsSerial();
                string serach_text = args[1].AsString().ToLower();
                uint curGumpid = Gumps.CurrentGump();

                if (gumpid == 0xffffffff || gumpid == curGumpid)
                {
                    var gump_text = String.Join("\n", Gumps.LastGumpGetLineList()).ToLower();
                    return gump_text.Contains(serach_text);

                }

            }
            return false;
        }

        /// <summary>
        /// war (serial)
        /// </summary>
        private static IComparable InWarMode(string expression, Argument[] args, bool quiet)
        {

            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.WarMode;
                }
            }
            return false;
        }

        /// <summary>
        /// poisoned [serial]
        /// </summary>
        private static IComparable IsPoisoned(string expression, Argument[] args, bool quiet)
        {

            if (args.Length == 0)
            {
                return Player.Poisoned;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.Poisoned;
                }
            }

            return false;
        }

        /// <summary>
        /// name [serial]
        /// </summary>
        private static IComparable Name(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Name;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.Name;
                }
            }

            return false;
        }
        
        /// <summary>
        /// dead [serial]
        /// </summary>
        private static IComparable IsDead(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.IsGhost;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.IsGhost;
                }
            }

            return false;
        }

        /// <summary>
        /// directionname [serial]
        /// </summary>
        private static IComparable DirectionName(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Direction;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.Direction;
                }
            }

            return false;
        }

        /// <summary>
        /// direction [serial]
        /// </summary>
        private static IComparable Direction(string expression, Argument[] args, bool quiet)
        {
            //UOS Direction -  Start in top-right-corner: 0 | North. Inclements: clockwise
            Dictionary<string, int> dir_num = new Dictionary<string, int>() {
                {"North",0}, {"Right",1}, {"East",2}, {"Down",3},
                {"South",4}, {"Left",5},  {"West",6}, {"Up",7},


            };

            string direction = null;
            if (args.Length == 0)
            {
                direction = Player.Direction;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    direction = theMobile.Direction;
                }
            }

            if (dir_num.ContainsKey(direction))
            {
                return dir_num[Player.Direction];
            }

            return false;
        }

        /// <summary>
        /// flying [serial]
        /// </summary>
        private static IComparable IsFlying(string expression, Argument[] args, bool quiet)
        {
            uint serial = (uint)Player.Serial;
            if (args.Length >= 1)
            {
                serial = args[0].AsSerial();
            }
            Mobile theMobile = Mobiles.FindBySerial((int)serial);
            if (theMobile != null)
            {
                return theMobile.Flying;
            }
            return false;
        }

        /// <summary>
        /// paralyzed [serial]
        /// </summary>
        private static IComparable IsParalyzed(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Paralized;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Paralized;
            }
            return false;
        }

        /// <summary>
        /// mounted [serial]
        /// </summary>
        private static IComparable IsMounted(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Mount != null;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Mount != null;
            }
            return false;
        }

        /// <summary>
        /// yellowhits [serial]
        /// </summary>
        private static IComparable YellowHits(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.YellowHits;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.YellowHits;
            }
            return false;
        }
        /*
         // hue color #30
            0x000000, // black      unused 0
            0x30d0e0, // blue       0x0059 1
            0x60e000, // green      0x003F 2
            0x9090b2, // greyish    0x03b2 3
            0x909090, // grey          "   4
            0xd88038, // orange     0x0090 5
            0xb01000, // red        0x0022 6
            0xe0e000, // yellow     0x0035 7
        */

        /// <summary>
        /// criminal [serial]
        /// </summary>
        private static IComparable IsCriminal(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Notoriety == 4;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Notoriety == 4;
            }
            return false;
        }

        /// <summary>
        /// murderer [serial]
        /// </summary>
        private static IComparable IsMurderer(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Notoriety == 6;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Notoriety == 6;
            }
            return false;
        }

        /// <summary>
        /// enemy serial
        /// </summary>
        private static IComparable IsEnemy(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "enemy requires parameters");
                // return false;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.IsHuman && (theMobile.Notoriety >= 4 && theMobile.Notoriety <= 6);
            }
            return false;
        }

        /// <summary>
        /// friend serial
        /// </summary>
        private static IComparable IsFriend(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "friend requires parameters");
            }

            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.IsHuman && theMobile.Notoriety < 4;
            }
            return false;
        }

        /// <summary>
        /// gray serial
        /// </summary>
        private static IComparable IsGray(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Notoriety == 3;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Notoriety == 3;
            }
            return false;
        }

        /// <summary>
        /// innocent serial
        /// </summary>
        private static IComparable IsInnocent(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Notoriety == 2;
            }
            Mobile theMobile = Mobiles.FindBySerial((int)args[0].AsSerial());
            if (theMobile != null)
            {
                return theMobile.Notoriety == 2;
            }
            return false;
        }

        /// <summary>
        /// bandage
        /// </summary>
        private static IComparable Bandage(string expression, Argument[] args, bool quiet)
        {
            int count = Items.ContainerCount((int)Player.Backpack.Serial, 0x0E21, -1, true);
            if (count > 0 && (Player.Hits < Player.HitsMax || Player.Poisoned))
                BandageHeal.Heal(Assistant.World.Player, false);
            return count;
        }

        /// <summary>
        /// gumpexists (gump id/'any')
        /// </summary>
        private static IComparable GumpExists(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Gumps.HasGump();
            }

            if (args.Length == 1)
            {
                uint gumpid = args[0].AsUInt();
                return Gumps.HasGump(gumpid);
            }
            return -1;
        }

        /// <summary>
        /// list ('list name') (operator) (value)
        /// </summary>
        private IComparable ListCount(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 1)
            {
                string listName = args[0].AsString();
                return m_Interpreter.ListLength(listName);
            }

            WrongParameterCount("list", 1, args.Length, "list command requires 1 parameter, the list name");
            return 0;
        }

        /// <summary>
        /// inlist ('list name') ('element value')
        /// </summary>
        private IComparable InList(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 1)
            {
                string listName = args[0].AsString();
                return m_Interpreter.ListContains(listName, args[1]);  // This doesn't seem right
            }
            return 0;
        }

        /// <summary>
        /// skillstate ('skill name') (operator) ('locked'/'up'/'down')
        /// </summary>
        private static IComparable SkillState(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 1)
            {
                string skill = args[0].AsString();
                int value = Player.GetSkillStatus(skill);
                switch (value)
                {
                    case 0:
                        return "up";
                    case 1:
                        return "down";
                    case 2:
                        return "locked";
                }
            }
            return "unknown";
        }

        /// <summary>
        /// inregion ('guards'/'town'/'dungeon'/'forest') [serial] [range]
        /// </summary>
        private static IComparable InRegion(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "inregion requires parameters");
                // return false;
            }

            string desiredRegion = args[0].AsString();
            string region = Player.Zone();
            if (args.Length == 1)
            {
                return desiredRegion.ToLower() == region.ToLower();
            }
            if (args.Length == 3)
            {
                uint serial = args[1].AsSerial();
                Mobile mobile = Mobiles.FindBySerial((int)serial);
                if (mobile == null)
                    return false;
                int range = args[2].AsInt();
                ConfigFiles.RegionByArea.Area area = Player.Area(Player.Map, mobile.Position.X, mobile.Position.Y);
                if (area == null)
                    return false;
                if (desiredRegion.ToLower() != area.zoneName.ToLower())
                    return false;

                foreach (System.Drawing.Rectangle rect in area.rect)
                {
                    if (rect.Contains(mobile.Position.X, mobile.Position.Y))
                    {
                        System.Drawing.Rectangle desiredRect = new System.Drawing.Rectangle(rect.X - range, rect.Y - range, rect.Width - range, rect.Height - range);
                        if (desiredRect.Contains(mobile.Position.X, mobile.Position.Y))
                            return true;
                    }
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// findwand NOT IMPLEMENTED
        /// </summary>
        private static IComparable FindWand(string expression, Argument[] args, bool quiet)
        {
            return ExpressionNotImplemented(expression, args, quiet);
        }

        /// <summary>
        /// inparty NOT IMPLEMENTED
        /// </summary>
        private static IComparable InParty(string expression, Argument[] args, bool quiet)
        {
            return ExpressionNotImplemented(expression, args, quiet);
        }

        /// <summary>
        /// skill ('name') (operator) (value)
        /// </summary>
        private static IComparable Skill(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "Skill requires parameters");
                // return false;
            }

            string skillname = args[0].AsString();
            double skillvalue = Player.GetRealSkillValue(skillname);
            return skillvalue;
        }

        /// <summary>
        /// findobject (serial) [color] [source] [amount] [range]
        /// </summary>
        private IComparable FindObject(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "Find Object requires parameters");
                // return false;
            }
            m_Interpreter.UnSetAlias("found");
            int color = -1;
            int amount = -1;
            int container = -1;
            int range = -1;

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            if (args.Length >= 2)
            {
                color = args[1].AsInt();
            }
            if (args.Length >= 3)
            {
                container = (int)args[2].AsSerial();
            }
            if (args.Length >= 4)
            {
                amount = args[3].AsInt();
            }
            if (args.Length >= 5)
            {
                range = args[4].AsInt();
            }

            if (thing.IsMobile)
            {
                Mobile mobile = Mobiles.FindBySerial((int)serial);
                if (mobile != null)
                {
                    if (color == -1 || color == mobile.Hue)
                    {
                        m_Interpreter.SetAlias("found", (uint)mobile.Serial);
                        return true;
                    }
                }
                return false;
            }

            // must be an item
            Item item = Items.FindBySerial((int)serial);
            if (item == null)
                return false;

            // written this way because I hate if ! && !
            if (color != -1)
                if (item.Hue != color)
                    return false;

            if (container != -1)
                if (item.Container != container)
                    return false;

            if (amount != -1)
                if (item.Amount < amount)
                    return false;

            if (range != -1)
                if (Assistant.Utility.Distance(Assistant.World.Player.Position.X, Assistant.World.Player.Position.Y, item.Position.X, item.Position.Y) > range)
                    return false;
            m_Interpreter.SetAlias("found", (uint)item.Serial);
            return true;
        }

        /// <summary>
        /// graphic (serial) (operator) (value)
        /// </summary>
        private static IComparable Graphic(string expression, Argument[] args, bool quiet)
        {
            if (args.Length > 1)
            {
                throw new RunTimeError(null, "graphic Object requires 0 or 1 parameters");
            }

            if (args.Length == 0)
                return Player.MobileID;

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);
            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item == null)
                    return Int32.MaxValue;
                return item.ItemID;
            }
            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item == null)
                    return Int32.MaxValue;
                return item.MobileID;
            }

            return Int32.MaxValue;
        }


        /// <summary>
        /// distance (serial) (operator) (value)
        /// </summary>
        private static IComparable Distance(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "Distance Object requires parameters");
                // return Int32.MaxValue;
            }

            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            int x = -1;
            int y = -1;
            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item == null)
                    return Int32.MaxValue;
                x = item.Position.X;
                y = item.Position.Y;
            }
            if (thing.IsMobile)
            {
                Mobile item = Mobiles.FindBySerial((int)serial);
                if (item == null)
                    return Int32.MaxValue;
                x = item.Position.X;
                y = item.Position.Y;
            }

            if (x == -1 || y == -1)
                return Int32.MaxValue;

            return Assistant.Utility.Distance(Assistant.World.Player.Position.X, Assistant.World.Player.Position.Y, x, y);
        }

        /// <summary>
        /// inrange (serial) (range)
        /// </summary>
        private static IComparable InRange(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 2)
            {
                throw new RunTimeError(null, "Find Object requires parameters");
                // return false;
            }
            uint serial = args[0].AsSerial();
            Assistant.Serial thing = new Assistant.Serial(serial);

            int range = args[1].AsInt();
            int x = -1;
            int y = -1;

            if (thing.IsMobile)
            {
                Mobile mobile = Mobiles.FindBySerial((int)serial);
                if (mobile != null)
                {
                    x = mobile.Position.X;
                    y = mobile.Position.Y;
                }
            }
            if (thing.IsItem)
            {
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    x = item.Position.X;
                    y = item.Position.Y;
                }
            }

            if (x == -1 || y == -1)
                return false;

            int distance = Assistant.Utility.Distance(Assistant.World.Player.Position.X, Assistant.World.Player.Position.Y, x, y);
            return (distance <= range);
        }

        /// <summary>
        /// buffexists ('buff name')
        /// </summary>
        private static IComparable BuffExists(string expression, Argument[] args, bool quiet)
        {
            if (args.Length >= 1)
            {
                return Player.BuffsExist(args[0].AsString());
            }

            return false;

        }

        /// <summary>
        /// findlayer (serial) (layer)
        /// </summary>
        private IComparable FindLayer(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 2)
            {
                throw new RunTimeError(null, "Find Object requires parameters");
                // return false;
            }
            uint serial = args[0].AsSerial();
            Assistant.Mobile mobile = Assistant.World.FindMobile((Assistant.Serial)((uint)serial));
            if (mobile == null)
                return false;

            Assistant.Layer layer = (Assistant.Layer)args[1].AsInt();
            Assistant.Item item = mobile.GetItemOnLayer(layer);
            if (item != null)
            {
                m_Interpreter.SetAlias("found", (uint)item.Serial);
                return true;
            }
            return false;
        }

        /// <summary>
        /// counttypeground (graphic) (color) (range) (operator) (value)
        /// </summary>
        private static IComparable CountTypeGround(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1)
            {
                throw new RunTimeError(null, "CountTypeGround requires parameters");
                // return 0;
            }

            int graphic = args[0].AsInt();
            int color = -1;
            int range = -1;
            //
            if (args.Length > 1)
            {
                color = args[1].AsInt();
            }
            if (args.Length > 2)
            {
                range = args[2].AsInt();
            }

            int count = CountType_ground(graphic, color, range);

            return count;
        }

        /// <summary>
        /// infriendlist (serial)
        /// </summary>
        private static IComparable InFriendList(string expression, Argument[] args, bool quiet)
        {
            if (args.Length > 0)
            {
                uint serial = args[0].AsSerial();
                Friend.FriendPlayer player = new Friend.FriendPlayer("FAKE", (int)serial, true);
                string selection = Friend.FriendListName;
                if (RazorEnhanced.Settings.Friend.ListExists(selection))
                {
                    if (RazorEnhanced.Settings.Friend.PlayerExists(selection, player))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        ///  timer ('timer name') (operator) (value)
        /// </summary>
        private IComparable Timer(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1) { WrongParameterCount(expression, 1, args.Length); }

            return m_Interpreter.GetTimer(args[0].AsString()).TotalMilliseconds;

        }

        /// <summary>
        ///  timerexists ('timer name')
        /// </summary>
        private IComparable TimerExists(string expression, Argument[] args, bool quiet)
        {
            if (args.Length < 1) { WrongParameterCount(expression, 1, args.Length); }
            return m_Interpreter.TimerExists(args[0].AsString());
        }

        /// <summary>
        ///  targetexists ('Any' | 'Harmful' | 'Neutral' | 'Beneficial')
        /// </summary>
        private static IComparable TargetExists(string expression, Argument[] args, bool quiet)
        {
            if (args.Length >= 1)
            {
                CultureInfo cultureInfo = Thread.CurrentThread.CurrentCulture;
                TextInfo textInfo = cultureInfo.TextInfo;
                string targetFlag = textInfo.ToTitleCase(args[0].AsString().ToLower());
                return RazorEnhanced.Target.HasTarget(targetFlag);
            }
            return RazorEnhanced.Target.HasTarget();
        }

        /// <summary>
        ///  waitingfortarget POORLY IMPLEMENTED
        /// </summary>
        private static IComparable WaitingForTarget(string expression, Argument[] args, bool quiet)
        {
            //TODO: This is an very loose approximation. Waitingfortarget should know if there is any "pending target" coming from the server.
            //UOS Tester: Lermster#2355
            return RazorEnhanced.Target.HasTarget();
        }

        /// <summary>
        /// hits [serial]
        /// </summary>
        private static IComparable Hits(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.Hits;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.Hits;
                }
            }

            return false;

            // return Player.Hits;
        }

        /// <summary>
        /// diffhits [serial]
        /// </summary>
        private static IComparable DiffHits(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.HitsMax - Player.Hits;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.HitsMax - theMobile.Hits;
                }
            }

            return false;
        }

        /// <summary>
        /// maxhits [serial]
        /// </summary>
        private static IComparable MaxHits(string expression, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                return Player.HitsMax;
            }
            else if (args.Length >= 1)
            {
                uint serial = args[0].AsSerial();
                Mobile theMobile = Mobiles.FindBySerial((int)serial);
                if (theMobile != null)
                {
                    return theMobile.HitsMax;
                }
            }

            return false;

        }

        #endregion

        #region Commands





        /// <summary>
        /// land
        /// </summary>
        private static bool LandCommand(string command, Argument[] args, bool quiet, bool force)
        {
            Player.Fly(false);
            return true;
        }

        /// UOSteamEngine.FlyCommand
        /// <summary>
        /// fly
        /// </summary>
        private static bool FlyCommand(string command, Argument[] args, bool quiet, bool force)
        {
            Player.Fly(true);
            return true;
        }

        private static bool Pause(string command, Argument[] args, bool quiet, bool force)
        {
            int delay = args[0].AsInt();
            Misc.Pause(delay);
            return true;
        }

        private static bool Info(string command, Argument[] args, bool quiet, bool force)
        {
            Misc.Inspect();
            return true;
        }

        /// UOSteamEngine.SetAbility
        /// <summary>
        /// setability ('primary'/'secondary'/'stun'/'disarm') ['on'/'off']
        /// </summary>
        private static bool SetAbility(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                Misc.SendMessage("set ability not proper syntax");
                return true;
            }
            string ability = args[0].AsString().ToLower();
            bool on = args[1].AsBool();

            switch (ability)
            {
                case "primary":
                    if (on)
                    {
                        Player.WeaponPrimarySA();
                    }
                    else
                    {
                        // I dunno how to turn off
                        Player.WeaponPrimarySA();
                    }
                    break;
                case "secondary":
                    if (on)
                    {
                        Player.WeaponSecondarySA();
                    }
                    else
                    {
                        // I dunno how to turn off
                        Player.WeaponSecondarySA();
                    }
                    break;
                case "stun":
                    if (on)
                    {
                        Player.WeaponStunSA();
                    }
                    else
                    {
                        // I dunno how to turn off
                        Player.WeaponStunSA();
                    }
                    break;
                case "disarm":
                    if (on)
                    {
                        Player.WeaponDisarmSA();
                    }
                    else
                    {
                        // I dunno how to turn off
                        Player.WeaponDisarmSA();
                    }
                    break;
                default:
                    return true;
            }

            return true;
        }

        /// <summary>
        /// attack (serial)
        /// </summary>
        private static bool Attack(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 1)
            {
                Player.SetWarMode(true);
            }
            else
            {
                uint serial = args[0].AsSerial();
                Mobile mobile = Mobiles.FindBySerial((int)serial);
                if (mobile != null)
                    Player.Attack(mobile);
            }

            return true;
        }
        /// <summary>
        /// walk (direction)
        /// </summary>
        private static bool Walk(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0)
                Player.Walk(Player.Direction);

            if (args.Length == 1)
            {
                string direction = args[0].AsString();
                Player.Walk(direction);
            }

            return true;
        }
        
        /// <summary>
        /// pathfindto x y
        /// </summary>
        private static bool PathFindTo(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 2)
            {
                Misc.SendMessage("pathfindto requires an X and Y co-ordinates");
                return false;
            }

            int X = args[0].AsInt();
            int Y = args[1].AsInt();
            PathFinding.PathFindTo(X, Y);

            return true;
        }


        /// <summary>
        /// run (direction)
        /// </summary>
        private static bool Run(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0)
                Player.Run(Player.Direction);

            if (args.Length == 1)
            {
                string direction = args[0].AsString();
                Player.Run(direction);
            }

            return true;
        }

        /// <summary>
        /// turn (direction)
        /// </summary>
        private static bool Turn(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string direction = args[0].AsString();
                if (Player.Direction != direction)
                    Player.Walk(direction);
            }

            return true;
        }


        /// <summary>
        /// clearhands ('left'/'right'/'both')
        /// </summary>
        private static bool ClearHands(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0 || args[0].AsString().ToLower() == "both")
            {
                Player.UnEquipItemByLayer("RightHand", false);
                Player.UnEquipItemByLayer("LeftHand", false);
            }
            if (args.Length == 1)
            {
                if (args[0].AsString().ToLower() == "right")
                    Player.UnEquipItemByLayer("RightHand", false);
                if (args[0].AsString().ToLower() == "left")
                    Player.UnEquipItemByLayer("LeftHand", false);
            }


            return true;
        }

        /// <summary>
        /// clickobject (serial)
        /// </summary>
        private static bool ClickObject(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                int serial = (int)args[0].AsSerial();
                Items.SingleClick(serial);
            }

            return true;
        }

        /// <summary>
        /// bandageself
        /// </summary>
        private static bool BandageSelf(string command, Argument[] args, bool quiet, bool force)
        {
            BandageHeal.Heal(Assistant.World.Player, false);
            return true;
        }


        /// <summary>
        /// usetype (graphic) [color] [source] [range or search level]
        /// </summary>
        private static IComparable UseType(string command, Argument[] args, bool quiet)
        {
            if (args.Length == 0)
            {
                Misc.SendMessage("Insufficient parameters");
                return false;
            }
            int itemID = args[0].AsInt();
            int color = -1;
            int container = -1;
            if (args.Length > 1)
            {
                color = args[1].AsInt();
            }
            if (args.Length > 2)
            {
                container = args[2].AsInt();
            }

            Item item = Items.FindByID(itemID, color, container, true);
            if (item == null)
                return false;

            // because UOS scripts seem to use usetype when they mean targettype
            if (Assistant.Targeting.HasTarget)
            {
                Assistant.Targeting.Target(item.Serial, false);
            }
            else
            {
                Items.UseItem(item.Serial);
                Misc.Pause(100);
            }
            return true;
        }

        /// <summary>
        /// useobject (serial)
        /// </summary>
        private static bool UseObject(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0)
            {
                Misc.SendMessage("Insufficient parameters");
                return true;
            }
            Assistant.Serial serial = (int)args[0].AsSerial();
            if (serial.IsItem)
            {
                // because UOS scripts seem to use usetype when they mean targettype
                if (Assistant.Targeting.HasTarget)
                {
                    Assistant.Targeting.Target(serial, false);
                }
                else
                {
                    Items.UseItem(serial);
                    Misc.Pause(100);
                }
            }
            else
            {
                // because UOS scripts seem to use usetype when they mean targettype
                if (Assistant.Targeting.HasTarget)
                {
                    Assistant.Targeting.Target(serial, false);
                }
                else
                {
                    Mobiles.UseMobile(serial);
                }
            }

            return true;
        }

        /// <summary>
        /// useonce (graphic) [color]
        /// </summary>
        private bool UseOnce(string command, Argument[] args, bool quiet, bool force)
        {
            // This is a bit problematic
            // UOSteam highlights the selected item red in your backpack, and it searches recursively to find the item id
            // Current logic for us, only searches 1 deep .. maybe thats enough for now
            if (args.Length == 0)
            {
                Misc.SendMessage("Insufficient parameters");
                return true;
            }

            int itemID = args[0].AsInt();
            int color = -1;
            if (args.Length > 1)
            {
                color = args[1].AsInt();
            }
            List<Item> items = Player.Backpack.Contains;
            Item selectedItem = null;

            foreach (Item item in items)
            {
                if (item.ItemID == itemID && (color == -1 || item.Hue == color) && (!m_serialUseOnceIgnoreList.Contains(item.Serial)))
                    selectedItem = item;
            }

            if (selectedItem != null)
            {
                m_serialUseOnceIgnoreList.Add(selectedItem.Serial);
                Items.UseItem(selectedItem.Serial);
                Misc.Pause(500);
            }

            return true;
        }

        /// <summary>
        /// clearusequeue resets the use once list
        /// </summary>
        /// 
        private bool CleanUseQueue(string command, Argument[] args, bool quiet, bool force)
        {
            m_serialUseOnceIgnoreList.Clear();
            return true;
        }


        /// <summary>
        /// (serial) (destination) [(x, y, z)] [amount]
        /// </summary>
        private static bool MoveItem(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                Misc.SendMessage("Insufficient parameters");
                return true;
            }
            int source = (int)args[0].AsSerial();
            int dest = (int)args[1].AsSerial();
            int x = -1;
            int y = -1;
            int amount = -1;

            if (args.Length == 3)
            {
                amount = args[2].AsInt();
            }

            if (args.Length > 5)
            {
                x = args[2].AsInt();
                y = args[3].AsInt();
                //int z = args[4].AsInt();
                amount = args[5].AsInt();
            }
            Items.Move(source, dest, amount, x, y);

            return true;
        }

        /// <summary>
        /// useskill ('skill name'/'last')
        /// </summary>
        private static bool UseSkill(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string skill = args[0].AsString();
                Player.UseSkill(skill);
            }
            else if (args.Length == 2)
            {
                string skill = args[0].AsString();
                int serial = (int)args[1].AsSerial();
                Player.UseSkill(skill, serial);
            }
            else
            {
                Misc.SendMessage("Incorrect number of parameters");
            }
            return true;
        }


        /// <summary>
        /// feed (serial) (graphic) [color] [amount]
        /// </summary>
        /// Feed doesn't support food groups etc unless someone adds it
        /// Config has the data now
        private static bool Feed(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                Misc.SendMessage("Insufficient parameters");
                return false;
            }
            int target = (int)args[0].AsSerial();
            int graphic = args[1].AsInt();
            int color = -1;
            int amount = 1;

            if (args.Length > 2)
            {
                color = args[2].AsInt();
            }
            if (args.Length > 3)
            {
                amount = args[1].AsInt();
            }
            Item food = Items.FindByID(graphic, color, Player.Backpack.Serial, true);
            if (food != null)
            {
                if (target == Player.Serial)
                    Items.UseItem(food.Serial);
                else
                    Items.Move(food, target, amount);
            }
            return true;
        }

        /// <summary>
        /// rename (serial) ('name')
        /// </summary>
        private static bool RenamePet(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 2)
            {
                Misc.SendMessage("Incorrect parameters");
                return true;
            }
            int serial = (int)args[0].AsSerial();
            string newName = args[1].AsString();

            Misc.PetRename(serial, newName);

            return true;
        }
        /// <summary>
        /// togglehands ('left'/'right')
        /// </summary>
        private  bool ToggleHands(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string hand = args[0].AsString().ToLower();
                if (hand == "left")
                {
                    Item left = Player.GetItemOnLayer("LeftHand");
                    if (left == null)
                    {
                        if (m_toggle_LeftSave != 0)
                        {
                            Player.EquipItem(m_toggle_LeftSave);
                        }
                    }
                    else
                    {
                        m_toggle_LeftSave = left.Serial;
                        Player.UnEquipItemByLayer("LeftHand", false);
                    }
                }
                if (hand == "right")
                {
                    Item right = Player.GetItemOnLayer("RightHand");
                    if (right == null)
                    {
                        if (m_toggle_RightSave != 0)
                        {
                            Player.EquipItem(m_toggle_RightSave);
                        }
                    }
                    else
                    {
                        m_toggle_RightSave = right.Serial;
                        Player.UnEquipItemByLayer("RightHand", false);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// unsetalias (alias name)
        /// </summary>
        private bool UnSetAlias(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string alias = args[0].AsString();
                m_Interpreter.UnSetAlias(alias);
            }

            return true;
        }

        /// <summary>
        /// setalias (alias name) [serial]
        /// </summary>
        private bool SetAlias(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                return PromptAlias(command, args, quiet, force);
            }
            if (args.Length == 2)
            {
                string alias = args[0].AsString();
                uint value = args[1].AsSerial();
                m_Interpreter.SetAlias(alias, value);
            }

            return true;
        }

        /// <summary>
        /// promptalias (alias name)
        /// </summary>
        private bool PromptAlias(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string alias = args[0].AsString();
                RazorEnhanced.Target target = new RazorEnhanced.Target();
                int value = target.PromptTarget("Target Alias for " + alias);
                m_Interpreter.SetAlias(alias, (uint)value);
            }
            return true;
        }

        /// <summary>
        /// headmsg ('text') [color] [serial]
        /// </summary>
        private static bool HeadMsg(string command, Argument[] args, bool quiet, bool force)
        {
            string msg = args[0].AsString();
            int color = 0;
            int mobile = Player.Serial;
            if (args.Length == 2)
            {
                int value = (int)args[1].AsSerial();
                if (value < 10240)
                    color = value;
                else
                    mobile = value;
            }
            if (args.Length == 3)
            {
                color = args[1].AsInt();
                mobile = (int)args[2].AsSerial();
            }

            Mobiles.Message(mobile, color, msg);

            return true;
        }

        //TODO: Not implemented properly .. I dunno how to do a party only msg
        /// <summary>
        /// partymsg ('text') [color] [serial]
        /// </summary>
        private static bool PartyMsg(string command, Argument[] args, bool quiet, bool force)
        {

            if (args.Length == 1)
            {
                string msg = args[0].AsString();
                Player.ChatParty(msg);
            }

            if (args.Length >= 2)
            {
                string msg = args[0].AsString();
                // 2nd parameter of ChatParty is Serial, to send private messages, not color, what0's
                int serial = args[1].AsInt();
                Player.ChatParty(msg, serial);
            }

            return true;
        }

        /// <summary>
        /// msg text [color]
        /// </summary>
        private static bool MsgCommand(string command, Argument[] args, bool quiet, bool force)
        {
            string msg = args[0].AsString();
            if (args.Length == 1)
            {
                Player.ChatSay(0, msg);
            }
            if (args.Length == 2)
            {
                int color = args[1].AsInt();
                Player.ChatSay(color, msg);
            }

            return true;
        }

        /// <summary>
        /// createlist (list name)
        /// </summary>
        private bool CreateList(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length >= 1)
            {
                Console.WriteLine("Creating list {0}", args[0].AsString());
                m_Interpreter.CreateList(args[0].AsString());
            }
            return true;
        }

        /// <summary>
        /// pushlist('list name') ('element value') ['front'/'back']
        /// </summary>
        private bool PushList(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                Misc.SendMessage("Usage: pushlist ('list name') ('element name') ('front'/'back']");
                throw new RunTimeError(null, "Usage: pushlist ('list name') ('element name') ('front'/'back']");
                // return true;
            }

            string listName = args[0].AsString();
            string frontBack = "back";
            if (args.Length == 3)
            {
                frontBack = args[2].AsString().ToLower();
            }

            uint resolvedAlias = m_Interpreter.GetAlias(args[1].AsString());
            Argument insertItem = args[1];
            if (resolvedAlias == uint.MaxValue)
            {
                Console.WriteLine("Pushing {0} to list {1}", insertItem.AsString(), listName);
                m_Interpreter.PushList(listName, insertItem, (frontBack == "front"), false);
            }
            else
            {
                ASTNode node = new ASTNode(ASTNodeType.INTEGER, resolvedAlias.ToString(), insertItem.Node, insertItem.Node.LineNumber);
                Argument newArg = new Argument(insertItem._script, node);
                Console.WriteLine("Pushing {0} to list {1}", newArg.AsString(), listName);
                m_Interpreter.PushList(listName, newArg, (frontBack == "front"), false);
            }
            return true;
        }


        /// <summary>
        /// moveitemoffset (serial) 'ground' [(x, y, z)] [amount] 
        /// </summary>
        private static bool MoveItemOffset(string command, Argument[] args, bool quiet, bool force)
        {
            uint serial = args[0].AsSerial();
            // string ground = args[1].AsString();
            if (args.Length == 2)
            {
                Items.DropItemGroundSelf((int)serial);
            }
            else
            {
                int amount;
                if (args.Length == 3)
                {
                    amount = args[2].AsInt();
                    Items.DropItemGroundSelf((int)serial, amount);
                }
                else if (args.Length >= 5)
                {
                    var ppos = Player.Position;
                    int X = args[2].AsInt() + ppos.X;
                    int Y = args[3].AsInt() + ppos.Y;
                    int Z = args[4].AsInt() + ppos.Z;

                    amount = (args.Length == 6) ? args[5].AsInt() : -1;
                    Items.MoveOnGround((int)serial, amount, X, Y, Z);
                }
                else
                {
                    WrongParameterCount(command, 5, args.Length, "Valid args num: 2,3,5,6");
                }
            }

            return true;
        }

        /// <summary>
        /// movetype (graphic) (source) (destination) [(x, y, z)] [color] [amount] [range or search level]
        /// </summary>
        private static IComparable MoveType(string command, Argument[] args, bool quiet)
        {
            if (args.Length == 3)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                uint dest = args[2].AsSerial();
                Item item = Items.FindByID(id, -1, (int)src, true);
                if (item != null)
                {
                    Items.Move(item.Serial, (int)dest, item.Amount);
                    return true;
                }
            }
            if (args.Length == 4)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                uint dest = args[2].AsSerial();
                int color = args[3].AsInt();
                Item item = Items.FindByID(id, color, (int)src, true);
                if (item != null)
                {
                    Items.Move(item.Serial, (int)dest, item.Amount);
                    return true;
                }
            }
            if (args.Length == 5)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                uint dest = args[2].AsSerial();
                int color = args[3].AsInt();
                int amount = args[4].AsInt();
                Item item = Items.FindByID(id, color, (int)src, true);
                if (item != null)
                {
                    Items.Move(item.Serial, (int)dest, amount);
                    return true;
                }
            }
            if (args.Length == 6)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                uint dest = args[2].AsSerial();
                int x = args[3].AsInt();
                int y = args[4].AsInt();
                //int z = args[5].AsInt();
                Item item = Items.FindByID(id, -1, (int)src, true);
                if (item != null)
                {
                    Items.Move(item.Serial, (int)dest, 0, x, y);
                    return true;
                }
            }
            if (args.Length == 7)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                uint dest = args[2].AsSerial();
                int x = args[3].AsInt();
                int y = args[4].AsInt();
                //int z = args[5].AsInt();
                int color = args[6].AsInt();
                Item item = Items.FindByID(id, color, (int)src, true);
                if (item != null)
                {
                    Items.Move(item.Serial, (int)dest, -1, x, y);
                    return true;
                }
            }
            if (args.Length == 8)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                uint dest = args[2].AsSerial();
                int x = args[3].AsInt();
                int y = args[4].AsInt();
                //int z = args[5].AsInt();
                int color = args[6].AsInt();
                int amount = args[7].AsInt();
                Item item = Items.FindByID(id, color, (int)src, true);
                if (item != null)
                {
                    Items.Move(item.Serial, (int)dest, amount, x, y);
                    return true;
                }
            }

            return false;
        }
        /// <summary>
        /// movetypeoffset (graphic) (source) 'ground' [(x, y, z)] [color] [amount] [range or search level]
        /// </summary>
        private static bool MoveTypeOffset(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2 || args.Length == 3)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                Item item = Items.FindByID(id, -1, (int)src, true);
                if (item != null)
                {
                    Items.DropItemGroundSelf(item.Serial);
                }
            }
            if (args.Length == 4)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                //uint dest = args[2].AsSerial();
                int color = args[3].AsInt();
                Item item = Items.FindByID(id, color, (int)src, true);
                if (item != null)
                {
                    Items.MoveOnGround(item.Serial, 0, Player.Position.X, Player.Position.Y, Player.Position.Z);
                }
            }
            if (args.Length == 5)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                // uint dest = args[2].AsSerial();
                int color = args[3].AsInt();
                int amount = args[4].AsInt();
                Item item = Items.FindByID(id, color, (int)src, true);
                if (item != null)
                {
                    Items.MoveOnGround(item.Serial, amount, Player.Position.X, Player.Position.Y, Player.Position.Z);
                }
            }
            if (args.Length == 6)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                //uint dest = args[2].AsSerial();
                int x = args[3].AsInt();
                int y = args[4].AsInt();
                int z = args[5].AsInt();
                Item item = Items.FindByID(id, -1, (int)src, true);
                if (item != null)
                {
                    Items.MoveOnGround(item.Serial, 0, x, y, z);
                }
            }
            if (args.Length == 7)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                //uint dest = args[2].AsSerial();
                int x = args[3].AsInt();
                int y = args[4].AsInt();
                int z = args[5].AsInt();
                int color = args[6].AsInt();
                Item item = Items.FindByID(id, color, (int)src, true);
                if (item != null)
                {
                    Items.MoveOnGround(item.Serial, 0, x, y, z);
                }
            }
            if (args.Length == 8)
            {
                int id = args[0].AsInt();
                uint src = args[1].AsSerial();
                //uint dest = args[2].AsSerial();
                int x = args[3].AsInt();
                int y = args[4].AsInt();
                int z = args[5].AsInt();
                int color = args[6].AsInt();
                int amount = args[7].AsInt();
                Item item = Items.FindByID(id, color, (int)src, true);
                if (item != null)
                {
                    Items.MoveOnGround(item.Serial, amount, x, y, z);
                }
            }

            return true;
        }

        /// <summary>
        /// equipitem (serial) (layer)
        /// </summary>
        private static bool EquipItem(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                uint serial = args[0].AsSerial();
                //int layer = args[1].AsInt();
                Player.EquipItem((int)serial);
            }
            return true;
        }
        /// <summary>
        /// togglemounted
        /// </summary>
        private bool ToggleMounted(string command, Argument[] args, bool quiet, bool force)
        {
            // uosteam has a crappy implementation
            // I am gonna change how it works a bit
            if (null != Player.Mount)
            {
                m_lastMount = Player.Mount.Serial;
                m_Interpreter.SetAlias("mount", (uint)m_lastMount);
                Mobiles.UseMobile(Player.Serial);
            }
            else
            {
                if (m_lastMount == 0)
                {
                    m_lastMount = (int)m_Interpreter.GetAlias("mount");
                    if (m_lastMount == 0)
                    {
                        RazorEnhanced.Target target = new RazorEnhanced.Target();
                        m_lastMount = target.PromptTarget("Select a new mount");
                    }
                }
                Mobile mount = Mobiles.FindBySerial(m_lastMount);
                if (mount != null)
                {
                    Items.UseItem(mount.Serial);
                }

            }
            return true;
        }

        /// <summary>
        /// NOT IMPLEMENTED
        /// </summary>
        private static bool EquipWand(string command, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(command, args, quiet, force);
        }
        /// <summary>
        /// buy ('list name')
        /// </summary>
        private static bool Buy(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string buyListName = args[0].AsString().ToLower();
                if (Settings.BuyAgent.ListExists(buyListName))
                {
                    BuyAgent.ChangeList(buyListName);
                    BuyAgent.Enable();
                }
                else
                {
                    Misc.SendMessage(String.Format("Buy List {0} does not exist", buyListName), 55);
                }

            }
            return true;
        }

        /// <summary>
        /// sell ('list name')
        /// </summary>
        private static bool Sell(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string sellListName = args[0].AsString().ToLower();
                if (Settings.SellAgent.ListExists(sellListName))
                {
                    SellAgent.ChangeList(sellListName);
                    SellAgent.Enable();
                }
                else
                {
                    Misc.SendMessage(String.Format("Sell List {0} does not exist", sellListName), 55);
                }

            }
            return true;

        }
        /// <summary>
        /// clearbuy
        /// </summary>
        private static bool ClearBuy(string command, Argument[] args, bool quiet, bool force)
        {
            BuyAgent.Disable();
            return true;
        }

        /// <summary>
        /// clearsell
        /// </summary>
        private static bool ClearSell(string command, Argument[] args, bool quiet, bool force)
        {
            SellAgent.Disable();
            return true;
        }

        /// <summary>
        /// restock ('profile name') [source] [destination] [dragDelay]
        /// </summary>
        private static bool Restock(string command, Argument[] args, bool quiet, bool force)
        {

            int src = -1;
            int dst = -1;
            int delay = -1;
            string restockName = null;

            if (args.Length >= 1)
            {
                restockName = args[0].AsString();
            }
            if (args.Length >= 2)
            {
                src = (int)args[1].AsSerial();
            }
            if (args.Length >= 3)
            {
                dst = (int)args[2].AsSerial();
            }
            if (args.Length >= 4)
            {
                delay = (int)args[3].AsSerial();
            }

            if (restockName != null)
            {
                RazorEnhanced.Restock.RunOnce(restockName, src, dst, delay);
                int max = 30 * 2; // max 30 seconds @ .5 seconds each loop
                while (RazorEnhanced.Restock.Status() == true && (max-- > 0))
                {
                    System.Threading.Thread.Sleep(500);
                }
            }

            return true;
        }

        /// <summary>
        /// organizer ('profile name') [source] [destination] [dragDelay]
        /// </summary>
        private static bool Organizer(string command, Argument[] args, bool quiet, bool force)
        {
            int src = -1;
            int dst = -1;
            int delay = -1;
            string organizerName = null;

            if (args.Length >= 1)
            {
                organizerName = args[0].AsString();
            }
            if (args.Length >= 2)
            {
                src = (int)args[1].AsSerial();
            }
            if (args.Length >= 3)
            {
                dst = (int)args[2].AsSerial();
            }
            if (args.Length >= 4)
            {
                delay = (int)args[3].AsSerial();
            }

            if (organizerName != null)
            {
                RazorEnhanced.Organizer.RunOnce(organizerName, src, dst, delay);
                int max = 30 * 2; // max 30 seconds @ .5 seconds each loop
                while (RazorEnhanced.Organizer.Status() == true && (max-- > 0))
                {
                    System.Threading.Thread.Sleep(500);
                }
            }

            return true;
        }

        private static bool AutoTargetObject(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                Assistant.Targeting.SetAutoTarget(serial);
            }
            return true;
        }

        /// <summary>
        /// autoloot - NOT IMPLEMENTED
        /// </summary>
        private static bool Autoloot(string command, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(command, args, quiet, force);
        }

        /// <summary>
        /// dress ['profile name']
        /// </summary>
        private static bool Dress(string command, Argument[] args, bool quiet, bool force)
        {

            if (args.Length == 1)
            {
                string dressListName = args[0].AsString();
                if (Settings.Dress.ListExists(dressListName))
                {
                    RazorEnhanced.Dress.ChangeList(dressListName);
                }
                else
                {
                    Misc.SendMessage(String.Format("Dress List {0} does not exist", dressListName), 55);
                }
            }
            RazorEnhanced.Dress.DressFStart();
            return true;
        }

        /// <summary>
        /// undress ['profile name']
        /// </summary>
        private static bool Undress(string command, Argument[] args, bool quiet, bool force)
        {

            if (args.Length == 1)
            {
                string unDressListName = args[0].AsString();
                if (Settings.Dress.ListExists(unDressListName))
                {
                    RazorEnhanced.Dress.ChangeList(unDressListName);
                }
                else
                {
                    Misc.SendMessage(String.Format("UnDress List {0} does not exist", unDressListName), 55);
                }
            }
            RazorEnhanced.Dress.UnDressFStart();
            return true;
        }

        /// <summary>
        /// dressconfig What is this supposed to do ? NOT IMPLEMENTED
        /// </summary>
        private static bool DressConfig(string command, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(command, args, quiet, force);
        }

        /// <summary>
        /// toggleautoloot
        /// </summary>
        private static bool ToggleAutoloot(string command, Argument[] args, bool quiet, bool force)
        {
            if (RazorEnhanced.AutoLoot.Status())
            {
                RazorEnhanced.AutoLoot.Stop();
            }
            else
            {
                RazorEnhanced.AutoLoot.Start();
            }
            return true;
        }

        /// <summary>
        /// togglescavenger
        /// </summary>
        private static bool ToggleScavenger(string command, Argument[] args, bool quiet, bool force)
        {
            if (RazorEnhanced.Scavenger.Status())
            {
                RazorEnhanced.Scavenger.Stop();
            }
            else
            {
                RazorEnhanced.Scavenger.Start();
            }
            return true;
        }

        /// <summary>
        /// counter ('format') (operator) (value) NOT IMPLEMENTED
        /// </summary>
        private static bool Counter(string command, Argument[] args, bool quiet, bool force)
        {
                return NotImplemented(command, args, quiet, force);
        }

        /// <summary>
        /// waitforgump (gump id/'any') (timeout)
        /// </summary>
        private static bool WaitForGump(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                uint gumpid = args[0].AsUInt();
                int delay = args[1].AsInt();
                Gumps.WaitForGump(gumpid, delay);
            }
            return true;
        }

        /// <summary>
        /// replygump gump-id button [switch ...]
        /// </summary>
        private static bool ReplyGump(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                uint gumpid = args[0].AsUInt();
                int buttonid = args[1].AsInt();
                Gumps.SendAction(gumpid, buttonid);
            }
            if (args.Length > 2)
            {
                uint gumpid = args[0].AsUInt();
                int buttonid = args[1].AsInt();
                List<int> switches = new List<int>();
                for (int i= 2; i < args.Length; i++)
                {
                    int switchid = args[i].AsInt();
                    switches.Add(switchid);
                }
                
                Gumps.SendAdvancedAction(gumpid, buttonid, switches);
            }

            return true;
        }

        /// <summary>
        /// closegump 'container' 'serial'
        /// </summary>
        private static bool CloseGump(string command, Argument[] args, bool quiet, bool force)
        {

            if (args.Length == 2)
            {
                string container = args[0].AsString().ToLower();
                if (container == "container")
                {
                    uint gumpid = args[1].AsSerial();
                    Gumps.CloseGump(gumpid);
                }
                else
                {
                    Misc.SendMessage(String.Format("Unable to closegumps on {0} type objects", container), 55);
                }
            }
            return true;
        }

        /// <summary>
        /// clearjournal
        /// </summary>
        private bool ClearJournal(string command, Argument[] args, bool quiet, bool force)
        {
            m_journal.Clear();
            return true;
        }

        /// <summary>
        /// waitforjournal ('text') (timeout) 
        /// </summary>
        private bool WaitForJournal(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                string text = args[0].AsString();
                int delay = args[1].AsInt();
                m_journal.WaitJournal(text, delay);
            }
            return true;
        }

        /// <summary>
        /// poplist ('list name') ('element value'/'front'/'back')
        /// </summary>
        private bool PopList(string command, Argument[] args, bool quiet, bool force)
        {
            string frontBack = args[1].AsString().ToLower();
            m_Interpreter.PopList(args[0].AsString(), (frontBack == "front"));
            return true;
        }

        /// <summary>
        /// removelist ('list name')
        /// </summary>
        private bool RemoveList(string command, Argument[] args, bool quiet, bool force)
        {
            m_Interpreter.DestroyList(args[0].AsString());
            return true;
        }

        /// <summary>
        /// clearlist ('list name')
        /// </summary>
        private bool ClearList(string command, Argument[] args, bool quiet, bool force)
        {
            m_Interpreter.ClearList(args[0].AsString());
            return true;
        }

        /// <summary>
        /// ping
        /// </summary>
        private static bool Ping(string command, Argument[] args, bool quiet, bool force)
        {
            Assistant.Commands.Ping(null);
            return true;
        }

        /// <summary>
        /// playmacro 'name'
        /// </summary>
        private static bool PlayMacro(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length > 0)
            {
                var macroAndArgs = new List<string>();
                foreach (var arg in args)
                {
                    macroAndArgs.Add(arg.AsString());
                }
                Assistant.Commands.PlayScript( macroAndArgs.ToArray() );
            }

            return true;
        }

        /// <summary>
        /// playsound (sound id/'file name') 
        /// </summary>
        private static bool PlaySound(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string filename = args[0].AsString();
                string fullpath = Path.Combine(Assistant.Engine.RootPath, filename);
                System.Media.SoundPlayer player = new System.Media.SoundPlayer(fullpath);
                player.Play();
            }
            return true;
        }

        /// <summary>
        /// resync
        /// </summary>
        private static bool Resync(string command, Argument[] args, bool quiet, bool force)
        {
            Misc.Resync();
            return true;
        }

        /// <summary>
        /// snapshot 
        /// </summary>
        private static bool Snapshot(string command, Argument[] args, bool quiet, bool force)
        {
            Assistant.ScreenCapManager.CaptureNow();
            return true;
        }

        /// <summary>
        /// hotkeys
        /// </summary>
        private static bool Hotkeys(string command, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(command, args, quiet, force);
        }

        /// <summary>
        /// where
        /// </summary>
        private static bool Where(string command, Argument[] args, bool quiet, bool force)
        {
            Assistant.Commands.Where(null);
            return true;
        }

        /// <summary>
        /// messagebox ('title') ('body')
        /// </summary>
        private static bool MessageBox(string command, Argument[] args, bool quiet, bool force)
        {
            string title = "not specified";
            string body = "empty";
            if (args.Length > 0)
            {
                title = args[0].AsString();
            }
            if (args.Length > 1)
            {
                title = args[1].AsString();
            }
            System.Windows.Forms.MessageBox.Show(body, title);
            return true;
        }

        /// <summary>
        /// mapuo NOT IMPEMENTED
        /// </summary>
        private static bool MapUO(string command, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(command, args, quiet, force);
        }


        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        public const int MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const int MOUSEEVENTF_LEFTUP = 0x0004;
        public const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const int MOUSEEVENTF_RIGHTUP = 0x0010;

        //This simulates a left mouse click
        internal static void LeftMouseClick(int xpos, int ypos)
        {
            SetCursorPos(xpos, ypos);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, xpos, ypos, 0, 0);
            mouse_event(MOUSEEVENTF_RIGHTUP, xpos, ypos, 0, 0);
        }
        internal static void RightMouseClick(int xpos, int ypos)
        {
            SetCursorPos(xpos, ypos);
            mouse_event(MOUSEEVENTF_LEFTDOWN, xpos, ypos, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTUP, xpos, ypos, 0, 0);
        }

        /// <summary>
        /// clickscreen (x) (y) ['single'/'double'] ['left'/'right']
        /// </summary>
        private static bool ClickScreen(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                Misc.SendMessage("Invalid parameters", 55);
                return true;
            }
            int x = args[0].AsInt();
            int y = args[1].AsInt();
            string singleDouble = "single";
            string leftRight = "left";

            if (args.Length > 2)
                singleDouble = args[2].AsString().ToLower();
            if (args.Length > 3)
                leftRight = args[3].AsString().ToLower();

            if (leftRight == "left")
            {
                LeftMouseClick(x, y);
                if (singleDouble == "double")
                {
                    System.Threading.Thread.Sleep(50);
                    LeftMouseClick(x, y);
                }
            }
            else
            {
                RightMouseClick(x, y);
                if (singleDouble == "double")
                {
                    System.Threading.Thread.Sleep(50);
                    RightMouseClick(x, y);
                }
            }

            return true;
        }

        /// <summary>
        /// paperdoll
        /// </summary>
        private static bool Paperdoll(string command, Argument[] args, bool quiet, bool force)
        {

            Misc.OpenPaperdoll();

            return true;
        }

        /// <summary>
        /// helpbutton  NOT IMPLEMENTED
        /// </summary>
        private static bool HelpButton(string command, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(command, args, quiet, force);
        }

        /// <summary>
        /// guildbutton 
        /// </summary>
        private static bool GuildButton(string command, Argument[] args, bool quiet, bool force)
        {
            Player.GuildButton();
            return true;
        }

        /// <summary>
        /// questsbutton 
        /// </summary>
        private static bool QuestsButton(string command, Argument[] args, bool quiet, bool force)
        {
            Player.QuestButton();
            return true;
        }

        /// <summary>
        /// logoutbutton 
        /// </summary>
        private static bool LogoutButton(string command, Argument[] args, bool quiet, bool force)
        {
            Misc.Disconnect();
            return true;
        }

        /// <summary>
        /// virtue('honor'/'sacrifice'/'valor') 
        /// </summary>
        private static bool Virtue(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string virtue = args[0].AsString();
                Player.InvokeVirtue(virtue);
            }

            return true;
        }

        /// <summary>
        /// guildmsg ('text')
        /// </summary>
        private static bool GuildMsg(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string msg = args[0].AsString();
                Player.ChatGuild(msg);
            }
            return true;
        }

        /// <summary>
        /// allymsg ('text')
        /// </summary>
        private static bool AllyMsg(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string msg = args[0].AsString();
                Player.ChatAlliance(msg);
            }
            return true;
        }

        /// <summary>
        /// whispermsg ('text') [color]
        /// </summary>
        private static bool WhisperMsg(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                int color = 40;
                if (args.Length == 2)
                {
                    color = args[1].AsInt();
                }

                string msg = args[0].AsString();
                Player.ChatWhisper(color, msg);
            }
            return true;
        }

        /// <summary>
        /// yellmsg ('text') [color]
        /// </summary>
        private static bool YellMsg(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                int color = 170;
                if (args.Length == 2)
                {
                    color = args[1].AsInt();
                }

                string msg = args[0].AsString();
                Player.ChatYell(color, msg);
            }
            return true;
        }

        /// <summary>
        /// location (serial)
        /// </summary>
        private static bool Location(string command, Argument[] args, bool quiet, bool force)
        {
            uint serial = args[0].AsSerial();
            Mobile m = Mobiles.FindBySerial((int)serial);
            Misc.SendMessage(String.Format("Position({0}, {1})", m.Position.X, m.Position.Y), 32, false);

            return true;
        }

        /// <summary>
        /// sysmsg (text) [color]
        /// </summary>
        private static bool SysMsg(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                Misc.SendMessage(args[0].AsString());
            }
            if (args.Length == 2)
            {
                Misc.SendMessage(args[0].AsString(), args[1].AsInt(), false);
            }

            return true;
        }

        /// <summary>
        /// chatmsg (text) [color]
        /// </summary>
        private static bool ChatMsg(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                int color = 70;
                if (args.Length == 2)
                {
                    color = args[1].AsInt();
                }

                string msg = args[0].AsString();
                Player.ChatSay(color, msg);
            }
            return true;
        }

        /// <summary>
        /// emotemsg (text) [color]
        /// </summary>
        private static bool EmoteMsg(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1 || args.Length == 2)
            {
                int color = 70;
                if (args.Length == 2)
                {
                    color = args[1].AsInt();
                }

                string msg = args[0].AsString();
                Player.ChatEmote(color, msg);
            }
            return true;
        }

        /// <summary>
        /// promptmsg (text) [color]
        /// </summary>
        private static bool PromptMsg(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                var msg = args[0].AsString();
                Misc.ResponsePrompt(msg);
            }
            return true;
        }

        /// <summary>
        /// timermsg (delay) (text) [color]
        /// </summary>
        private static bool TimerMsg(string command, Argument[] args, bool quiet, bool force)
        {
            //Verrify/Guessing parameter order.
            if (args.Length == 2)
            {
                var delay = args[0].AsInt();
                var msg = args[1].AsString();
                var color = 20;
                if (args.Length == 3) {
                    color = args[2].AsInt();
                }
                Task.Delay(delay).ContinueWith( t => Misc.SendMessage(msg, color) );
            }
            return true;
        }

        /// <summary>
        /// waitforprompt (timeout)
        /// </summary>
        private static bool WaitForPrompt(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                var delay = args[0].AsInt();
                Misc.WaitForPrompt(delay);
            }
            return true;
        }

        /// <summary>
        /// cancelprompt 
        /// </summary>
        private static bool CancelPrompt(string command, Argument[] args, bool quiet, bool force)
        {
            Misc.CancelPrompt();
            return true;
        }

        /// <summary>
        /// addfriend [serial]
        /// </summary>
        private static bool AddFriend(string command, Argument[] args, bool quiet, bool force)
        {
            // docs say something about options, guessing thats the selection ?
            // docs sucks and I stuggle to find examples on what params it takes
            //
            // TODO: Hypothetical implementation: 0 args -> prompt for serial, 1 arg = serial
            // once verified, remove NotImplemented below
            var list_name = DEFAULT_FRIEND_LIST;
            if (!RazorEnhanced.Settings.Friend.ListExists(list_name)) {
                RazorEnhanced.Settings.Friend.ListInsert(list_name, true, true, false, false, false, false, false);
            }

            int serial = -1;
            if (args.Length == 0) {
                serial = new Target().PromptTarget();
            }else if (args.Length == 1)
            {
                serial = args[0].AsInt();
            }


            if (serial > 0 ) {
                var new_friend = Mobiles.FindBySerial(serial);
                string name = new_friend.Name;
                Friend.AddPlayer(list_name, name, serial);
            }
            return true;
        }

        /// <summary>
        /// removefriend NOT IMPLEMENTED
        /// </summary>
        private static bool RemoveFriend(string command, Argument[] args, bool quiet, bool force)
        {
            // the Razor API for removing a frend is not pretty ( agent code, midex up with form code a bit, NotImplemented for now )
            return NotImplemented(command, args, quiet, force);
        }

        /// <summary>
        /// contextmenu (serial) (option)
        /// </summary>
        private static bool ContextMenu(string command, Argument[] args, bool quiet, bool force)
        {
            // docs say something about options, guessing thats the selection ?
            if (args.Length == 2)
            {
                uint serial = args[0].AsSerial();
                string option = args[1].AsString();
                Misc.ContextReply ((int)serial, option);
            }

            return true;
        }

        /// <summary>
        /// waitforcontext (serial) (option) (timeout)
        /// </summary>
        private static bool WaitForContext(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2)
            {
                Misc.SendMessage("Usage is waitforcontents serial contextSelection timeout");
                WrongParameterCount(command, 2, args.Length, "waitforcontents serial contextSelection timeout");
            }
            int timeout = 5000;
            if (args.Length > 2)
            {
                timeout = args[2].AsInt();
            }
            if (args.Length > 1)
            {
                uint serial = args[0].AsSerial();

                try
                {
                    int intOption = args[1].AsInt();                    
                    Misc.WaitForContext((int)serial, timeout, false);
                    Misc.ContextReply((int)serial, intOption);
                    return true;
                }
                catch (RazorEnhanced.UOS.RunTimeError)
                {
                     // try string
                }

                string option = args[1].AsString();
                Misc.WaitForContext((int)serial, timeout, false);
                Misc.ContextReply((int)serial, option);
                return true;
            }

            return true;
        }

        /// <summary>
        /// ignoreobject (serial)
        /// </summary>
        private static bool IgnoreObject(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                Misc.IgnoreObject((int)serial);
            }
            return true;
        }

        /// <summary>
        /// clearignorelist
        /// </summary>
        private static bool ClearIgnoreList(string command, Argument[] args, bool quiet, bool force)
        {
            Misc.ClearIgnore();
            return true;
        }

        /// <summary>
        /// setskill ('skill name') ('locked'/'up'/'down')
        /// </summary>
        private static bool SetSkill(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                string skill = args[0].AsString().ToLower();
                string action = args[1].AsString().ToLower();
                int setAs = 0;
                switch (action)
                {
                    case "up":
                        setAs = 0;
                        break;
                    case "down":
                        setAs = 1;
                        break;
                    case "locked":
                        setAs = 2;
                        break;
                }
                Player.SetSkillStatus(skill, setAs);
            }
            return true;
        }

        /// <summary>
        /// waitforproperties (serial) (timeout)
        /// </summary>
        private static bool WaitForProperties(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                uint serial = args[0].AsSerial();
                int timeout = args[1].AsInt();
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    Items.WaitForProps(item, timeout);
                }
            }
            return true;
        }

        /// <summary>
        /// autocolorpick (color) (dyesSerial) (dyeTubSerial)
        /// </summary>
        private static bool AutoColorPick(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 3)
            {
                Misc.SendMessage("Usage is: autocolorpick color dyesSerial dyeTubSerial");
                WrongParameterCount(command, 3, args.Length, "Usage is: autocolorpick color dyesSerial dyeTubSerial");

            }
            int color = args[0].AsInt();
            uint dyesSerial = args[1].AsSerial();
            uint dyeTubSerial = args[2].AsSerial();
            Item dyes = Items.FindBySerial((int)dyesSerial);
            Item dyeTub = Items.FindBySerial((int)dyeTubSerial);
            if (dyes == null) { Misc.SendMessage("autocolorpick: error: can't find dyes with serial " + dyesSerial); }
            if (dyeTub == null) { Misc.SendMessage("autocolorpick: error: can't find dye tub with serial " + dyeTubSerial); }
            if (dyes != null && dyeTub != null)
            {
                Items.ChangeDyeingTubColor(dyes, dyeTub, color);
            }
            return true;
        }

        /// <summary>
        /// waitforcontents (serial) (timeout)
        /// </summary>
        private static bool WaitForContents(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 2)
            {
                uint serial = args[0].AsSerial();
                int timeout = args[0].AsInt();
                Item item = Items.FindBySerial((int)serial);
                if (item != null)
                {
                    Items.WaitForContents(item, timeout);
                }
            }
            return true;
        }


        //Not a UOS Function, utility method for autocure within big/small heal
        private static bool SelfCure()
        {
            if (Player.Poisoned) {
                RazorEnhanced.Target.Cancel();
                Spells.CastMagery("Cure");
                RazorEnhanced.Target.WaitForTarget(2500); //TODO: find reasonable delay
                if (RazorEnhanced.Target.HasTarget())
                {
                    RazorEnhanced.Target.Self();
                }
                RazorEnhanced.Target.Cancel();
                return true;
            }
            return false;
        }

        /// <summary>
        /// miniheal [serial]
        /// </summary>
        private static bool MiniHeal(string command, Argument[] args, bool quiet, bool force)
        {
            if (SelfCure()) { return true;  }

            RazorEnhanced.Target.Cancel();
            Spells.CastMagery("Heal");
            RazorEnhanced.Target.WaitForTarget(2500); //TODO: find reasonable delay

            if (RazorEnhanced.Target.HasTarget())
            {
                if (args.Length == 0)
                {
                    RazorEnhanced.Target.Self();
                }
                else
                {
                    var serial = args[0].AsInt();
                    RazorEnhanced.Target.TargetExecute(serial);
                }
                RazorEnhanced.Target.Cancel();
            }
            return true;
        }

        /// <summary>
        /// bigheal [serial]
        /// </summary>
        private static bool BigHeal(string command, Argument[] args, bool quiet, bool force)
        {
            if (SelfCure()) { return true; }

            RazorEnhanced.Target.Cancel();
            Spells.CastMagery("Greater Heal");
            RazorEnhanced.Target.WaitForTarget(2500); //TODO: find reasonable delay

            if (RazorEnhanced.Target.HasTarget())
            {
                if (args.Length == 0)
                {
                    RazorEnhanced.Target.Self();
                }
                else
                {
                    var serial = args[0].AsInt();
                    RazorEnhanced.Target.TargetExecute(serial);
                }
                RazorEnhanced.Target.Cancel();
            }
            return true;
        }

        /// <summary>
        /// cast (spell id/'spell name'/'last') [serial]
        /// </summary>
        private static bool Cast(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                string spell = args[0].AsString();
                Spells.Cast(spell);
            }
            else if (args.Length == 2)
            {
                string spell = args[0].AsString();
                uint serial = args[1].AsSerial();
                Spells.Cast(spell, serial);
            }
            else 
            {
                Misc.SendMessage("Incorrect number of parameters");
            }



            return true;
        }

        /// <summary>
        /// chivalryheal [serial] 
        /// </summary>
        private static bool ChivalryHeal(string command, Argument[] args, bool quiet, bool force)
        {
            RazorEnhanced.Target.Cancel();
            Spells.CastChivalry("Close Wounds");
            RazorEnhanced.Target.WaitForTarget(2500); //TODO: find reasonable delay

            if (RazorEnhanced.Target.HasTarget()) {
                if (args.Length == 0)
                {
                    RazorEnhanced.Target.Self();
                }
                else {
                    var serial = args[0].AsInt();
                    RazorEnhanced.Target.TargetExecute(serial);
                }
                RazorEnhanced.Target.Cancel();
            }
            return true;
        }

        /// <summary>
        /// waitfortarget (timeout)
        /// </summary>
        private static bool WaitForTarget(string command, Argument[] args, bool quiet, bool force)
        {
            int delay = 1000;
            bool show = false;
            if (args.Length > 0)
            {
                delay = args[0].AsInt();
            }
            if (args.Length > 1)
            {
                show = args[1].AsBool();
            }

            RazorEnhanced.Target.WaitForTarget(delay, show);
            return true;
        }

        /// <summary>
        /// cancelautotarget
        /// </summary>
        private static bool CancelAutoTarget(string command, Argument[] args, bool quiet, bool force)
        {
            Assistant.Targeting.CancelAutoTarget();
            return true;
        }

        /// <summary>
        /// canceltarget
        /// </summary>
        private static bool CancelTarget(string command, Argument[] args, bool quiet, bool force)
        {
            //https://discord.com/channels/292282788311203841/383331237269602325/839987031853105183
            //Target.Execute(0x0) is a better form of Target.Cancel
            RazorEnhanced.Target.TargetExecute(0x0);
            //RazorEnhanced.Target.Cancel();
            return true;
        }

        /// <summary>
        /// targetresource (serial) ('ore'/'sand'/'wood'/'graves'/'red mushrooms')
        /// </summary>
        private static bool TargetResource(string command, Argument[] args, bool quiet, bool force)
        {
            // targetresource serial (ore/sand/wood/graves/red mushrooms)
            if (args.Length != 2)
            {
                WrongParameterCount(command, 2, args.Length);
            }
            uint tool = args[0].AsSerial();
            string resource = args[1].AsString();
            RazorEnhanced.Target.TargetResource((int)tool, resource);

            return true;

            /*
            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                RazorEnhanced.Target.TargetExecute((int)serial);
            }
            return true;
            */
        }

        /// <summary>
        /// target (serial)
        /// </summary>
        private static bool Target(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 1)
            {
                uint serial = args[0].AsSerial();
                RazorEnhanced.Target.TargetExecute((int)serial);
            }
            return true;
        }

        /// <summary>
        /// getenemy ('notoriety') ['filter']
        /// </summary>
        private bool GetEnemy(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0) { WrongParameterCount(command, 1, args.Length); }

            RazorEnhanced.Mobiles.Filter filter = new RazorEnhanced.Mobiles.Filter();
            bool nearest = false;
            foreach (var arg in args)
            {
                string argStr = arg.AsString().ToLower();
                switch (argStr)
                {
                    case "friend":
                        filter.Notorieties.Add(1);
                        break;
                    case "innocent":
                        filter.Notorieties.Add(2);
                        break;
                    case "criminal":
                        filter.Notorieties.Add(4);
                        break;
                    case "gray":
                        filter.Notorieties.Add(3);
                        filter.Notorieties.Add(4);
                        break;
                    case "murderer":
                        filter.Notorieties.Add(6);
                        break;
                    case "enemy":
                        filter.Notorieties.Add(6);
                        filter.Notorieties.Add(5);
                        filter.Notorieties.Add(4);
                        break;
                    case "humanoid":
                        filter.IsHuman = 1;
                        break;
                    case "transformation":
                        //TODO: add ids for transformations: ninja, necro, polymorpjh(?), etc
                    case "closest":
                    case "nearest":
                        nearest = true;
                        break;
                }
            }

            var list = Mobiles.ApplyFilter(filter);
            if (list.Count > 0)
            {
                Mobile anEnemy = list[0];
                if (nearest)
                {
                    anEnemy = Mobiles.Select(list, "Nearest");
                }


                int color = 20;
                switch (anEnemy.Notoriety){
                    case 1: color = 190; break; //Blue
                    case 2: color = 168; break; //Green
                    case 3:
                    case 4: color = 1000; break; //Gray
                    case 5: color = 140; break; //Orange
                    case 6: color = 138; break; //Red
                    case 7: color = 153; break; //Yellow
                }
                RazorEnhanced.Target.SetLast(anEnemy.Serial); //Attempt to highlight
                if (! quiet)
                    Player.HeadMessage(color, "[Enemy] " + anEnemy.Name);
                m_Interpreter.SetAlias("enemy", (uint)anEnemy.Serial);
            }
            else
            {
                m_Interpreter.UnSetAlias("enemy");
            }
            return true;
        }

        /// <summary>
        /// getfriend ('notoriety') ['filter']
        /// </summary>
        private bool GetFriend(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length == 0) { WrongParameterCount(command, 1, args.Length); }

            RazorEnhanced.Mobiles.Filter filter = new RazorEnhanced.Mobiles.Filter();
            bool nearest = false;
            foreach (var arg in args)
            {
                string argStr = arg.AsString().ToLower();
                switch (argStr)
                {
                    case "friend":
                        filter.Notorieties.Add(1);
                        break;
                    case "innocent":
                        filter.Notorieties.Add(2);
                        break;
                    case "criminal":
                    case "gray":
                        filter.Notorieties.Add(3);
                        filter.Notorieties.Add(4);
                        break;
                    case "murderer":
                        filter.Notorieties.Add(6);
                        break;
                    case "enemy":
                        filter.Notorieties.Add(6);
                        filter.Notorieties.Add(5);
                        filter.Notorieties.Add(4);
                        break;
                    case "invulnerable":
                        filter.Notorieties.Add(7);
                        break;
                    case "humanoid":
                        filter.IsHuman = 1;
                        break;
                    case "transformation":
                        //TODO: add ids for transformations: ninja, necro, polymorpjh(?), etc
                    case "closest":
                    case "nearest":
                        nearest = true;
                        break;
                }
            }
            var list = Mobiles.ApplyFilter(filter);
            if (list.Count > 0)
            {
                Mobile anEnemy = list[0];
                if (nearest)
                {
                    anEnemy = Mobiles.Select(list, "Nearest");
                }

                int color = 20;
                switch (anEnemy.Notoriety)
                {
                    case 1: color = 190; break; //Blue
                    case 2: color = 168; break; //Green
                    case 3:
                    case 4: color = 1000; break; //Gray
                    case 5: color = 140; break; //Orange
                    case 6: color = 138; break; //Red
                    case 7: color = 153; break; //Yellow
                }
                RazorEnhanced.Target.SetLast(anEnemy.Serial); //Attempt to highlight
                if (!quiet)
                    Player.HeadMessage(color, "[Friend] " + anEnemy.Name);
                m_Interpreter.SetAlias("friend", (uint)anEnemy.Serial);
            }
            else
            {
                m_Interpreter.UnSetAlias("friend");
            }
            return true;
        }



        /// <summary>
        /// namespace 'isolation' (true|false)
        /// namespace 'list'
        /// namespace ('create'|'activate'|'delete') (namespace_name) 
        /// namespace 'move' (new_namespace_name) [old_namespace_name] ['merge'|'replace']
        /// namespace ('get'|'set'|'print') (namespace_name) ['all'|'alias'|'lists'|'timers'] [source_name] [destination_name]
        /// namespace 'print' [namespace_name] ['all'|'alias'|'lists'|'timers'] [name]
        /// </summary>
        private bool ManageNamespaces(string command, Argument[] args, bool quiet, bool force)
        {
            var cmd = command;
            var operation = args[0].AsString();
            cmd = $"{cmd}_{operation}";
            if (operation == "list")
            {
                return ManageNamespaces_List(cmd, args, quiet, force);
            }
            else if (operation == "isolation")
            {
                return ManageNamespaces_Isolation(cmd, args, quiet, force);
            }
            else if (operation == "activate" || operation == "create")
            {
                return ManageNamespaces_CreateActivate(cmd, args, quiet, force);
            }
            else if (operation == "delete")
            {
                return ManageNamespaces_Delete(cmd, args, quiet, force);
            }
            else if (operation == "move")
            {
                return ManageNamespaces_Move(cmd, args, quiet, force);
            }
            else if (operation == "print")
            {
                return ManageNamespaces_Print(cmd, args, quiet, force);
            }
            else if (operation == "get" || operation == "set")
            {
                return ManageNamespaces_SetGet(cmd, args, quiet, force);
            }
            else
            {
                throw new IllegalArgumentException(cmd + " not recognized.");
            }

            return true;
        }

        private bool ManageNamespaces_List(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 1) { WrongParameterCount(command, 2, args.Length); }
            //var toListName = (args.Length == 2) ? args[1].ToString() : null;
            //if (toListName != null)
            //{
            //m_Interpreter.CreateList(toListName);
            //Namespace.List().Apply();
            //Namespace._lists[toListName] = new List<Argument>();
            //
            //}
            //else
            //{    }
            Misc.SendMessage("Namespaces:");
            foreach (var name in Namespace.List())
            {
                Misc.SendMessage(name);
            }
            //
            return true;
        }

        private bool ManageNamespaces_Isolation(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2) { WrongParameterCount(command, 2, args.Length); }
            this.UseIsolation = args[1].AsBool();
            return true;
        }
        private bool ManageNamespaces_CreateActivate(string command, Argument[] args, bool quiet, bool force)
        {
            var namespace_name = args.Length >= 2 ? args[1].AsString() : null;
            var operation = args[0].AsString();
            if (args.Length < 2) { WrongParameterCount(command, 2, args.Length); }
            var ns = Namespace.Get(namespace_name);
            if (operation == "activate") { this.Namespace = ns; }
            return true;

        }
        private bool ManageNamespaces_Delete(string command, Argument[] args, bool quiet, bool force)
        {
            var cur_namespace_name = Namespace.Name;
            var namespace_name = args.Length >= 2 ? args[1].AsString() : null;
            if (cur_namespace_name == namespace_name){
                this.Namespace = Namespace.Get();
            }
            Namespace.Delete(namespace_name);
            return true;
        }
        private bool ManageNamespaces_Move(string command, Argument[] args, bool quiet, bool force)
        {
            
            if (args.Length < 2) { WrongParameterCount(command, 2, args.Length); }
            var namespace_name = args[1].AsString();

            var old_name = Namespace.Name;
            var new_name = namespace_name;
            
            if (args.Length >= 3)
            {
                old_name = args[2].AsString();
            }

            var merge = args.Length == 4 && args[3].AsString() == "merge";
            var replace = args.Length == 4 && args[3].AsString() == "replace";
            merge = (!merge && !replace);
            replace = !merge;

            var cmd = $"{command} '{new_name}'" + (replace ? " 'replace'" : "") + (merge ? " 'merge'" : "");
            var didMove = Namespace.Move(old_name, new_name, replace);
            if (!didMove)
            {
                throw new IllegalArgumentException(cmd + $" failed, old name '{old_name}' not found.");
            }
            return true;
        }
        /// namespace 'print' [namespace_name] ['all'|'alias'|'lists'|'timers'] [name]
        private bool ManageNamespaces_Print(string command, Argument[] args, bool quiet, bool force)
        {
            var operation = args[0].AsString();
            var namespace_name = args.Length >=2 ? args[1].AsString() : Namespace.Name;
            var item_type = args.Length >= 3 ? args[2].AsString() : "all";
            var item_name = args.Length == 4 ? args[3].AsString() : null;

            if (!Namespace.Has(namespace_name))
            {
                throw new IllegalArgumentException($" {command} namespace '{namespace_name}' not found.");
            }
            
            var cmd = $"{command} '{namespace_name}' '{item_type}'";
            var ns = Namespace.Get(namespace_name);
            var content = "";
            if (item_type == "all")
            {
                content = Namespace.PrintAll(namespace_name, item_name);
            }
            else if (item_type == "alias")
            {
                content = Namespace.PrintAlias(namespace_name, item_name);
            }
            else if (item_type == "lists")
            {
                content = Namespace.PrintLists(namespace_name, item_name);
            }
            else if (item_type == "timers")
            {
                content = Namespace.PrintTimers(namespace_name, item_name);
            }
            else
            {
                throw new IllegalArgumentException($"{cmd} items kind must be either: 'all', 'alias', 'lists', 'timers'  ");
            }
            Misc.SendMessage(content);
            return true;
        }
        private bool ManageNamespaces_SetGet(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 2) { WrongParameterCount(command, 2, args.Length); }
            var operation = args[0].AsString();
            var namespace_name = args[1].AsString();

            if (!Namespace.Has(namespace_name))
            {
                throw new IllegalArgumentException($"{command} namespace '{namespace_name}' not found.");
            }

            var item_type = args.Length >= 3 ? args[2].AsString() : "all";
            var cmd = $"{command} '{namespace_name}' '{item_type}'";
            var ns = Namespace.Get(namespace_name);

            var namespace_src = (operation == "get" ? ns.Name : this.Namespace.Name);
            var namespace_dst = (operation == "get" ? this.Namespace.Name : ns.Name);
            var name_src = args.Length >= 4 ? args[3].AsString() : null;
            var name_dst = args.Length >= 5 ? args[4].AsString() : null;

            // 'alias'|'lists'|'timers'|'all'
            var copy_ok = false;
            if (item_type == "all")
            {
                copy_ok = Namespace.CopyAll(namespace_src, namespace_dst, name_src, name_dst);
            }
            else if (item_type == "alias")
            {
                copy_ok = Namespace.CopyAlias(namespace_src, namespace_dst, name_src, name_dst);
            }
            else if (item_type == "lists")
            {
                copy_ok = Namespace.CopyLists(namespace_src, namespace_dst, name_src, name_dst);
            }
            else if (item_type == "timers")
            {
                copy_ok = Namespace.CopyTimers(namespace_src, namespace_dst, name_src, name_dst);
            }
            else
            {
                throw new IllegalArgumentException($"{cmd} items kind must be either: 'all', 'alias', 'lists', 'timers'  ");
            }
            return copy_ok;
        }



        /// <summary>
        /// targettype (graphic) [color] [range]
        /// </summary>
        private static bool TargetType(string command, Argument[] args, bool quiet, bool force)
        {
            // targettype (graphic) [color] [range]
            if (args.Length == 0) { WrongParameterCount(command, 1, 0);}
            var graphic = args[0].AsInt();
            var color = (args.Length >=2 ? args[1].AsInt() : -1);
            var range = (args.Length >=3 ? args[2].AsInt() : Player.Backpack.Serial);

            Item itm = null;
            // Container (Range: Container Serial)
            if (range > 18)
            {
                itm = Items.FindByID(graphic, color, -1, range);
            }
            else
            {
                var options = new Items.Filter();
                options.Graphics.Add( graphic );
                if (color != -1)
                    options.Hues.Add( color );
                options.RangeMin = -1;
                options.RangeMax = range;
                options.OnGround = 1;


                var item_list = Items.ApplyFilter(options);
                if (item_list.Count > 0) {
                    item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1) );
                    itm = item_list[0];
                }
            }

            if (itm == null)
            {
                if (!quiet) { Misc.SendMessage("targettype: graphic "+ graphic.ToString() + " not found in range " + range.ToString() ); }
            }
            else {
                RazorEnhanced.Target.TargetExecute(itm);
            }

            return true;
        }

        /// <summary>
        /// targetground (graphic) [color] [range]
        /// </summary>
        private static bool TargetGround(string command, Argument[] args, bool quiet, bool force)
        {
            // targettype (graphic) [color] [range]
            if (args.Length == 0) { WrongParameterCount(command, 1, 0); }
            var graphic = args[0].AsInt();
            var color = (args.Length >= 2 ? args[1].AsInt() : -1);
            var range = (args.Length >= 3 ? args[2].AsInt() : -1);

            Item itm = null;
            var options = new Items.Filter();
            options.Graphics.Add(graphic);
            if (color != -1)
                options.Hues.Add(color);
            options.RangeMin = -1;
            options.RangeMax = range;
            options.OnGround = 1;


            var item_list = Items.ApplyFilter(options);
            if (item_list.Count > 0)
            {
                item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                itm = item_list[0];
            }


            if (itm == null)
            {
                if (!quiet) { Misc.SendMessage("targettype: graphic " + graphic.ToString() + " not found in range " + range.ToString()); }
            }
            else
            {
                RazorEnhanced.Target.TargetExecute(itm);
            }

            return true;
        }

        internal static int[] LastTileTarget = new int[4] {0, 0, 0, 0};

        /// <summary>
        ///  targettile ('last'/'current'/(x y z)) [graphic]
        /// </summary>
        private static bool TargetTile(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 1) { WrongParameterCount(command, 1, args.Length); }
            if (args.Length == 2 || args.Length == 4) // then graphic specified just use it
            {
                int graphic = 0;
                if (args.Length == 2)
                    graphic = args[1].AsInt();
                if (args.Length == 4)
                    graphic = args[3].AsInt();
                var options = new Items.Filter();
                options.Graphics.Add(graphic);
                options.OnGround = 1;
                var item_list = Items.ApplyFilter(options);
                Item itm = null;
                if (item_list.Count > 0)
                {
                    item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                    itm = item_list[0];
                }
                if (itm == null)
                {
                    if (!quiet) { Misc.SendMessage("targettile: graphic " + graphic.ToString() + " not found"); }
                }
                else
                {
                    LastTileTarget[0] = itm.Position.X;
                    LastTileTarget[1] = itm.Position.Y;
                    LastTileTarget[2] = itm.Position.Z;
                    var tiles2 = Statics.GetStaticsTileInfo(LastTileTarget[0], LastTileTarget[1], Player.Map);
                    if (tiles2.Count > 0)
                    {
                        LastTileTarget[2] = tiles2[0].StaticZ;
                        LastTileTarget[3] = tiles2[0].StaticID;
                    }
                    RazorEnhanced.Target.TargetExecute(itm);
                }
                return true;
            }
            // if we get here graphic wasnt specified


            if (args[0].AsString() == "current")
            {
                LastTileTarget[0] = Player.Position.X;
                LastTileTarget[1] = Player.Position.Y;
                LastTileTarget[2] = Player.Position.Z;
            }

            if (args.Length == 3)
            {
                LastTileTarget[0] = args[0].AsInt();
                LastTileTarget[1] = args[1].AsInt();
                LastTileTarget[2] = args[2].AsInt();
            }
            var tiles = Statics.GetStaticsTileInfo(LastTileTarget[0], LastTileTarget[1], Player.Map);
            if (tiles.Count > 0)
            {
                LastTileTarget[2] = tiles[0].StaticZ;
                LastTileTarget[3] = tiles[0].StaticID;
            }

            RazorEnhanced.Target.TargetExecute(LastTileTarget[0], LastTileTarget[1], LastTileTarget[2], LastTileTarget[3]);
            return true;
        }

        /// <summary>
        /// targettileoffset (x y z) [graphic]
        /// </summary>
        private static bool TargetTileOffset(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length < 3) { WrongParameterCount(command, 3, args.Length); }
            if (args.Length == 4) // then graphic specified just use it
            {
                int graphic = 0;
                if (args.Length == 4)
                    graphic = args[3].AsInt();
                var options = new Items.Filter();
                options.Graphics.Add(graphic);
                options.OnGround = 1;
                var item_list = Items.ApplyFilter(options);
                Item itm = null;
                if (item_list.Count > 0)
                {
                    item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                    itm = item_list[0];
                }
                if (itm == null)
                {
                    if (!quiet) { Misc.SendMessage("targettile: graphic " + graphic.ToString() + " not found"); }
                }
                else
                {
                    LastTileTarget[0] = itm.Position.X;
                    LastTileTarget[1] = itm.Position.Y;
                    LastTileTarget[2] = itm.Position.Z;
                    var tiles2 = Statics.GetStaticsTileInfo(LastTileTarget[0], LastTileTarget[1], Player.Map);
                    if (tiles2.Count > 0)
                    {
                        LastTileTarget[2] = tiles2[0].StaticZ;
                        LastTileTarget[3] = tiles2[0].StaticID;
                    }
                    RazorEnhanced.Target.TargetExecute(itm);
                }
                return true;
            }
            // if we get here graphic wasnt specified
            LastTileTarget[0] = Player.Position.X + args[0].AsInt();
            LastTileTarget[1] = Player.Position.Y + args[1].AsInt();
            LastTileTarget[2] = Player.Position.Z + args[2].AsInt();
            var tiles = Statics.GetStaticsTileInfo(LastTileTarget[0], LastTileTarget[1], Player.Map);
            if (tiles.Count > 0)
            {
                LastTileTarget[2] = tiles[0].StaticZ;
                LastTileTarget[3] = tiles[0].StaticID;
            }

            RazorEnhanced.Target.TargetExecute(LastTileTarget[0], LastTileTarget[1], LastTileTarget[2], LastTileTarget[3]);
            return true;
        }
        /// <summary>
        /// targettilerelative (serial) (range) [reverse = 'true' or 'false'] [graphic]
        /// </summary>
        private static bool TargetTileRelative(string command, Argument[] args, bool quiet, bool force)
        {
            // targettilerelative   (serial) (range) [reverse = 'true' or 'false'] [graphic]
            if (args.Length < 2) { WrongParameterCount(command, 2, args.Length); }
            uint serial = args[0].AsSerial();
            int range = args[1].AsInt();
            bool reverse = false;
            int graphic = -1;
            if (args.Length == 3)
            {
                try
                {
                    reverse = args[2].AsBool();
                }
                catch (RunTimeError)
                {
                    // Maybe it was a graphic
                    graphic = args[2].AsInt();
                }
            }
            if (args.Length == 4)
            {
                    reverse = args[2].AsBool();
                    graphic = args[3].AsInt();
            }

            Item itm = null;
            if (graphic != -1)
            {
                var options = new Items.Filter();
                options.Graphics.Add(graphic);
                options.RangeMax = range;
                options.OnGround = 1;
                var item_list = Items.ApplyFilter(options);
                if (item_list.Count > 0)
                {
                    item_list.Sort((a, b) => (Player.DistanceTo(a) > Player.DistanceTo(b) ? 1 : -1));
                    itm = item_list[0];
                }
                if (itm == null)
                {
                    if (!quiet)
                    {
                        Misc.SendMessage("targettilerelative: graphic " + graphic.ToString() + " not found in range " + range.ToString());
                    }
                }
                else
                {
                    RazorEnhanced.Target.TargetExecute(itm);
                }
                return true;
            }

            if (reverse)
                range = 0 - range;
            RazorEnhanced.Target.TargetExecuteRelative((int)serial, range);

            return true;
        }

        /// <summary>
        /// war (on/off)
        /// </summary>
        private static bool WarMode(string command, Argument[] args, bool quiet, bool force)
        {
            // warmode on/off
            if (args.Length != 1)
            {
                WrongParameterCount(command, 1, args.Length);
            }
            bool onOff = args[0].AsBool();
            Player.SetWarMode(onOff);

            return true;
        }

        /// <summary>
        ///cleartargetqueue
        /// </summary>
        private static bool ClearTargetQueue(string command, Argument[] args, bool quiet, bool force)
        {
            RazorEnhanced.Target.ClearQueue();
            return true;
        }

        /// <summary>
        ///  settimer ('timer name') (milliseconds)
        /// </summary>
        private bool SetTimer(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 2)
            {
                WrongParameterCount(command, 2, args.Length);
            }

            m_Interpreter.SetTimer(args[0].AsString(), args[1].AsInt());
            return true;
        }

        /// <summary>
        ///  removetimer ('timer name')
        /// </summary>
        private bool RemoveTimer(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 1)
            {
                WrongParameterCount(command, 1, args.Length);
            }

            m_Interpreter.RemoveTimer(args[0].AsString());
            return true;
        }

        /// <summary>
        ///  createtimer ('timer name')
        /// </summary>
        private bool CreateTimer(string command, Argument[] args, bool quiet, bool force)
        {
            if (args.Length != 1)
            {
                WrongParameterCount(command, 1, args.Length);
            }

            m_Interpreter.CreateTimer(args[0].AsString());
            return true;
        }

        /// <summary>
        /// shownames NOT IMPLEMENTED 
        /// </summary>
        /* shownames ['mobiles'/'corpses'] */
        private static bool ShowNames(string command, Argument[] args, bool quiet, bool force)
        {
            return NotImplemented(command, args, quiet, force);
        }



        #endregion


    }


    #region Parser/Interpreter

    public class RunTimeError : Exception
    {
        public ASTNode Node;

        public static String BuildErrorMessage(ASTNode node, string error) {
            string msg = string.Format("Error:\t{0}\n", error);
            if (node != null)
            {
                msg += String.Format("Type:\t{0}\n", node.Type);
                msg += String.Format("Word:\t{0}\n", node.Lexeme);
                msg += String.Format("Line:\t{0}\n", node.LineNumber + 1);
                msg += String.Format("Code:\t{0}\n", node.Lexer.GetLine(node.LineNumber));
            }
            return msg;
        }

        public RunTimeError(ASTNode node, string error) : base(BuildErrorMessage(node, error))
        {
            Node = node;
        }
    }

    internal static class TypeConverter
    {
        public static int ToInt(string token)
        {
            int val;

            token = token.Replace("(", "").Replace(")", "");  // get rid of ( or ) if its there
            if (token.StartsWith("0x"))
            {
                if (int.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber, Interpreter.Culture, out val))
                    return val;
            }
            else if (int.TryParse(token, out val))
                return val;

            throw new RunTimeError(null, "Cannot convert argument to int");
        }

        public static uint ToUInt(string token)
        {
            uint val;

            if (token.StartsWith("0x"))
            {
                if (uint.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber, Interpreter.Culture, out val))
                    return val;
            }
            else if (uint.TryParse(token, out val))
                return val;

            throw new RunTimeError(null, "Cannot convert " + token + " argument to uint");
        }

        public static ushort ToUShort(string token)
        {
            ushort val;

            if (token.StartsWith("0x"))
            {
                if (ushort.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber, Interpreter.Culture, out val))
                    return val;
            }
            else if (ushort.TryParse(token, out val))
                return val;

            throw new RunTimeError(null, "Cannot convert argument to ushort");
        }

        public static double ToDouble(string token)
        {
            double val;

            if (double.TryParse(token, out val))
                return val;

            throw new RunTimeError(null, "Cannot convert argument to double");
        }

        public static bool ToBool(string token)
        {
            bool val;
            switch (token)
            {
                case "on":
                    token = "true";
                    break;
                case "off":
                    token = "false";
                    break;
            }
            if (bool.TryParse(token, out val))
                return val;

            throw new RunTimeError(null, "Cannot convert argument to bool");
        }
    }

    public class Scope
    {
        private readonly Dictionary<string, Argument> _namespace = new Dictionary<string, Argument>();

        public readonly ASTNode StartNode;
        public readonly Scope Parent;
        public System.Diagnostics.Stopwatch timer;

        public Scope(Scope parent, ASTNode start)
        {
            Parent = parent;
            StartNode = start;
            timer = new System.Diagnostics.Stopwatch();
        }

        public Argument GetVar(string name)
        {
            Argument arg;

            if (_namespace.TryGetValue(name, out arg))
                return arg;

            return null;
        }

        public void SetVar(string name, Argument val)
        {
            _namespace[name] = val;
        }

        public void ClearVar(string name)
        {
            _namespace.Remove(name);
        }
    }

    public class Argument
    {
        internal ASTNode Node
        {
            get; set;
        }
        
        internal Script _script
        {
            get; set;
        }

        public Argument(Script script, ASTNode node)
        {
            Node = node;
            _script = script;
        }
    
        // Treat the argument as an integer
        public int AsInt()
        {
            if (Node.Lexeme == null)
                throw new RunTimeError(Node, $"Cannot convert argument to int: {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsInt();

            if (_script.Engine.Interpreter.FindAlias(Node.Lexeme))
            {
                int value = (int)_script.Engine.Interpreter.GetAlias(Node.Lexeme);
                return value;
            }
            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsInt();
            return TypeConverter.ToInt(Node.Lexeme);
        }

        // Treat the argument as an unsigned integer
        public uint AsUInt()
        {
            if (Node.Lexeme == null)
                throw new RunTimeError(Node, $"Cannot convert argument to uint: {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsUInt();

            if (_script.Engine.Interpreter.FindAlias(Node.Lexeme))
            {
                uint value = _script.Engine.Interpreter.GetAlias(Node.Lexeme);
                return value;
            }

            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsUInt();
            return TypeConverter.ToUInt(Node.Lexeme);
        }

        public ushort AsUShort()
        {
            if (Node.Lexeme == null)
                throw new RunTimeError(Node, $"Cannot convert argument to ushort {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsUShort();

            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsUShort();

            return TypeConverter.ToUShort(Node.Lexeme);
        }

        // Treat the argument as a serial or an alias. Aliases will
        // be automatically resolved to serial numbers.
        public uint AsSerial()
        {
            if (Node.Lexeme == null)
                throw new RunTimeError(Node, $"Cannot convert argument to serial {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsSerial();

            // Resolve it as a global alias next
            if (_script.Engine.Interpreter.FindAlias(Node.Lexeme))
            {
                uint serial = _script.Engine.Interpreter.GetAlias(Node.Lexeme);
                return serial;
            }

            try
            {
                arg = CheckIsListElement(Node.Lexeme);
                if (arg != null)
                    return arg.AsUInt();
                return AsUInt();
            }
            catch (RunTimeError)
            {
                // invalid numeric
            }
            try
            {
                arg = CheckIsListElement(Node.Lexeme);
                if (arg != null)
                    return (uint)arg.AsInt();
                return (uint)AsInt();
            }
            catch (RunTimeError)
            {
                // invalid numeric
            }
            // This is a bad place to be
            return 0;
        }

        // Treat the argument as a string
        public string AsString()
        {
            if (Node.Lexeme == null)
                throw new RunTimeError(Node, $"Cannot convert argument to string {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsString();

            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsString();

            return Node.Lexeme;
        }

        internal Argument CheckIsListElement(string token)
        {
            Regex rx = new Regex(@"(\S+)\[(\d+)\]");
            Match match = rx.Match(token);
            if (match.Success)
            {
                string list = match.Groups[1].Value;
                int index = int.Parse(match.Groups[2].Value);
                if (_script.Engine.Interpreter.ListExists(list))
                {
                    return _script.Engine.Interpreter.GetListValue(list, index);
                }
            }
            return null;
        }

        public bool AsBool()
        {
            if (Node.Lexeme == null)
                throw new RunTimeError(Node, $"Cannot convert argument to bool {Node.LineNumber}");

            // Try to resolve it as a scoped variable first
            var arg = _script.Lookup(Node.Lexeme);
            if (arg != null)
                return arg.AsBool();

            arg = CheckIsListElement(Node.Lexeme);
            if (arg != null)
                return arg.AsBool();


            return TypeConverter.ToBool(Node.Lexeme);
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
                return false;

            Argument arg = obj as Argument;

            if (arg == null)
                return false;

            return Equals(arg);
        }

        public bool Equals(Argument other)
        {
            if (other == null)
                return false;

            return (other.Node.Lexeme == Node.Lexeme);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
    public delegate bool UOSTracebackDelegate(Script script, ASTNode node, Scope scope);
    public class Script
    {
        bool Debug { get; set; }
        public string Filename;
        public UOSteamEngine Engine;
        Action<string> debugWriter;
            
        public event UOSTracebackDelegate OnTraceback;

        private ASTNode _root;
        private ASTNode _statement;

        private Scope _scope;

        public Script()
        {
            Debug = false;
        }

        public Argument Lookup(string name)
        {
            var scope = _scope;
            Argument result = null;

            while (scope != null)
            {
                result = scope.GetVar(name);
                if (result != null)
                    return result;

                scope = scope.Parent;
            }

            return result;
        }

        private void PushScope(ASTNode node)
        {
            _scope = new Scope(_scope, node);
        }

        private void PopScope()
        {
            _scope = _scope.Parent;
        }

        internal Scope CurrentScope()
        {
            return _scope;
        }

        private Argument[] ConstructArguments(ref ASTNode node)
        {
            List<Argument> args = new List<Argument>();

            node = node.Next();

            bool comment = false;
            while (node != null)
            {
                switch (node.Type)
                {
                    case ASTNodeType.AND:
                    case ASTNodeType.OR:
                    case ASTNodeType.EQUAL:
                    case ASTNodeType.NOT_EQUAL:
                    case ASTNodeType.LESS_THAN:
                    case ASTNodeType.LESS_THAN_OR_EQUAL:
                    case ASTNodeType.GREATER_THAN:
                    case ASTNodeType.GREATER_THAN_OR_EQUAL:
                        return args.ToArray();
                }
                if (node.Lexeme == "//")
                    comment = true;

                if (! comment)
                    args.Add(new Argument(this, node));

                node = node.Next();
            }

            return args.ToArray();
        }

        // For now, the scripts execute directly from the
        // abstract syntax tree. This is relatively simple.
        // A more robust approach would be to "compile" the
        // scripts to a bytecode. That would allow more errors
        // to be caught with better error messages, as well as
        // make the scripts execute more quickly.
        public Script(ASTNode root, Action<string> writer, UOSteamEngine engine)
        {
            debugWriter = writer;
            // Set current to the first statement
            _root = root;
            Engine = engine;
            Init();
        }
            
        public void Init(){
            _statement = _root.FirstChild();
            _scope = new Scope(null, _statement);
                
        }

        public bool ExecuteNext()
        {
            if (_statement == null) {
                Init();
            }
            if (_statement == null) { return false;}

            if (_statement.Type != ASTNodeType.STATEMENT)
                throw new RunTimeError(_statement, "Invalid script");
                
            var node = _statement.FirstChild();

            if (OnTraceback!=null && !OnTraceback.Invoke(this, node, _scope)){
                return false;
            }

            if (node == null)
                throw new RunTimeError(_statement, "Invalid statement");



            int depth;
            switch (node.Type)
            {
                case ASTNodeType.IF:
                    {
                        PushScope(node);

                        var expr = node.FirstChild();
                        var result = EvaluateExpression(ref expr);

                        // Advance to next statement
                        Advance();

                        // Evaluated true. Jump right into execution.
                        if (result)
                            break;

                        // The expression evaluated false, so keep advancing until
                        // we hit an elseif, else, or endif statement that matches
                        // and try again.
                        depth = 0;

                        while (_statement != null)
                        {
                            node = _statement.FirstChild();

                            if (node.Type == ASTNodeType.IF)
                            {
                                depth++;
                            }
                            else if (node.Type == ASTNodeType.ELSEIF)
                            {
                                if (depth == 0)
                                {
                                    expr = node.FirstChild();
                                    result = EvaluateExpression(ref expr);

                                    // Evaluated true. Jump right into execution
                                    if (result)
                                    {
                                        Advance();
                                        break;
                                    }
                                }
                            }
                            else if (node.Type == ASTNodeType.ELSE)
                            {
                                if (depth == 0)
                                {
                                    // Jump into the else clause
                                    Advance();
                                    break;
                                }
                            }
                            else if (node.Type == ASTNodeType.ENDIF)
                            {
                                if (depth == 0)
                                    break;

                                depth--;
                            }

                            Advance();
                        }

                        if (_statement == null)
                            throw new RunTimeError(node, "If with no matching endif");

                        break;
                    }
                case ASTNodeType.ELSEIF:
                    // If we hit the elseif statement during normal advancing, skip over it. The only way
                    // to execute an elseif clause is to jump directly in from an if statement.
                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.IF)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.ENDIF)
                        {
                            if (depth == 0)
                                break;

                            depth--;
                        }

                        Advance();
                    }

                    if (_statement == null)
                        throw new RunTimeError(node, "If with no matching endif");

                    break;
                case ASTNodeType.ENDIF:
                    PopScope();
                    Advance();
                    break;
                case ASTNodeType.ELSE:
                    // If we hit the else statement during normal advancing, skip over it. The only way
                    // to execute an else clause is to jump directly in from an if statement.
                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.IF)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.ENDIF)
                        {
                            if (depth == 0)
                                break;

                            depth--;
                        }

                        Advance();
                    }

                    if (_statement == null)
                        throw new RunTimeError(node, "If with no matching endif");

                    break;
                case ASTNodeType.WHILE:
                    {
                        // When we first enter the loop, push a new scope
                        if (_scope.StartNode != node)
                        {
                            PushScope(node);
                        }
                        _scope.timer.Start();
                        var expr = node.FirstChild();
                        var result = EvaluateExpression(ref expr);

                        // Advance to next statement
                        Advance();

                        // The expression evaluated false, so keep advancing until
                        // we hit an endwhile statement.
                        if (!result)
                        {
                            depth = 0;

                            while (_statement != null)
                            {
                                node = _statement.FirstChild();

                                if (node.Type == ASTNodeType.WHILE)
                                {
                                    depth++;
                                }
                                else if (node.Type == ASTNodeType.ENDWHILE)
                                {
                                    if (depth == 0)
                                    {
                                        int duration = (int)_scope.timer.ElapsedMilliseconds;
                                        const int minimumLoopDelay = 500;
                                        if (duration < minimumLoopDelay)
                                            Misc.Pause(minimumLoopDelay - duration);
                                        _scope.timer.Reset();
                                        PopScope();
                                        // Go one past the endwhile so the loop doesn't repeat
                                        Advance();
                                        break;
                                    }

                                    depth--;
                                }

                                Advance();
                            }
                        }
                        break;
                    }
                case ASTNodeType.ENDWHILE:
                    // Walk backward to the while statement
                    bool curDebug = Debug;
                    int whileStmnts = 0;
                    Debug = false; // dont print our internal movement

                    _statement = _statement.Prev();

                    depth = 0;

                    while (_statement != null)
                    {
                        whileStmnts++;
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.ENDWHILE)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.WHILE)
                        {
                            if (depth == 0)
                                break;

                            depth--;
                        }

                        _statement = _statement.Prev();
                    }

                    var duration2 = _scope.timer.ElapsedMilliseconds;
                    if (duration2 < 500)
                        Misc.Pause((int)(500 - duration2));
                    _scope.timer.Reset();
                    Debug = curDebug;
                    if (_statement == null)
                        throw new RunTimeError(node, "Unexpected endwhile");

                    break;
                case ASTNodeType.FOR:
                    {
                        // The iterator variable's name is the hash code of the for loop's ASTNode.
                        var iterName = node.GetHashCode().ToString(); // + "_" + node.LineNumber.ToString();

                        // When we first enter the loop, push a new scope
                        if (_scope.StartNode != node)
                        {
                            PushScope(node);
                            _scope.timer.Start();
                            // Grab the arguments
                            var max = node.FirstChild();

                            if (max.Type != ASTNodeType.INTEGER)
                                throw new RunTimeError(max, "Invalid for loop syntax");

                            // Create a dummy argument that acts as our loop variable
                            var iter = new ASTNode(ASTNodeType.INTEGER, "0", node, 0);

                            _scope.SetVar(iterName, new Argument(this, iter));
                        }
                        else
                        {
                            // Increment the iterator argument
                            var arg = _scope.GetVar(iterName);

                            var iter = new ASTNode(ASTNodeType.INTEGER, (arg.AsUInt() + 1).ToString(), node, 0);

                            _scope.SetVar(iterName, new Argument(this, iter));
                        }

                        // Check loop condition
                        var i = _scope.GetVar(iterName);

                        // Grab the max value to iterate to
                        node = node.FirstChild();
                        var end = new Argument(this, node);

                        if (i.AsUInt() < end.AsUInt())
                        {
                            // enter the loop
                            Advance();
                        }
                        else
                        {
                            // Walk until the end of the loop
                            Advance();

                            depth = 0;

                            while (_statement != null)
                            {
                                node = _statement.FirstChild();

                                if (node.Type == ASTNodeType.FOR || node.Type == ASTNodeType.FOREACH)
                                {
                                    depth++;
                                }
                                else if (node.Type == ASTNodeType.ENDFOR)
                                {

                                    if (depth == 0)
                                    {
                                        PopScope();
                                        // Go one past the end so the loop doesn't repeat
                                        Advance();
                                        break;
                                    }
                                    depth--;

                                }

                                Advance();
                            }
                        }
                    }
                    break;
                case ASTNodeType.FOREACH:
                    {
                        /*Dalamar
                        ORIGINAL UOS Behaviour:
                        All list iteration start from 0 and provide in list_name[] the first item of the list while in the forloop.
                        The start/end parameter simply "cap" the list size to a lower value as folloing:

                        for (start) to ('list name')
                        n_interations = list_length - start

                        for (start) to (end) in ('list name')
                        n_interations = ( end - start )

                        this second case falls back to the first.


                        PS: feels like writing intentionally broken code, but it's 100% UOSteam behaviour
                        */

                        // The iterator's name is the hash code of the for loop's ASTNode.
                        var children = node.Children();
                        ASTNode startParam = children[0];
                        ASTNode endParam = null;
                        ASTNode listParam;
                        if (children.Length == 2)
                        {
                            listParam = children[1];
                        }
                        else {
                            endParam = children[1];
                            listParam = children[2];
                        }


                        string listName = listParam.Lexeme;
                        int listSize = Engine.Interpreter.ListLength(listName);

                        string iterName = listParam.GetHashCode().ToString(); //+"_"+ listParam.LineNumber.ToString();
                        string varName = listName + "[]";
                        int start = int.Parse(startParam.Lexeme);


                        int num_iter;
                        if (endParam == null)
                        {
                            num_iter = listSize - start;
                        }
                        else
                        {
                            int end = int.Parse(endParam.Lexeme) + 1; // +1 is important
                            if (end > listSize)
                            {
                                throw new RunTimeError(node, "Invalid for loop: END parameter must be smaller then the list size (" + listSize + "), " + (end - 1) + " given ");
                            }
                            num_iter = end - start;
                        }


                        if (num_iter > 0)
                        {

                            var idx = 0;
                            // When we first enter the loop, push a new scope
                            if (_scope.StartNode != node)
                            {
                                PushScope(node);
                                // Create a dummy argument that acts as our iterator object
                                var iter = new ASTNode(ASTNodeType.INTEGER, idx.ToString(), node, 0);
                                _scope.SetVar(iterName, new Argument(this, iter));

                                // Make the user-chosen variable have the value for the front of the list
                                var arg = Engine.Interpreter.GetListValue(listName, 0);

                                if (arg == null || 0 >= num_iter)
                                    _scope.ClearVar(varName);
                                else
                                    _scope.SetVar(varName, arg);
                            }
                            else
                            {
                                // Increment the iterator argument
                                idx = _scope.GetVar(iterName).AsInt() + 1;
                                var iter = new ASTNode(ASTNodeType.INTEGER, idx.ToString(), node, 0);
                                _scope.SetVar(iterName, new Argument(this, iter));

                                // Update the user-chosen variable
                                var arg = Engine.Interpreter.GetListValue(listName, idx);

                                if (arg == null || idx >= num_iter)
                                {
                                    _scope.ClearVar(varName);
                                }
                                else
                                {
                                    _scope.SetVar(varName, arg);
                                }
                            }
                        }
                        else
                        {
                            // nothing to do - force end
                            _scope.ClearVar(varName);
                        }

                        // Check loop condition
                        var i = _scope.GetVar(varName);

                        if (i != null)
                        {
                            // enter the loop
                            Advance();
                        }
                        else
                        {
                            // Walk until the end of the loop
                            Advance();

                            depth = 0;

                            while (_statement != null)
                            {
                                node = _statement.FirstChild();

                                if (node.Type == ASTNodeType.FOR || node.Type == ASTNodeType.FOREACH)
                                {
                                    depth++;
                                }
                                else if (node.Type == ASTNodeType.ENDFOR)
                                {
                                    if (depth == 0)
                                    {
                                        PopScope();
                                        // Go one past the end so the loop doesn't repeat
                                        Advance();
                                        break;
                                    }

                                    depth--;
                                }

                                Advance();
                            }
                        }
                        break;
                    }
                case ASTNodeType.ENDFOR:
                    // Walk backward to the for statement
                    // track depth in case there is a nested for
                    // Walk backward to the for statement
                    curDebug = Debug;
                    Debug = false; // dont print our internal movement

                    _statement = _statement.Prev();

                    // track depth in case there is a nested for
                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.ENDFOR)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.FOR || node.Type == ASTNodeType.FOREACH)
                        {
                            if (depth == 0)
                            {
                                break;
                            }
                            depth--;
                        }

                        _statement = _statement.Prev();

                    }
                    Debug = curDebug;

                    if (_statement == null)
                        throw new RunTimeError(node, "Unexpected endfor");
                    break;
                case ASTNodeType.BREAK:
                    // Walk until the end of the loop
                    curDebug = Debug;
                    Debug = false; // dont print our internal movement
                    Advance();

                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.WHILE ||
                            node.Type == ASTNodeType.FOR ||
                            node.Type == ASTNodeType.FOREACH)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.ENDWHILE ||
                            node.Type == ASTNodeType.ENDFOR)
                        {
                            if (depth == 0)
                            {
                                PopScope();

                                // Go one past the end so the loop doesn't repeat
                                Advance();
                                break;
                            }

                            depth--;
                        }

                        Advance();
                    }

                    PopScope();
                    Debug = curDebug;
                    break;
                case ASTNodeType.CONTINUE:
                    // Walk backward to the loop statement
                    curDebug = Debug;
                    Debug = false; // dont print our internal movement

                    _statement = _statement.Prev();

                    depth = 0;

                    while (_statement != null)
                    {
                        node = _statement.FirstChild();

                        if (node.Type == ASTNodeType.ENDWHILE ||
                            node.Type == ASTNodeType.ENDFOR)
                        {
                            depth++;
                        }
                        else if (node.Type == ASTNodeType.WHILE ||
                                    node.Type == ASTNodeType.FOR ||
                                    node.Type == ASTNodeType.FOREACH)
                        {
                            if (depth == 0)
                                break;

                            depth--;
                        }

                        _statement = _statement.Prev();
                    }
                    Debug = curDebug;
                    if (_statement == null)
                        throw new RunTimeError(node, "Unexpected continue");
                    break;
                case ASTNodeType.STOP:
                    Engine.Interpreter.StopScript();
                    _statement = null;
                    throw(new UOSteamEngine.StopException());
                    break;
                case ASTNodeType.REPLAY:
                    _statement = _statement.Parent.FirstChild();
                    break;
                case ASTNodeType.QUIET:
                case ASTNodeType.FORCE:
                case ASTNodeType.COMMAND:
                    if (ExecuteCommand(node))
                        Advance();

                    break;
            }
            return (_statement != null);
        }

        public void Advance()
        {
            Engine.Interpreter.ClearTimeout();
            _statement = _statement.Next();
            if (Debug)
            {
                if (_statement != null)
                    debugWriter(String.Format("Line: {0}", _statement.LineNumber+1));
            }
        }

        private ASTNode EvaluateModifiers(ASTNode node, out bool quiet, out bool force, out bool not)
        {
            quiet = false;
            force = false;
            not = false;

            while (true)
            {
                switch (node.Type)
                {
                    case ASTNodeType.QUIET:
                        quiet = true;
                        break;
                    case ASTNodeType.FORCE:
                        force = true;
                        break;
                    case ASTNodeType.NOT:
                        not = true;
                        break;
                    default:
                        return node;
                }

                node = node.Next();
            }
        }

        private bool ExecuteCommand(ASTNode node)
        {
            node = EvaluateModifiers(node, out bool quiet, out bool force, out _);

            var cont = true;
            if (node.Lexeme.ToLower() == "debug")
            {
                var args = ConstructArguments(ref node);
                if (args.Length == 1)
                {
                    bool debugSetting = false;
                    if (args[0].AsString().ToLower() == "on")
                        debugSetting = true;
                    Debug = debugSetting;
                }
                cont = true;
            }
            else
            {
                var handler = Engine.Interpreter.GetCommandHandler(node.Lexeme);

                if (handler == null)
                    throw new RunTimeError(node, "Unknown command");

                cont = handler(node.Lexeme, ConstructArguments(ref node), quiet, force);

                if (node != null)
                    throw new RunTimeError(node, "Command did not consume all available arguments");
            }
            return cont;
        }

        private bool EvaluateExpression(ref ASTNode expr)
        {
            if (expr == null || (expr.Type != ASTNodeType.UNARY_EXPRESSION && expr.Type != ASTNodeType.BINARY_EXPRESSION && expr.Type != ASTNodeType.LOGICAL_EXPRESSION))
                throw new RunTimeError(expr, "No expression following control statement");

            var node = expr.FirstChild();

            if (node == null)
                throw new RunTimeError(expr, "Empty expression following control statement");

            switch (expr.Type)
            {
                case ASTNodeType.UNARY_EXPRESSION:
                    return EvaluateUnaryExpression(ref node);

                case ASTNodeType.BINARY_EXPRESSION:
                    return EvaluateBinaryExpression(ref node);
            }

            bool lhs = EvaluateExpression(ref node);

            node = node.Next();

            while (node != null)
            {
                // Capture the operator
                var op = node.Type;
                node = node.Next();

                if (node == null)
                    throw new RunTimeError(node, "Invalid logical expression");

                bool rhs;
                var e = node.FirstChild();
                switch (node.Type)
                {
                    case ASTNodeType.UNARY_EXPRESSION:
                        rhs = EvaluateUnaryExpression(ref e);
                        break;
                    case ASTNodeType.BINARY_EXPRESSION:
                        rhs = EvaluateBinaryExpression(ref e);
                        break;
                    default:
                        throw new RunTimeError(node, "Nested logical expressions are not possible");
                }

                switch (op)
                {
                    case ASTNodeType.AND:
                        lhs = lhs && rhs;
                        break;
                    case ASTNodeType.OR:
                        lhs = lhs || rhs;
                        break;
                    default:
                        throw new RunTimeError(node, "Invalid logical operator");
                }

                node = node.Next();
            }

            return lhs;
        }

        private bool CompareOperands(ASTNodeType op, IComparable lhs, IComparable rhs)
        {
            if (lhs.GetType() != rhs.GetType())
            {
                // Different types. Try to convert one to match the other.

                if (rhs is double)
                {
                    // Special case for rhs doubles because we don't want to lose precision.
                    lhs = (double)lhs;
                }
                else if (rhs is bool)
                {
                    // Special case for rhs bools because we want to down-convert the lhs.
                    var tmp = Convert.ChangeType(lhs, typeof(bool));
                    lhs = (IComparable)tmp;
                }
                else
                {
                    var tmp = Convert.ChangeType(rhs, lhs.GetType());
                    rhs = (IComparable)tmp;
                }
            }

            try
            {
                // Evaluate the whole expression
                switch (op)
                {
                    case ASTNodeType.EQUAL:
                        return lhs.CompareTo(rhs) == 0;
                    case ASTNodeType.NOT_EQUAL:
                        return lhs.CompareTo(rhs) != 0;
                    case ASTNodeType.LESS_THAN:
                        return lhs.CompareTo(rhs) < 0;
                    case ASTNodeType.LESS_THAN_OR_EQUAL:
                        return lhs.CompareTo(rhs) <= 0;
                    case ASTNodeType.GREATER_THAN:
                        return lhs.CompareTo(rhs) > 0;
                    case ASTNodeType.GREATER_THAN_OR_EQUAL:
                        return lhs.CompareTo(rhs) >= 0;
                }
            }
            catch (ArgumentException e)
            {
                throw new RunTimeError(null, e.Message);
            }

            throw new RunTimeError(null, "Unknown operator in expression");

        }

        private bool EvaluateUnaryExpression(ref ASTNode node)
        {
            node = EvaluateModifiers(node, out bool quiet, out _, out bool ifnot);

            var handler = Engine.Interpreter.GetExpressionHandler(node.Lexeme);

            if (handler == null)
                throw new RunTimeError(node, "Unknown expression");

            var result = handler(node.Lexeme, ConstructArguments(ref node), quiet);

            if (ifnot)
                return CompareOperands(ASTNodeType.EQUAL, result, false);
            else
                return CompareOperands(ASTNodeType.EQUAL, result, true);
        }

        private bool EvaluateBinaryExpression(ref ASTNode node)
        {
            node = EvaluateModifiers(node, out bool quiet, out _, out bool ifnot);

            // Evaluate the left hand side
            var lhs = EvaluateBinaryOperand(ref node);

            // Capture the operator
            var op = node.Type;
            node = node.Next();

            // Evaluate the right hand side
            var rhs = EvaluateBinaryOperand(ref node);

            var result = CompareOperands(op, lhs, rhs);
            if (ifnot)
                return CompareOperands(ASTNodeType.EQUAL, result, false);
            else
                return CompareOperands(ASTNodeType.EQUAL, result, true);
        }

        private IComparable EvaluateBinaryOperand(ref ASTNode node)
        {
            IComparable val;

            node = EvaluateModifiers(node, out bool quiet, out _, out _);
            switch (node.Type)
            {
                case ASTNodeType.INTEGER:
                    val = TypeConverter.ToInt(node.Lexeme);
                    break;
                case ASTNodeType.SERIAL:
                    val = TypeConverter.ToUInt(node.Lexeme);
                    break;
                case ASTNodeType.STRING:
                    val = node.Lexeme;
                    break;
                case ASTNodeType.DOUBLE:
                    val = TypeConverter.ToDouble(node.Lexeme);
                    break;
                case ASTNodeType.OPERAND:
                    {
                        // This might be a registered keyword, so do a lookup
                        var handler = Engine.Interpreter.GetExpressionHandler(node.Lexeme);
                        if (handler != null)
                            val = handler(node.Lexeme, ConstructArguments(ref node), quiet);
                        else
                        {
                            Argument temp = new Argument(this, node);
                            val = temp.AsString();
                        }
                        break;
                    }
                default:
                    throw new RunTimeError(node, "Invalid type found in expression");
            }

            return val;
        }
    }

    public class Namespace {
        const string DEFAULT_NAMESPACE = "global";

        public static readonly ConcurrentDictionary<string, Namespace> _namespaces = new ConcurrentDictionary<string, Namespace>();
        public static readonly Namespace GlobalNamespace = new Namespace(DEFAULT_NAMESPACE);

        // Timers
        public readonly string Name;
        public readonly ConcurrentDictionary<string, int> _alias = new ConcurrentDictionary<string, int>();
        public readonly ConcurrentDictionary<string, DateTime> _timers = new ConcurrentDictionary<string, DateTime>();
        public readonly ConcurrentDictionary<string, List<Argument>> _lists = new ConcurrentDictionary<string, List<Argument>>();
            
        public static Namespace Get(string name=null) {
            if (name == null) { name = DEFAULT_NAMESPACE; }
            if (Has(name))
            {
                return _namespaces[name];
            }
            else { 
                var ns = new Namespace(name);
                return ns;
            }
        }

        public static List<string> List() {
            return _namespaces.Keys.ToList();
        }

        public static bool Has(string name)
        {
            if (name == Namespace.GlobalNamespace.Name) return true;
            return _namespaces.ContainsKey(name);
        }

        public static void Delete(string name=null)
        {
            if (name==null) { name = DEFAULT_NAMESPACE; }

            if (name == Namespace.GlobalNamespace.Name)
            {
                Misc.SharedScriptData.Clear();
                GlobalNamespace._alias.Clear();
                GlobalNamespace._lists.Clear();
                GlobalNamespace._timers.Clear();
            }
            else if (Has(name))
            {
                _namespaces.TryRemove(name, out var _);
            }
        }

        public static string PrintAll(string namespace_name = null, string item_name = null)
        {
            var nss = namespace_name == null ? _namespaces.Keys.ToList():new List<string>{ namespace_name };

            var content = "";
            foreach(var ns in nss)
            {
                var contentAlias = PrintAlias(namespace_name, item_name);
                var contentLists = PrintLists(namespace_name, item_name);
                var contentTimers = PrintTimers(namespace_name, item_name);
                content += contentAlias + contentLists + contentTimers; 
            }

            return content;
        }
        public static string PrintAlias(string namespace_name, string item_name = null)
        {
            if (!Has(namespace_name)) return "";
            var content = "";
            var ns = Get(namespace_name);
            if (item_name == null)
            {
                var pairs = ns._alias.ToSortedList((a, b) => { return a.Key.CompareTo(b.Key); });
                foreach (var pair in pairs)
                { content += $"{namespace_name}:alias:{pair.Key} = {pair.Value}\n"; }
                if (content == "") { content = $"{namespace_name}:alias: -EMPTY-\n"; }
            }
            else
            {
                var found = ns._alias.TryGetValue(item_name, out var value);
                content += $"{namespace_name}:alias:{item_name}{(found?$" = {value}":" NOT found")}\n";
            }

            return content;
        }

        public static string PrintLists(string namespace_name, string item_name = null)
        {
            if (!Has(namespace_name)) return "";
            var content = "";
            var ns = Get(namespace_name);
            if (item_name == null)
            {
                var pairs = ns._lists.ToSortedList((a, b) => { return a.Key.CompareTo(b.Key); });
                foreach (var pair in pairs)
                { content += $"{namespace_name}:lists:{pair.Key} = {string.Join(", ",pair.Value.Apply((a)=>a.AsString()))}\n"; }
                if (content == "") { content = $"{namespace_name}:lists: -EMPTY-\n"; }
            }
            else
            {
                var found = ns._lists.TryGetValue(item_name, out var value);
                content += $"{namespace_name}:lists:{item_name} {(found ? $" = {value}" : "NOT found.")}\n";
            }
            return content;
        }

        public static string PrintTimers(string namespace_name, string item_name = null)
        {
            if (!Has(namespace_name)) return "";
            var content = "";
            var ns = Get(namespace_name);
            if (item_name == null)
            {
                var pairs = ns._timers.ToSortedList((a, b) => { return a.Key.CompareTo(b.Key); });
                foreach (var pair in pairs)
                { content += $"{namespace_name}:timers:{pair.Key} = {pair.Value}\n"; }
                if (content == "") { content = $"{namespace_name}:timers: -EMPTY-\n"; }
            }
            else
            {
                var found = ns._timers.TryGetValue(item_name, out var value);
                content += $"{namespace_name}:timers:{item_name} {(found ? $" = {value}" : "NOT found.")}\n";
            }
            return content;
        }

        public static bool CopyAll(string namespace_src, string namespace_dst, string name_src = null, string name_dst = null) {
            var alias_ok = CopyAlias(namespace_src, namespace_dst, name_src, name_dst);
            var lists_ok = CopyLists(namespace_src, namespace_dst, name_src, name_dst);
            var timers_ok = CopyTimers(namespace_src, namespace_dst, name_src, name_dst);
            return alias_ok && lists_ok && timers_ok;
        }

        public static bool CopyAlias(string namespace_src, string namespace_dst, string name_src = null, string name_dst = null)
        {
            if (!Has(namespace_src)) return false;
            var src_ns = Get(namespace_src);
            var dst_ns = Get(namespace_dst);

            if (name_src == null)
            {
                dst_ns._alias.Concat(dst_ns._alias);
            }
            else
            {
                if (name_dst == null) { name_dst = name_src; }
                dst_ns._alias.TryAdd(name_dst, dst_ns._alias[name_src]);
            }
            return true;
        }

        public static bool CopyLists(string namespace_src, string namespace_dst, string name_src = null, string name_dst = null)
        {
            if (!Has(namespace_src)) return false;
            var src_ns = Get(namespace_src);
            var dst_ns = Get(namespace_dst);

            if (name_src == null)
            {
                dst_ns._lists.Concat(dst_ns._lists);
            }
            else
            {
                if (name_dst == null) { name_dst = name_src; }
                dst_ns._lists.TryAdd(name_dst, dst_ns._lists[name_src]);
            }
            return true;
        }

        public static bool CopyTimers(string namespace_src, string namespace_dst, string name_src = null, string name_dst = null)
        {
            if (!Has(namespace_src)) return false;
            var src_ns = Get(namespace_src);
            var dst_ns = Get(namespace_dst);

            if (name_src == null)
            {
                dst_ns._timers.Concat(dst_ns._timers);
            }
            else
            {
                if (name_dst == null) { name_dst = name_src; }
                dst_ns._timers.TryAdd(name_dst, dst_ns._timers[name_src]);
            }
            return true;
        }
            
        public static bool Move(string oldname, string newname, bool replace=false) {               
            if (!Has(oldname)) { return false; }

            if (replace){
                Delete(newname);
                var new_ns = Get(newname);
                var old_ns = _namespaces[oldname];
            }
            CopyAll(oldname, newname);
            Delete(oldname);

            return true;
        }

        private Namespace(string name) { 
            Name = name;
            _namespaces.TryAdd(name, this);
        }

    }

    public class Interpreter
    {
        internal static readonly Dictionary<string, AliasHandler> _aliasHandlers = new Dictionary<string, AliasHandler>();

        // Delegates
        //public delegate T ExpressionHandler<T>(string expression, Argument[] args, bool quiet) where T : IComparable;
        public delegate IComparable ExpressionHandler(string expression, Argument[] args, bool quiet);
        public delegate bool CommandHandler(string command, Argument[] args, bool quiet, bool force);
        public delegate uint AliasHandler(string alias);

        internal readonly ConcurrentDictionary<string, ExpressionHandler> _exprHandlers = new ConcurrentDictionary<string, ExpressionHandler>();
        internal readonly ConcurrentDictionary<string, CommandHandler> _commandHandlers = new ConcurrentDictionary<string, CommandHandler>();

        // Timers

        internal ConcurrentDictionary<string, int> _alias { get { return m_Engine.Namespace._alias; } }
        internal ConcurrentDictionary<string, DateTime> _timers { get { return m_Engine.Namespace._timers; } }
        internal ConcurrentDictionary<string, List<Argument>> _lists { get { return m_Engine.Namespace._lists; } }
        
        
            // Lists
        private UOSteamEngine m_Engine;
        private Script m_Script = null;
            
        private bool m_Suspended = false;
        private ManualResetEvent m_SuspendedMutex;
            

        private enum ExecutionState
        {
            RUNNING,
            PAUSED,
            SUSPENDED,
            TIMING_OUT
        };

        public delegate bool TimeoutCallback();

        private ExecutionState _executionState = ExecutionState.RUNNING;
        private static long _pauseTimeout = long.MaxValue;
        private static TimeoutCallback _timeoutCallback = null;

        public static System.Globalization.CultureInfo Culture;

        static Interpreter()
        {
            Culture = new System.Globalization.CultureInfo(System.Globalization.CultureInfo.CurrentCulture.LCID, false);
            Culture.NumberFormat.NumberDecimalSeparator = ".";
            Culture.NumberFormat.NumberGroupSeparator = ","; 
        }

        public Interpreter(UOSteamEngine engine) {
            m_Engine = engine;
            m_SuspendedMutex = new ManualResetEvent(!m_Suspended);
        }

        /// <summary>
        /// An adapter that lets expressions be registered as commands
        /// </summary>
        /// <param name="command">name of command</param>
        /// <param name="args">arguments passed to command</param>
        /// <param name="quiet">ignored</param>
        /// <param name="force">ignored</param>
        /// <returns></returns>
        private bool ExpressionCommand(string command, Argument[] args, bool quiet, bool force)
        {
            var handler = GetExpressionHandler(command);
            handler(command, args, false);
            return true;
        }

        public void RegisterExpressionHandler(string keyword, ExpressionHandler handler)
        {
            //_exprHandlers[keyword] = (expression, args, quiet) => handler(expression, args, quiet);
            _exprHandlers[keyword] = handler;
            RegisterCommandHandler(keyword, ExpressionCommand); // also register expressions as commands
        }

        public ExpressionHandler GetExpressionHandler(string keyword)
        {
            _exprHandlers.TryGetValue(keyword, out ExpressionHandler expression);
            return expression;
        }

        public void RegisterCommandHandler(string keyword, CommandHandler handler, string docString = "default")
        {                                                                                                                           
            _commandHandlers[keyword] = handler;
            DocItem di = new DocItem("", "UOSteamEngine", keyword, docString);
        }
        public CommandHandler GetCommandHandler(string keyword)
        {
            _commandHandlers.TryGetValue(keyword, out CommandHandler handler);

            return handler;
        }

        public void RegisterAliasHandler(string keyword, AliasHandler handler)
        {
            _aliasHandlers[keyword] = handler;
        }

        public void UnregisterAliasHandler(string keyword)
        {
            _aliasHandlers.Remove(keyword);
        }

        public uint GetAlias(string alias)
        {
            // If a handler is explicitly registered, call that.
            if (_aliasHandlers.TryGetValue(alias, out AliasHandler handler))
                return handler(alias);

            if (m_Engine.Namespace == Namespace.GlobalNamespace) { 
                // uint value;
                if (Misc.CheckSharedValue(alias))
                {
                    return (uint)Misc.ReadSharedValue(alias);
                }
            }
            if (_alias.TryGetValue(alias, out int value)) { 
                return (uint)value; 
            }

            return uint.MaxValue;
        }
        public bool FindAlias(string alias)
        {
            // If a handler is explicitly registered, call that.

            if (_aliasHandlers.TryGetValue(alias, out AliasHandler handler))
                return true;
            if (m_Engine.Namespace == Namespace.GlobalNamespace)
            {
                return Misc.CheckSharedValue(alias);
            }else
            {
                return _alias.ContainsKey(alias);
            }
        }

        public void UnSetAlias(string alias)
        {
                
            if (m_Engine.Namespace == Namespace.GlobalNamespace)
            {
                Misc.RemoveSharedValue(alias);
            }
            if (_alias.ContainsKey(alias)) {
                _alias.TryRemove(alias, out var _);
            }
                
        }
        public void SetAlias(string alias, uint serial)
        {
            if (m_Engine.Namespace == Namespace.GlobalNamespace)
            {
                Misc.SetSharedValue(alias, serial);
            }
            _alias.TryAdd(alias, (int)serial);
        }

        public void CreateList(string name)
        {
            if (_lists.ContainsKey(name))
                return;

            _lists[name] = new List<Argument>();
        }

        public void DestroyList(string name)
        {
            _lists.TryRemove(name, out var _);
        }

        public void ClearList(string name)
        {
            if (!_lists.ContainsKey(name))
                return;

            _lists[name].Clear();
        }

        public bool ListExists(string name)
        {
            return _lists.ContainsKey(name);
        }

        public bool ListContains(string name, Argument arg)
        {
            if (!_lists.ContainsKey(name))
                throw new RunTimeError(null, String.Format("ListContains {0} does not exist", name));

            return _lists[name].Contains(arg);
        }

        public List<Argument> ListContents(string name)
        {
            if (!_lists.ContainsKey(name))
                throw new RunTimeError(null, String.Format("ListContents {0} does not exist", name));

            return _lists[name];
        }


        public int ListLength(string name)
        {
            if (!_lists.ContainsKey(name))
                throw new RunTimeError(null, String.Format("ListLength {0} does not exist", name));

            return _lists[name].Count;
        }

        public void PushList(string name, Argument arg, bool front, bool unique)
        {
            if (!_lists.ContainsKey(name))
                throw new RunTimeError(null, String.Format("PushList {0} does not exist", name));

            if (unique && _lists[name].Contains(arg))
                return;

            if (front)
                _lists[name].Insert(0, arg);
            else
                _lists[name].Add(arg);
        }

        public bool PopList(string name, Argument arg)
        {
            if (!_lists.ContainsKey(name))
                throw new RunTimeError(null, String.Format("PopList {0} does not exist", name));

            return _lists[name].Remove(arg);
        }

        public bool PopList(string name, bool front)
        {
            if (!_lists.ContainsKey(name))
                throw new RunTimeError(null, String.Format("PopList {0} does not exist", name));

            var idx = front ? 0 : _lists[name].Count - 1;
            _lists[name].RemoveAt(idx);

            return _lists[name].Count > 0;
        }

        public Argument GetListValue(string name, int idx)
        {
            if (!_lists.ContainsKey(name))
                throw new RunTimeError(null, String.Format("GetListValue {0} does not exist", name));

            var list = _lists[name];

            if (idx < list.Count)
                return list[idx];

            return null;
        }

        public void CreateTimer(string name)
        {
            _timers[name] = DateTime.UtcNow;
        }

        public TimeSpan GetTimer(string name)
        {
            if (!_timers.TryGetValue(name, out DateTime timestamp))
                throw new RunTimeError(null, String.Format("GetTimer {0} does not exist", name));

            TimeSpan elapsed = DateTime.UtcNow - timestamp;

            return elapsed;
        }

        public void SetTimer(string name, int elapsed)
        {
            // Setting a timer to start at a given value is equivalent to
            // starting the timer that number of milliseconds in the past.
            _timers[name] = DateTime.UtcNow.AddMilliseconds(-elapsed);
        }

        public void RemoveTimer(string name)
        {
            _timers.TryRemove(name,out var _);
        }

        public bool TimerExists(string name)
        {
            return _timers.ContainsKey(name);
        }


        public bool Suspended
        {
            get { return m_Suspended; }
            set
            {
                if (value) { SuspendScript(); }
                else { ReseumeScript(); }
            }
        }

        public void SuspendScript()
        {
            m_SuspendedMutex.Reset();
            m_Suspended = true;
        }

        public void ReseumeScript()
        {
            m_SuspendedMutex.Set();
            m_Suspended = false;
        }

        public bool StartScript()
        {
            if (m_Script != null)
                return false;

            m_Script = m_Engine.Script;
            _executionState = ExecutionState.RUNNING;

            ExecuteScript();

            return true;
        }

        public void StopScript()
        {
            m_Script = null;
            _executionState = ExecutionState.RUNNING;
        }

        public bool ExecuteScript()
        {
            if (m_Script == null)
                return false;
            
            if (_executionState == ExecutionState.PAUSED)
            {
                if (_pauseTimeout < DateTime.UtcNow.Ticks)
                    _executionState = ExecutionState.RUNNING;
                else
                    return true;
            }
            else if (_executionState == ExecutionState.TIMING_OUT)
            {
                if (_pauseTimeout < DateTime.UtcNow.Ticks)
                {
                    if (_timeoutCallback != null)
                    {
                        if (_timeoutCallback())
                        {
                            m_Script.Advance();
                            ClearTimeout();
                        }

                        _timeoutCallback = null;
                    }

                    /* If the callback changed the state to running, continue
                        * on. Otherwise, exit.
                        */
                    if (_executionState != ExecutionState.RUNNING)
                    {
                        m_Script = null;
                        return false;
                    }
                }
            }

            if (!m_Script.ExecuteNext())
            {
                m_Script = null;
                return false;
            }

            return true;
        }

            

        // Pause execution for the given number of milliseconds
        public void Pause(long duration)
        {
            // Already paused or timing out
            if (_executionState != ExecutionState.RUNNING)
                return;

            _pauseTimeout = DateTime.UtcNow.Ticks + (duration * 10000);
            _executionState = ExecutionState.PAUSED;
        }

        // Unpause execution
        public void Unpause()
        {
            if (_executionState != ExecutionState.PAUSED)
                return;

            _pauseTimeout = 0;
            _executionState = ExecutionState.RUNNING;
        }

        // If forward progress on the script isn't made within this
        // amount of time (milliseconds), bail
        public void Timeout(long duration, TimeoutCallback callback)
        {
            // Don't change an existing timeout
            if (_executionState != ExecutionState.RUNNING)
                return;

            _pauseTimeout = DateTime.UtcNow.Ticks + (duration * 10000);
            _executionState = ExecutionState.TIMING_OUT;
            _timeoutCallback = callback;
        }

        // Clears any previously set timeout. Automatically
        // called any time the script advances a statement.
        public void ClearTimeout()
        {
            if (_executionState != ExecutionState.TIMING_OUT)
                return;

            _pauseTimeout = 0;
            _executionState = ExecutionState.RUNNING;
        }
    }

    public class SyntaxError : Exception
    {
        public ASTNode Node;
        public string Line;
        public int LineNumber;
        public string Error;

        public SyntaxError(ASTNode node, string error) : base(error)
        {
            Node = node;
            Line = null;
            LineNumber = 0;
            Error = error;
        }

        public SyntaxError(string line, int lineNumber, ASTNode node, string error) : base(error)
        {
            Line = line;
            LineNumber = lineNumber;
            Node = node;
            Error = error;
        }

        public override string ToString()
        {
            return base.ToString() + $"line {LineNumber}: {Line} error near '{Node.Lexeme}': {Error}";
        }

        public override string Message { get { return base.Message + ToString(); } }
    }

    public enum ASTNodeType
    {
        // Keywords
        IF,
        ELSEIF,
        ELSE,
        ENDIF,
        WHILE,
        ENDWHILE,
        FOR,
        FOREACH,
        ENDFOR,
        BREAK,
        CONTINUE,
        STOP,
        REPLAY,

        // Operators
        EQUAL,
        NOT_EQUAL,
        LESS_THAN,
        LESS_THAN_OR_EQUAL,
        GREATER_THAN,
        GREATER_THAN_OR_EQUAL,

        // Logical Operators
        NOT,
        AND,
        OR,

        // Value types
        STRING,
        SERIAL,
        INTEGER,
        DOUBLE,
        LIST,

        // Modifiers
        QUIET, // @ symbol
        FORCE, // ! symbol

        // Everything else
        SCRIPT,
        STATEMENT,
        COMMAND,
        OPERAND,
        LOGICAL_EXPRESSION,
        UNARY_EXPRESSION,
        BINARY_EXPRESSION,
    }

    // Abstract Syntax Tree Node
    public class ASTNode
    {
        public readonly ASTNodeType Type;
        public readonly string Lexeme;
        public readonly ASTNode Parent;
        public readonly int LineNumber;
        public readonly Lexer Lexer;

        internal LinkedListNode<ASTNode> _node;
        private LinkedList<ASTNode> _children; 

        public ASTNode(ASTNodeType type, string lexeme, ASTNode parent, int lineNumber, Lexer lexer=null)
        {
            Type = type;
            if (lexeme != null)
                Lexeme = lexeme;
            else
                Lexeme = "";
            Parent = parent;
            LineNumber = lineNumber;
            if (lexer == null) { 
                this.Lexer = parent.Lexer;
            }

        }

        public ASTNode Push(ASTNodeType type, string lexeme, int lineNumber)
        {
            var node = new ASTNode(type, lexeme, this, lineNumber);

            if (_children == null)
                _children = new LinkedList<ASTNode>();

            node._node = _children.AddLast(node);

            return node;
        }

        public ASTNode FirstChild()
        {
            if (_children == null || _children.First == null)
                return null;

            return _children.First.Value;
        }

        public ASTNode[] Children()
        {
            if (_children == null || _children.First == null)
                return null;

            return _children.ToArray();
        }

        public ASTNode Next()
        {
            if (_node == null || _node.Next == null)
                return null;

            return _node.Next.Value;
        }

        public ASTNode Prev()
        {
            if (_node == null || _node.Previous == null)
                return null;

            return _node.Previous.Value;
        }
    }


    //TODO: convert to "non static"  ( generic methos Slice gives issue, would need rewrite, but later down the roadmap )
    public static class LexerExtension {
        public static T[] Slice<T>(this T[] src, int start, int end)
        {
            if (end < start)
                return new T[0];

            int len = end - start + 1;

            T[] slice = new T[len];
            for (int i = 0; i < len; i++)
            {
                slice[i] = src[i + start];
            }

            return slice;
        }
    }
    
    public class Lexer
    {
        private int _curLine = 0;
        private string[] _lines;


        static Regex matchListName = new Regex("[a-zA-Z]+", RegexOptions.Compiled);
        static Regex matchNumber = new Regex("^[0-9]+$", RegexOptions.Compiled);

        //private static string _filename = ""; // can be empty

        public Lexer() { 
        }

        public string GetLine(int lineNum=-1)
        {
            if (lineNum < 0) lineNum = _curLine;
            return _lines[lineNum];
        }

        public ASTNode Lex(string[] lines)
        {
            _lines = lines;
            ASTNode node = new ASTNode(ASTNodeType.SCRIPT, null, null, 0, this);

            try
            {
                for (_curLine = 0; _curLine < lines.Length; _curLine++)
                {
                    foreach (var l in lines[_curLine].Split(';'))
                    {
                        ParseLine(node, l);
                    }
                }
            }
            catch (SyntaxError e)
            {
                throw new SyntaxError(lines[_curLine], _curLine, e.Node, e.Message);
            }
            catch (Exception e)
            {
                throw new SyntaxError(lines[_curLine], _curLine, null, e.Message);
            }

            return node;
        }

        public ASTNode Lex(string fname)
        {
            var lines = System.IO.File.ReadAllLines(fname);
            return Lex(lines);
        }

        private static readonly TextParser _tfp = new TextParser("", new char[] { ' ' }, new string[] { "//", "#" }, new char[] { '\'', '\'', '"', '"' });
        private void ParseLine(ASTNode node, string line)
        {
            line = line.Trim();

            if (line.StartsWith("//") || line.StartsWith("#"))
                return;

            // Split the line by spaces (unless the space is in quotes)
            var lexemes = _tfp.GetTokens(line, false);

            if (lexemes.Length == 0)
                return;

            ParseStatement(node, lexemes);
        }

        private void ParseValue(ASTNode node, string lexeme, ASTNodeType typeDefault)
        {
            if (lexeme.StartsWith("0x"))
                node.Push(ASTNodeType.SERIAL, lexeme, _curLine);
            else if (int.TryParse(lexeme, out _))
                node.Push(ASTNodeType.INTEGER, lexeme, _curLine);
            else if (double.TryParse(lexeme, out _))
                node.Push(ASTNodeType.DOUBLE, lexeme, _curLine);
            else
                node.Push(typeDefault, lexeme, _curLine);
        }

        private void ParseCommand(ASTNode node, string lexeme)
        {
            // A command may start with an '@' symbol. Pick that
            // off.
            if (lexeme[0] == '@')
            {
                node.Push(ASTNodeType.QUIET, null, _curLine);
                lexeme = lexeme.Substring(1, lexeme.Length - 1);
            }

            // A command may end with a '!' symbol. Pick that
            // off.
            if (lexeme.EndsWith("!"))
            {
                node.Push(ASTNodeType.FORCE, null, _curLine);
                lexeme = lexeme.Substring(0, lexeme.Length - 1);
            }

            node.Push(ASTNodeType.COMMAND, lexeme, _curLine);
        }

        private void ParseOperand(ASTNode node, string lexeme)
        {
            bool modifier = false;

            // An operand may start with an '@' symbol. Pick that
            // off.
            if (lexeme[0] == '@')
            {
                node.Push(ASTNodeType.QUIET, null, _curLine);
                lexeme = lexeme.Substring(1, lexeme.Length - 1);
                modifier = true;
            }

            // An operand may end with a '!' symbol. Pick that
            // off.
            if (lexeme.EndsWith("!"))
            {
                node.Push(ASTNodeType.FORCE, null, _curLine);
                lexeme = lexeme.Substring(0, lexeme.Length - 1);
                modifier = true;
            }

            if (!modifier)
                ParseValue(node, lexeme, ASTNodeType.OPERAND);
            else
                node.Push(ASTNodeType.OPERAND, lexeme, _curLine);
        }

        private void ParseOperator(ASTNode node, string lexeme)
        {
            switch (lexeme)
            {
                case "==":
                case "=":
                    node.Push(ASTNodeType.EQUAL, null, _curLine);
                    break;
                case "!=":
                    node.Push(ASTNodeType.NOT_EQUAL, null, _curLine);
                    break;
                case "<":
                    node.Push(ASTNodeType.LESS_THAN, null, _curLine);
                    break;
                case "<=":
                    node.Push(ASTNodeType.LESS_THAN_OR_EQUAL, null, _curLine);
                    break;
                case ">":
                    node.Push(ASTNodeType.GREATER_THAN, null, _curLine);
                    break;
                case ">=":
                    node.Push(ASTNodeType.GREATER_THAN_OR_EQUAL, null, _curLine);
                    break;
                default:
                    throw new SyntaxError(node, "Invalid operator in binary expression");
            }
        }

        private void ParseStatement(ASTNode node, string[] lexemes)
        {
            var statement = node.Push(ASTNodeType.STATEMENT, null, _curLine);

            // Examine the first word on the line
            switch (lexemes[0])
            {
                // Ignore comments
                case "#":
                case "//":
                    return;

                // Control flow statements are special
                case "if":
                    {
                        if (lexemes.Length <= 1)
                            throw new SyntaxError(node, "Script compilation error");

                        var t = statement.Push(ASTNodeType.IF, null, _curLine);
                        ParseLogicalExpression(t, lexemes.Slice(1, lexemes.Length - 1));
                        break;
                    }
                case "elseif":
                    {
                        if (lexemes.Length <= 1)
                            throw new SyntaxError(node, "Script compilation error");

                        var t = statement.Push(ASTNodeType.ELSEIF, null, _curLine);
                        ParseLogicalExpression(t, lexemes.Slice(1, lexemes.Length - 1));
                        break;
                    }
                case "else":
                    if (lexemes.Length > 1)
                        throw new SyntaxError(node, "Script compilation error");

                    statement.Push(ASTNodeType.ELSE, null, _curLine);
                    break;
                case "endif":
                    if (lexemes.Length > 1)
                        throw new SyntaxError(node, "Script compilation error");

                    statement.Push(ASTNodeType.ENDIF, null, _curLine);
                    break;
                case "while":
                    {
                        if (lexemes.Length <= 1)
                            throw new SyntaxError(node, "Script compilation error");

                        var t = statement.Push(ASTNodeType.WHILE, null, _curLine);
                        ParseLogicalExpression(t, lexemes.Slice(1, lexemes.Length - 1));
                        break;
                    }
                case "endwhile":
                    if (lexemes.Length > 1)
                        throw new SyntaxError(node, "Script compilation error");

                    statement.Push(ASTNodeType.ENDWHILE, null, _curLine);
                    break;
                case "for":
                    {
                        if (lexemes.Length <= 1)
                            throw new SyntaxError(node, "Script compilation error");

                        ParseForLoop(statement, lexemes.Slice(1, lexemes.Length - 1));
                        break;
                    }
                case "endfor":
                    if (lexemes.Length > 1)
                        throw new SyntaxError(node, "Script compilation error");

                    statement.Push(ASTNodeType.ENDFOR, null, _curLine);
                    break;
                case "break":
                    if (lexemes.Length > 1)
                        throw new SyntaxError(node, "Script compilation error");

                    statement.Push(ASTNodeType.BREAK, null, _curLine);
                    break;
                case "continue":
                    if (lexemes.Length > 1)
                        throw new SyntaxError(node, "Script compilation error");

                    statement.Push(ASTNodeType.CONTINUE, null, _curLine);
                    break;
                case "stop":
                    if (lexemes.Length > 1)
                        throw new SyntaxError(node, "Script compilation error");

                    statement.Push(ASTNodeType.STOP, null, _curLine);
                    break;
                case "replay":
                case "loop":
                    if (lexemes.Length > 1)
                        throw new SyntaxError(node, "Script compilation error");

                    statement.Push(ASTNodeType.REPLAY, null, _curLine);
                    break;
                default:
                    // It's a regular statement.
                    ParseCommand(statement, lexemes[0]);

                    foreach (var lexeme in lexemes.Slice(1, lexemes.Length - 1))
                    {
                        ParseValue(statement, lexeme, ASTNodeType.STRING);
                    }
                    break;
            }

        }

        private static bool IsOperator(string lexeme)
        {
            switch (lexeme)
            {
                case "==":
                case "=":
                case "!=":
                case "<":
                case "<=":
                case ">":
                case ">=":
                    return true;
            }

            return false;
        }

        private void ParseLogicalExpression(ASTNode node, string[] lexemes)
        {
            // The steam language supports logical operators 'and' and 'or'.
            // Catch those and split the expression into pieces first.
            // Fortunately, it does not support parenthesis.
            var expr = node;
            bool logical = false;
            int start = 0;

            for (int i = start; i < lexemes.Length; i++)
            {
                if (lexemes[i] == "and" || lexemes[i] == "or")
                {
                    if (!logical)
                    {
                        expr = node.Push(ASTNodeType.LOGICAL_EXPRESSION, null, _curLine);
                        logical = true;
                    }

                    ParseExpression(expr, lexemes.Slice(start, i - 1));
                    start = i + 1;
                    expr.Push(lexemes[i] == "and" ? ASTNodeType.AND : ASTNodeType.OR, null, _curLine);

                }
            }

            ParseExpression(expr, lexemes.Slice(start, lexemes.Length - 1));
        }

        private void ParseExpression(ASTNode node, string[] lexemes)
        {

            // The steam language supports both unary and
            // binary expressions. First determine what type
            // we have here.

            bool unary = false;
            bool binary = false;

            foreach (var lexeme in lexemes)
            {
                
                if (IsOperator(lexeme))
                {
                    // Operators mean it is a binary expression.
                    binary = true;
                }
            }

            // If no operators appeared, it's a unary expression
            if (!unary && !binary)
                unary = true;

            if (unary && binary)
                throw new SyntaxError(node, String.Format("Invalid expression at line {0}", node.LineNumber));

            if (unary)
                ParseUnaryExpression(node, lexemes);
            else
                ParseBinaryExpression(node, lexemes);
        }

        private void ParseUnaryExpression(ASTNode node, string[] lexemes)
        {
            var expr = node.Push(ASTNodeType.UNARY_EXPRESSION, null, _curLine);

            int i = 0;

            if (lexemes[i] == "not")
            {
                expr.Push(ASTNodeType.NOT, null, _curLine);
                i++;
            }

            ParseOperand(expr, lexemes[i++]);

            for (; i < lexemes.Length; i++)
            {
                ParseValue(expr, lexemes[i], ASTNodeType.STRING);
            }
        }

        private void ParseBinaryExpression(ASTNode node, string[] lexemes)
        {
            var expr = node.Push(ASTNodeType.BINARY_EXPRESSION, null, _curLine);

            int i = 0;
            if (lexemes[i] == "not")
            {
                expr.Push(ASTNodeType.NOT, null, _curLine);
                i++;
            }

            // The expressions on either side of the operator can be values
            // or operands that need to be evaluated.
            ParseOperand(expr, lexemes[i++]);

            for (; i < lexemes.Length; i++)
            {
                if (IsOperator(lexemes[i]))
                    break;

                ParseValue(expr, lexemes[i], ASTNodeType.STRING);
            }

            ParseOperator(expr, lexemes[i++]);

            ParseOperand(expr, lexemes[i++]);

            for (; i < lexemes.Length; i++)
            {
                if (IsOperator(lexemes[i]))
                    break;

                ParseValue(expr, lexemes[i], ASTNodeType.STRING);
            }
        }

        private void ParseForLoop(ASTNode statement, string[] lexemes)
        {
            // There are 4 variants of for loops in steam. The simplest two just
            // iterate a fixed number of times. The other two iterate
            // parts of lists. We call those second two FOREACH.

            // We're intentionally deprecating one of the variants here.
            // The for X to Y in LIST variant may have some niche uses, but
            // is annoying to implement.

            // The for X loop remains supported as is, while the
            // The for X to Y variant, where both X and Y are integers,
            // is transformed to a for X.
            // for X in LIST form is unsupported and will probably crash


            // Reworking FOR implementation
            /* Dalamar
            for (end)                                ->    lexemes.Length == 1
            for (start) to (end)                     ->    lexemes.Length == 3
            for (start) to ('list name')             ->    lexemes.Length == 3 && startswith '
            for (start) to (end) in ('list name')    ->    lexemes.Length == 5
            */

            //Common Syntax check
            if (!matchNumber.IsMatch(lexemes[0]))
            {
                throw new SyntaxError(statement, "Invalid for loop: expected number got " + lexemes[0]);
            }

            if (lexemes.Length > 1 && lexemes[1] != "to" && lexemes[1] != "in" )
            {
                throw new SyntaxError(statement, "Invalid for loop: missing 'to/in' keyword");
            }




            //CASE: for (end)
            if (lexemes.Length == 1)
            {
                if (!matchNumber.IsMatch(lexemes[0])) {
                    throw new SyntaxError(statement, "Invalid for loop: expected number got "+ lexemes[0]);
                }
                var loop = statement.Push(ASTNodeType.FOR, null, _curLine);
                ParseValue(loop, lexemes[0], ASTNodeType.STRING);
            }
            //CASE: for (start) to (end) in ('list name')
            else if (lexemes.Length == 5)
            {
                if (!matchNumber.IsMatch(lexemes[2]))
                {
                    throw new SyntaxError(statement, "Invalid for loop: expected number got " + lexemes[2]);
                }
                if ( lexemes[3] != "in" && lexemes[3] != "to" )
                {
                    throw new SyntaxError(statement, "Invalid for loop: missing 'in/to' keyword");
                }
                if (!matchListName.IsMatch(lexemes[4]))
                {
                    throw new SyntaxError(statement, "Invalid for loop: list names must contain letters");
                }

                var loop = statement.Push(ASTNodeType.FOREACH, null, _curLine);
                ParseValue(loop, lexemes[0], ASTNodeType.STRING);
                ParseValue(loop, lexemes[2], ASTNodeType.STRING);
                ParseValue(loop, lexemes[4], ASTNodeType.LIST);
            }
            //CASE: for (start) to ('list name')
            else if (lexemes.Length == 3 && matchListName.IsMatch(lexemes[2])  )
            {
                var loop = statement.Push(ASTNodeType.FOREACH, null, _curLine);
                ParseValue(loop, lexemes[0], ASTNodeType.STRING);
                ParseValue(loop, lexemes[2], ASTNodeType.LIST);
            }
            //CASE: for (start) to (end)
            else if (lexemes.Length == 3)
            {
                if (!matchNumber.IsMatch(lexemes[2]))
                {
                    throw new SyntaxError(statement, "Invalid for loop: expected number got " + lexemes[2]);
                }
                int from = Int32.Parse(lexemes[0]);
                int to = Int32.Parse(lexemes[2]);
                int length = to - from;

                if (length < 0)
                {
                    throw new SyntaxError(statement, "Invalid for loop: loop count must be greater then 0, " + length.ToString() + " given ");
                }

                var loop = statement.Push(ASTNodeType.FOR, null, _curLine);
                ParseValue(loop, length.ToString(), ASTNodeType.STRING);
            }
            //CASE: syntax error
            else
            {
                throw new SyntaxError(statement, "Invalid for loop");
            }
        }

    }

    internal class TextParser
    {
        private readonly char[] _delimiters, _quotes;
        private readonly string[] _comments;
        private int _eol;
        private int _pos;
        private int _Size;
        private string _string;
        private bool _trim;

        public TextParser(string str, char[] delimiters, string[] comments, char[] quotes)
        {
            _delimiters = delimiters;
            _comments = comments;
            _quotes = quotes;
            _Size = str.Length;
            _string = str;
        }

        internal bool IsDelimiter()
        {
            bool result = false;

            for (int i = 0; i < _delimiters.Length && !result; i++)
                result = _string[_pos] == _delimiters[i];

            return result;
        }

        private void SkipToData()
        {
            while (_pos < _eol && IsDelimiter())
                _pos++;
        }

        private bool IsComment()
        {
            bool result = _string[_pos] == '\n';

            for (int i = 0; i < _comments.Length && !result; i++)
            {
                //Dalamar: added support for inline comments ( multi char )
                var comment = _comments[i];
                var char_left = _string.Length - _pos;

                result = _string.Substring(_pos, Math.Min(char_left, comment.Length) ) == comment;

                /*
                if (result && i + 1 < _comments.Length && _comments[i] == _comments[i + 1] && _pos + 1 < _eol)
                {
                    result = _string[_pos] == _string[_pos + 1];
                    i++;
                }
                */
            }

            return result;
        }

        private string ObtainData()
        {
            StringBuilder result = new StringBuilder();

            while (_pos < _Size && _string[_pos] != '\n')
            {
                if (IsDelimiter())
                    break;

                if (IsComment())
                {
                    _pos = _eol;

                    break;
                }

                if (_string[_pos] != '\r' && (!_trim || _string[_pos] != ' ' && _string[_pos] != '\t'))
                    result.Append(_string[_pos]);

                _pos++;
            }

            return result.ToString();
        }

        private string ObtainQuotedData()
        {
            bool exit = false;
            string result = "";

            for (int i = 0; i < _quotes.Length; i += 2)
            {
                if (_string[_pos] == _quotes[i])
                {
                    char endQuote = _quotes[i + 1];
                    exit = true;

                    int pos = _pos + 1;
                    int start = pos;

                    while (pos < _eol && _string[pos] != '\n' && _string[pos] != endQuote)
                    {
                        if (_string[pos] == _quotes[i]) // another {
                        {
                            _pos = pos;
                            ObtainQuotedData(); // skip
                            pos = _pos;
                        }

                        pos++;
                    }

                    _pos++;
                    int size = pos - start;

                    if (size > 0)
                    {
                        result = _string.Substring(start, size).TrimEnd('\r', '\n');
                        _pos = pos;

                        if (_pos < _eol && _string[_pos] == endQuote)
                            _pos++;
                    }

                    break;
                }
            }

            if (!exit)
                result = ObtainData();

            return result;
        }

        internal string[] GetTokens(string str, bool trim = true)
        {
            _trim = trim;
            List<string> result = new List<string>();

            _pos = 0;
            _string = str;
            _Size = str.Length;
            _eol = _Size - 1;

            while (_pos < _eol)
            {
                SkipToData();

                if (IsComment())
                    break;

                string buf = ObtainQuotedData();

                if (buf.Length > 0)
                    result.Add(buf);
            }

            return result.ToArray();
        }
    }
}



#endregion
