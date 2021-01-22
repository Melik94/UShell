﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UShell
{
    public class Shell : MonoBehaviour, ICommand
    {
        #region FIELDS
        private static Shell _sh;

        private static readonly string[] _readme =
{
@"    type 'help' to display the list of all registered commands
    type 'convar' to display the list of all convars
	type ':help' to display informations about events
	type 'alias' to display the list of all aliases
    type 'help command' to display informations about a command (e.g. 'help help')
    type 'clear' to clear the console (if supported)",
@"    type 'echo message ...' to log a message (e.g. 'echo hello world!')
    type 'source path' to execute the content of a file as a command line (e.g. 'source ""C:\Users\MyName\Desktop\script.ush""')",
};
        private static readonly Token[] _operators =
        {
            new Token(Token.Type.NEWLINE, "\n"),
            new Token(Token.Type.NEWLINE, "\r"),
            new Token(Token.Type.SEPARATOR, ";"),
        };
        private static readonly char[] _blankCharacters =
        {
            ' ',
            '\t',
        };
        private static string[] _builtinLabels =
        {
            "README",
            "help",
            "version",
            "echo",
            "type",
            "alias",
            "unalias",
            "history",
            ".",
            "source",
            "throw",
            "font",
            "args",
            "convar",
            "event",
            "reflex",
        };
        [Convar("converters", "the shell converters", true)]
        private static Tuple<Type, Type>[] _converters =
        {
            new Tuple<Type, Type>(typeof(Vector2), typeof(Converters.Vector2Converter)),
            new Tuple<Type, Type>(typeof(Vector3), typeof(Converters.Vector3Converter)),
            new Tuple<Type, Type>(typeof(Vector4), typeof(Converters.Vector4Converter)),
            new Tuple<Type, Type>(typeof(Quaternion), typeof(Converters.QuaternionConverter)),
            new Tuple<Type, Type>(typeof(Color), typeof(Converters.ColorConverter)),
            new Tuple<Type, Type>(typeof(Color32), typeof(Converters.Color32Converter)),
            new Tuple<Type, Type>(typeof(Rect), typeof(Converters.RectConverter)),
        };
        private static Type[] _excludedTypes =
        {
            typeof(object),
            typeof(MonoBehaviour),
        };

        private const string _version = "a1.0";
        private const string _playerPrefsKeysPrefix = "ush";
        private const BindingFlags _bindingFlags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

        [SerializeField] [TextArea(1, 10)]
        private string _motd = "type 'README' for more information";
        [SerializeField]
        private int _historySize = 200;
        [SerializeField] [Convar("fuzzysearch", "does the shell should search for similar commands if an unknown command is read?")]
        private bool _logSimilarCmds = true;
        [SerializeField]
        private string[] _defaultAliasKeys;
        [SerializeField]
        private string[] _defaultAliasValues;
        [SerializeField]
        private bool _singleInstance = true;
        [SerializeField]
        private bool _dontDestroyOnLoad = true;
        [SerializeField]
        private string[] _excludedAssemblies =
        {
                "mscorlib",
                "System",
                "Unity",
                "SyntaxTree",
                "Mono",
                "ExCSS",
                "PathCreator",
                "Rewired",
                "Heathen",
        };

        private string _ID = String.Empty;

        private History _history;

        private Dictionary<string, IConsole> _consoles = new Dictionary<string, IConsole>();
        private Dictionary<string, ICommand> _cmds = new Dictionary<string, ICommand>();
        private Dictionary<string, ICommand> _builtinCmds = new Dictionary<string, ICommand>();
        private Dictionary<string, string> _aliases = new Dictionary<string, string>();
        private Dictionary<string, ConvarCmd> _convars = new Dictionary<string, ConvarCmd>();
        private Dictionary<string, List<MethodCmd>> _methods = new Dictionary<string, List<MethodCmd>>();
        private Dictionary<string, EventCmd> _events = new Dictionary<string, EventCmd>();
        private Dictionary<Type, List<object>> _instances = new Dictionary<Type, List<object>>();
        private Dictionary<string, string> _variables = new Dictionary<string, string>();

        private AssemblyBuilder _eventsAssemblyBuilder;

        [Convar("headless", "is the current process running in batchmode?", true)]
        private bool _isHeadless = false;

        private Stack<string> _usedAliases = new Stack<string>();

        private Dictionary<string, UFont> _uFonts = new Dictionary<string, UFont>();
        #endregion

        #region EVENTS
        /// <summary>
        /// in string label, in string[] args, out bool eventCatch
        /// </summary>
        public event Func<string, string[], bool> OnEvent;
        #endregion

        #region PROPERTIES
        /// <summary>
        /// 
        /// </summary>
        public static Shell Main { get { return _sh; } }
        /// <summary>
        /// 
        /// </summary>
        [Convar("version", "the shell version", true)]
        public static string Version { get { return _version; } }

        /// <summary>
        /// The identifier for this Shell
        /// </summary>
        [Convar("shid", "the shell identifier", true)]
        public string ID { get { return _ID; } }
        /// <summary>
        /// The number of entries in the command history
        /// </summary>
        [Convar("histcount", "the number of entries in the command history", true)]
        public int HistoryCount { get { return _history.Count; } }
        /// <summary>
        /// The message of the day
        /// </summary>
        [Convar("motd", "the message of the day", true)]
        public string MOTD { get { return _motd; } }
        #endregion

        #region MESSAGES
        void Awake()
        {
            //Singleton
            if (_sh != null)
            {
                if (_singleInstance)
                {
                    Destroy(this.gameObject);
                    return;
                }
            }
            else
                _sh = this;

            if (_dontDestroyOnLoad)
            {
                if (this.transform.parent != null)
                    this.transform.SetParent(null);
                DontDestroyOnLoad(this.gameObject);
            }

            Stopwatch watch = Stopwatch.StartNew();

            _isHeadless = Application.isBatchMode;

            //Cmd History initialization
            _history = new History(_historySize, _playerPrefsKeysPrefix + "_history");
            _history.LoadFromDisk();

            //Default Aliases registration
            int count = Mathf.Min(_defaultAliasKeys.Length, _defaultAliasValues.Length);
            for (int i = 0; i < count; i++)
                _aliases.Add(_defaultAliasKeys[i], _defaultAliasValues[i]);

            //Builtin Cmds registration
            for (int i = 0; i < _builtinLabels.Length; i++)
                _builtinCmds.Add(_builtinLabels[i], this);

            //Add Converters
            for (int i = 0; i < _converters.Length; i++)
                TypeDescriptor.AddAttributes(_converters[i].Item1, new Attribute[] { new TypeConverterAttribute(_converters[i].Item2) });

            findAndRegisterMembers();
            RegisterInstance(this);

            //Create the assembly for the events
            _eventsAssemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("UShellEventsAssembly"), AssemblyBuilderAccess.Run);

            Debug.Log("shell: initialized in " + watch.ElapsedMilliseconds + "ms");
        }
        void Start()
        {
            if (_sh != this)
                return;


            string cmdLine;

            //Execute cmds from command line args
            try {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 1; i < args.Length - 1; i++)
                    if (args[i] == "-cmd")
                        processCmdLine("-cmd", args[i++ + 1], false);
            } catch (Exception e) {
                Debug.LogError("shell: error while reading command line argument \"-cmd\"");
                Debug.LogException(e);
            }

            //Execute cmds from init file
            string startupScriptPath = Application.dataPath + "/../.ushrc";
            if (File.Exists(startupScriptPath))
            {
                try {
                    cmdLine = File.ReadAllText(startupScriptPath);
                    processCmdLine(".ushrc", cmdLine, false);
                } catch (Exception e) {
                    Debug.LogError("shell: error while loading " + startupScriptPath);
                    Debug.LogException(e);
                }
            }

            //Execute cmds from player prefs
            string startupPPrefKey = _playerPrefsKeysPrefix + "rc";
            if (PlayerPrefs.HasKey(startupPPrefKey))
            {
                cmdLine = PlayerPrefs.GetString(startupPPrefKey);
                processCmdLine(startupPPrefKey, cmdLine, false);
            }
        }
        void OnDestroy()
        {
            if (_sh != this)
                return;

            deleteAllFonts();
            _history.SaveToDisk();
            UnregisterInstance(this);
        }

        void OnEnable()
        {
            if (_sh != this)
                return;


            Application.logMessageReceivedThreaded += handleLog;

            //Set the shell ID from the command line args
            try {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "-shid")
                    {
                        _ID = args[i + 1];
                        break;
                    }
                }
            } catch (Exception e) {
                Debug.LogError("shell: error while reading command line argument \"-shid\"");
                Debug.LogException(e);
            }
        }
        void OnDisable()
        {
            if (_sh != this)
                return;

            Application.logMessageReceivedThreaded -= handleLog;
        }
        #endregion

        #region METHODS
        #region REGISTRATION
        /// <summary>
        /// 
        /// </summary>
        /// <param name="id"></param>
        /// <param name="console"></param>
        public void RegisterConsole(string id, IConsole console)
        {
            if (id == null || console == null)
            {
                console.AddLog(new Log(LogType.Error, "shell: cannot register console (id or console null)", String.Empty));
                return;
            }
            if (_consoles.ContainsKey(id))
            {
                console.AddLog(new Log(LogType.Error, "shell: cannot register console (id already used)", String.Empty));
                return;
            }
            if (_consoles.ContainsValue(console))
            {
                console.AddLog(new Log(LogType.Error, "shell: cannot register console (console already registered)", String.Empty));
                return;
            }

            _consoles.Add(id, console);
            console.Init(_isHeadless);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="id"></param>
        /// <param name="console"></param>
        /// <returns></returns>
        public bool UnregisterConsole(string id, IConsole console)
        {
            if (id == null || console == null)
            {
                console.AddLog(new Log(LogType.Error, "shell: cannot unregister console (id or console null)", String.Empty));
                return false;
            }

            return _consoles.Remove(id);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="id"></param>
        /// <param name="command"></param>
        public void RegisterCmd(string id, ICommand command)
        {
            if (id == null || command == null)
            {
                Debug.LogError("shell: cannot register command (id or command null)");
                return;
            }
            if (_cmds.ContainsKey(id))
            {
                Debug.LogError("shell: cannot register command \"" + id + "\" (id already used)");
                return;
            }

            _cmds.Add(id, command);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="id"></param>
        /// <param name="command"></param>
        /// <returns></returns>
        public bool UnregisterCmd(string id, ICommand command)
        {
            if (id == null || command == null)
            {
                Debug.LogError("shell: cannot unregister command (id or command null)");
                return false;
            }

            return _cmds.Remove(id);
        }
        /// <summary>
        /// If a type defines an instance member with a Convar/Cmd/Event attribute, each instance of this type must be registered in order to access the member.
        /// It is not necessary to call this method if the Convar/Cmd/Event attribute is only used on static members.
        /// </summary>
        /// <param name="instance"></param>
        public void RegisterInstance(object instance)
        {
            if (instance == null)
            {
                Debug.LogError("shell: cannot register instance (instance null)");
                return;
            }

            registerInstanceInternal(instance, instance.GetType());
        }
        private void registerInstanceInternal(object instance, Type type)
        {
            if (!_instances.ContainsKey(type))
                _instances.Add(type, new List<object>());

            _instances[type].Add(instance);

            if (type.BaseType != null && Array.IndexOf(_excludedTypes, type.BaseType) < 0)
                registerInstanceInternal(instance, type.BaseType);
        }
        /// <summary>
        /// Use this method if you no longer use an object that has been registered using the RegisterInstance method
        /// </summary>
        /// <param name="instance"></param>
        /// <returns></returns>
        public bool UnregisterInstance(object instance)
        {
            if (instance == null)
            {
                Debug.LogError("shell: cannot unregister instance (instance null)");
                return false;
            }

            return unregisterInstanceInternal(instance, instance.GetType());
        }
        private bool unregisterInstanceInternal(object instance, Type type)
        {
            if (!_instances.ContainsKey(type))
                return false;

            bool result = _instances[type].Remove(instance);

            if (_instances[type].Count <= 0)
                _instances.Remove(type);

            if (type.BaseType != null && Array.IndexOf(_excludedTypes, type.BaseType) < 0)
                unregisterInstanceInternal(instance, type.BaseType);

            return result;
        }

        private void findAndRegisterMembers()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            //Make the shell assembly tested before the others
            Assembly executingAssembly = Assembly.GetExecutingAssembly();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (assemblies[i] == executingAssembly)
                {
                    Assembly tmp = assemblies[i];
                    assemblies[i] = assemblies[0];
                    assemblies[0] = tmp;
                    break;
                }
            }

            //Make the shell type tested before the others
            Type[] executingTypes = executingAssembly.GetTypes();
            for (int i = 0; i < executingTypes.Length; i++)
            {
                if (executingTypes[i] == typeof(Shell))
                {
                    Type tmp = executingTypes[i];
                    executingTypes[i] = executingTypes[0];
                    executingTypes[0] = tmp;
                    break;
                }
            }

            registerMembersFromTypes(executingTypes);
            for (int i = 1; i < assemblies.Length; i++)
            {
                bool isException = false;
                Assembly assembly = assemblies[i];
                for (int j = 0; j < _excludedAssemblies.Length; j++)
                {
                    if (assembly.FullName.StartsWith(_excludedAssemblies[j], StringComparison.InvariantCulture))
                    {
                        isException = true;
                        break;
                    }
                }

                if (!isException)
                    registerMembersFromTypes(assemblies[i].GetTypes());
            }
        }
        private void registerMembersFromTypes(Type[] types)
        {
            for (int i = 0; i < types.Length; i++)
            {
                FieldInfo[] fieldInfos = types[i].GetFields(_bindingFlags);
                for (int j = 0; j < fieldInfos.Length; j++)
                    if (Attribute.IsDefined(fieldInfos[j], typeof(ConvarAttribute), false))
                        registerConvar(new ConvarCmd(fieldInfos[j]));

                PropertyInfo[] propertyInfos = types[i].GetProperties(_bindingFlags);
                for (int j = 0; j < propertyInfos.Length; j++)
                    if (Attribute.IsDefined(propertyInfos[j], typeof(ConvarAttribute), false))
                        registerConvar(new ConvarCmd(propertyInfos[j]));

                MethodInfo[] methodInfos = types[i].GetMethods(_bindingFlags);
                for (int j = 0; j < methodInfos.Length; j++)
                    if (Attribute.IsDefined(methodInfos[j], typeof(CmdAttribute), false))
                        registerMethod(new MethodCmd(methodInfos[j]));

                EventInfo[] eventInfos = types[i].GetEvents(_bindingFlags);
                for (int j = 0; j < eventInfos.Length; j++)
                    if (Attribute.IsDefined(eventInfos[j], typeof(EventAttribute), false))
                        registerEvent(new EventCmd(eventInfos[j]));
            }
        }
        private void registerConvar(ConvarCmd convarCmd)
        {
            string label = convarCmd.Name;
            if (!_convars.ContainsKey(label))
                _convars.Add(label, convarCmd);
            else
            {
                label = convarCmd.DeclaringType + "." + convarCmd.Name;
                if (!_convars.ContainsKey(label))
                    _convars.Add(label, convarCmd);
            }
        }
        private void registerMethod(MethodCmd methodCmd)
        {
            string label = methodCmd.Name;
            if (!_methods.ContainsKey(label))
                _methods.Add(label, new List<MethodCmd>() { methodCmd });
            else
            {
                for (int i = 0; i < _methods[label].Count; i++)
                    if (methodCmd.ParametersCount == _methods[label][i].ParametersCount || methodCmd.DeclaringType != _methods[label][i].DeclaringType)
                        return;

                _methods[label].Add(methodCmd);
            }
        }
        private void registerEvent(EventCmd eventCmd)
        {
            string label = eventCmd.Name;
            if (!_events.ContainsKey(label))
                _events.Add(label, eventCmd);
            else
            {
                label = eventCmd.DeclaringType + "." + eventCmd.Name;
                if (!_events.ContainsKey(label))
                    _events.Add(label, eventCmd);
            }
        }
        #endregion

        #region COMMAND PROCESSING
        /// <summary>
        /// 
        /// </summary>
        /// <param name="source"></param>
        /// <param name="cmdLine"></param>
        public void ProcessCmdLine(string source, string cmdLine)
        {
            processCmdLine(source, cmdLine, true);
        }

        private void processCmdLine(string source, string cmdLine, bool saveToHistory = true)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(cmdLine))
                return;

            try
            {
                processCmdLineVisibility(source, cmdLine, saveToHistory);
                processCmdLineInternal(source, cmdLine, _usedAliases);
            }
            catch (Exception)
            {
                _usedAliases.Clear();
                Debug.LogError("shell: fatal error!");
                throw;
            }
        }
        private void processCmdLineVisibility(string source, string cmdLine, bool saveToHistory)
        {
            if (!string.IsNullOrEmpty(cmdLine) && Array.IndexOf(_blankCharacters, cmdLine[0]) < 0)
            {
                Debug.Log(source + "> " + cmdLine);
                if (saveToHistory && _history.GetValue(1) != cmdLine)
                    _history.AddValue(cmdLine);
            }
        }
        private void processCmdLineInternal(string source, string cmdLine, Stack<string> usedAliases)
        {
            List<Token> tokens;
            cmdLine = Utils.RemoveEscapedSeparators(cmdLine, new char[] { '\n', '\r' });
            try
            {
                tokens = Utils.Tokenize(cmdLine, _operators);
                Utils.ExpandTokens(tokens, GetVariableValue);
                tokens.RemoveAll(token => string.IsNullOrEmpty(token.value));
                Utils.Parse(tokens);
            }
            catch (Exception e)
            {
                Debug.LogWarning("shell: " + e.Message);
                return;
            }

            List<List<Token>> cmds = Utils.Split(tokens, _operators);
            for (int i = 0; i < cmds.Count; i++)
            {
                int offset = Utils.ResolveAssignment(cmds[i], SetVariableValue);
                if (cmds[i].Count > offset)
                {
                    string label = cmds[i][offset].value;
                    List<string> args = new List<string>();
                    for (int j = offset + 1; j < cmds[i].Count; j++)
                        args.Add(cmds[i][j].value);

                    string unquoteLabel = Utils.RemoveQuoting(label);
                    if (unquoteLabel == label && !usedAliases.Contains(label) && _aliases.TryGetValue(label, out string aliasValue))
                    {
                        usedAliases.Push(label);
                        string cmd = aliasValue + " " + string.Join(" ", args);
                        processCmdLineInternal(source, cmd, usedAliases);
                        usedAliases.Pop();
                    }
                    else
                    {
                        string[] fields = args.ToArray();
                        Utils.RemoveQuoting(fields);
                        processCmd(source, unquoteLabel, fields);
                    }
                }
            }
        }
        private void processCmd(string source, string label, string[] fields)
        {
            const string unknownMessage = "shell: {0}: unknown command";

            if (label == "help" && fields.Length == 1 && fields[0] == "-e")
                processEvent(source, ":help", new string[0]);
            else if (label[0] == ':')
                processEvent(source, label, fields);
            else if (_builtinCmds.TryGetValue(label, out ICommand cmd) || _cmds.TryGetValue(label, out cmd))
                processICmd(source, cmd, label, fields);
            else if (_convars.TryGetValue(label, out ConvarCmd convarCmd))
                processConvar(source, convarCmd, label, fields);
            else if (_methods.TryGetValue(label, out List<MethodCmd> methodCmds))
                processMethod(source, methodCmds, label, fields);
            else
            {
                StringBuilder log = new StringBuilder(string.Format(unknownMessage, label));
                if (_logSimilarCmds)
                {
                    List<string> fuzzyWords = GetSimilarCmds(label, 3);
                    if (fuzzyWords.Count > 0)
                    {
                        log.Append("\nDid you mean:  ");
                        foreach (string word in fuzzyWords)
                            log.Append(word + " ");
                    }
                }

                Debug.LogWarning(log);
            }
        }
        private void processEvent(string source, string label, string[] fields)
        {
            const string exceptionMessage = "shell: {0}: {1}";
            const string unknownMessage = "shell: {0}: unknown event";
            bool eventCatch = false;

            foreach (KeyValuePair<string, IConsole> pair in _consoles)
            {
                try
                {
                    if (pair.Value.ProcessEvent(label, fields))
                        eventCatch = true;
                }
                catch (FormatException e)
                {
                    Debug.LogWarning(string.Format(exceptionMessage, pair.Key + label, e.Message));
                    eventCatch = true;
                }
                catch (Exception e)
                {
                    Debug.LogError(string.Format(exceptionMessage, pair.Key + label, e.Message));
                    eventCatch = true;
                }
            }

            if (OnEvent != null)
            {
                try
                {
                    if (OnEvent(label, fields))
                        eventCatch = true;
                }
                catch (FormatException e)
                {
                    Debug.LogWarning(string.Format(exceptionMessage, label, e.Message));
                    eventCatch = true;
                }
                catch (Exception e)
                {
                    Debug.LogError(string.Format(exceptionMessage, label, e.Message));
                    eventCatch = true;
                }
            }

            if (!eventCatch)
                Debug.LogWarning(string.Format(unknownMessage, label));
        }
        private void processICmd(string source, ICommand cmd, string label, string[] fields)
        {
            const string wrongSyntaxMessage = "shell: {0}: wrong syntax";
            const string exceptionMessage = "shell: {0}: {1}";

            try
            {
                cmd.Execute(label, fields);
            }
            catch (ArgumentException)
            {
                string log = string.Format(wrongSyntaxMessage, label);
                string[] syntaxes = cmd.GetSyntaxes(label);
                if (syntaxes?.Length > 0)
                    log += "\n" + label + " " + syntaxes[0];

                Debug.LogWarning(log);
            }
            catch (FormatException e)
            {
                Debug.LogWarning(string.Format(exceptionMessage, label, e.Message));
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format(exceptionMessage, label, e.Message));
            }
        }
        private void processConvar(string source, ConvarCmd convarCmd, string label, string[] fields)
        {
            const string noInstances = "shell: {0}: no instances registered for {1}";
            const string cannotRead = "shell: {0}: no permission to read";
            const string cannotModify = "shell: {0}: no permission to modify";
            const string tooManyArgs = "shell: {0}: too many arguments";
            const string exception = "shell: {0}: {1}";

            List<object> instances;
            if (convarCmd.IsStatic)
            {
                instances = new List<object>();
                instances.Add(null);
            }
            else if (!_instances.TryGetValue(convarCmd.DeclaringType, out instances))
            {
                Debug.LogWarning(string.Format(noInstances, label, convarCmd.DeclaringType));
                return;
            }

            if (fields.Length == 0) //READING VALUE
            {
                if (convarCmd.CanRead)
                {
                    StringBuilder log = new StringBuilder();
                    try
                    {
                        for (int i = 0; i < instances.Count; i++)
                            log.Append(Utils.ConvertToString(convarCmd.GetValue(instances[i])));
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(string.Format(exception, label, e.Message));
                    }

                    Debug.Log(log);
                }
                else
                    Debug.LogWarning(string.Format(cannotRead, label));
            }
            else if (fields.Length == 1) //MODIFY VALUE
            {
                if (convarCmd.CanWrite)
                {
                    try
                    {
                        if (convarCmd.Type.IsArray)
                        {
                            Type T = convarCmd.Type.GetElementType();
                            Utils.TryParseArray(fields[0], out object result, T);
                            for (int i = 0; i < instances.Count; i++)
                                convarCmd.SetValue(instances[i], result);
                        }
                        else
                        {
                            object value = Utils.ConvertFromString(fields[0], convarCmd.Type);
                            for (int i = 0; i < instances.Count; i++)
                                convarCmd.SetValue(instances[i], value);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(string.Format(exception, label, e.Message));
                    }
                }
                else
                    Debug.LogWarning(string.Format(cannotModify, label));
            }
            else
                Debug.LogWarning(string.Format(tooManyArgs, label));
        }
        private void processMethod(string source, List<MethodCmd> methodCmds, string label, string[] fields)
        {
            const string noInstances = "shell: {0}: no instances registered for {1}";
            const string wrongSyntax = "shell: {0}: wrongSyntax";
            const string exception = "shell: {0}: {1}";

            for (int i = 0; i < methodCmds.Count; i++)
            {
                ParameterInfo[] parameters = methodCmds[i].Parameters;
                if (parameters.Length != fields.Length)
                    continue;

                List<object> instances;
                if (methodCmds[i].IsStatic)
                {
                    instances = new List<object>();
                    instances.Add(null);
                }
                else if (!_instances.TryGetValue(methodCmds[i].DeclaringType, out instances))
                {
                    Debug.LogWarning(string.Format(noInstances, label, methodCmds[i].DeclaringType));
                    return;
                }

                object[] args = new object[fields.Length];
                for (int j = 0; j < args.Length; j++)
                {
                    if (parameters[j].IsOut)
                        continue;

                    Type paramType = parameters[j].ParameterType.IsByRef ? parameters[j].ParameterType.GetElementType() : parameters[j].ParameterType;
                    string fieldValue;
                    if (parameters[j].ParameterType.IsByRef && !parameters[j].IsIn)
                        fieldValue = GetVariableValue(fields[j]);
                    else
                        fieldValue = fields[j];

                    try
                    {
                        args[j] = Utils.ConvertFromString(fieldValue, paramType);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(string.Format(exception, label, e.Message));
                        return;
                    }
                }

                try
                {
                    object returnValue = null;
                    for (int j = 0; j < instances.Count; j++)
                        returnValue = methodCmds[i].Invoke(instances[j], args);

                    for (int j = 0; j < args.Length; j++)
                    {
                        if (parameters[j].ParameterType.IsByRef && !parameters[j].IsIn)
                            SetVariableValue(fields[j], args[j].ToString());
                    }

                    if (returnValue != null)
                        Debug.Log(Utils.ConvertToString(returnValue));
                }
                catch (Exception e)
                {
                    Debug.LogError(string.Format(exception, label, e.Message));
                    return;
                }

                return;
            }

            StringBuilder strBuilder = new StringBuilder();
            strBuilder.AppendLine(string.Format(wrongSyntax, label));
            getHelpFromLabel(strBuilder, methodCmds, label);
            Debug.LogWarning(strBuilder);
        }
        #endregion

        #region OTHER
        /// <summary>
        /// 
        /// </summary>
        /// <param name="index">The less the more recent</param>
        /// <returns></returns>
        public string GetCmdLineFromHistory(int index)
        {
            return _history.GetValue(index);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="prefix"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public string GetCompletion(string prefix, out List<string> options)
        {
            //Potential variable assignments are not taken into account

            string label = "";
            bool bypassAliases = false;
            List<Token> tokens = Utils.Tokenize(prefix, _operators, false);
            if (prefix.EndsWith(_blankCharacters)) //Not correct if quotation is used
                tokens.Add(new Token(Token.Type.WORD, ""));
            if (tokens.Count > 0)
            {
                List<List<Token>> cmds = Utils.Split(tokens, _operators);
                if (cmds.Count > 0)
                {
                    tokens = cmds[cmds.Count - 1];
                    string quotedLabel = tokens[0].value;
                    Utils.RemoveQuoting(tokens);
                    label = tokens[0].value;
                    bypassAliases = quotedLabel != label;
                }
            }

            if (tokens.Count > 1)
            {
                options = new List<string>();
                if ((!bypassAliases && _aliases.ContainsKey(label)) || _builtinCmds.ContainsKey(label) || _cmds.ContainsKey(label) || _convars.ContainsKey(label) || _methods.ContainsKey(label))
                    options.Add(label);
                else
                    return "";
            }
            else
            {
                options = Utils.GetWordsThatStartWith(label, false,
                    _builtinCmds.Keys,
                    _cmds.Keys,
                    _convars.Keys,
                    _methods.Keys);

                if (!bypassAliases)
                    options.AddRange(Utils.GetWordsThatStartWith(label, false, _aliases.Keys));
            }

            if (options.Count == 0)
            {
                return "";
            }
            else if (options.Count == 1)
            {
                string labelFound = options[0];
                if (!bypassAliases && !_usedAliases.Contains(labelFound) && _aliases.TryGetValue(labelFound, out string aliasValue))
                {
                    _usedAliases.Push(labelFound);
                    prefix = prefix.ReplaceFirst(label, aliasValue);
                    string completion = labelFound.Remove(0, label.Length) + GetCompletion(prefix, out options);
                    _usedAliases.Pop();

                    return completion;
                }
                else if (_builtinCmds.TryGetValue(labelFound, out ICommand cmd) || _cmds.TryGetValue(labelFound, out cmd))
                {
                    string[] args = tokens.Count > 1 ? Utils.ExtractArguments(tokens) : new string[] { "" };
                    return labelFound.Remove(0, label.Length) + (tokens.Count > 1 ? "" : " ") + cmd.GetCompletion(labelFound, args, out options);
                }
                else
                {
                    if (tokens.Count > 1)
                        return "";
                    return labelFound.Remove(0, label.Length) + " ";
                }
            }
            else
                return Utils.GetLongestCommonPrefix(options).Remove(0, label.Length);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="input"></param>
        /// <param name="maxDistance"></param>
        /// <returns></returns>
        public List<string> GetSimilarCmds(string input, int maxDistance)
        {
            string label = "";
            List<Token> tokens = Utils.Tokenize(input, _operators, false);
            if (tokens.Count > 0)
            {
                List<List<Token>> cmds = Utils.Split(tokens, _operators);
                if (cmds.Count > 0)
                {
                    tokens = cmds[cmds.Count - 1];
                    Utils.RemoveQuoting(tokens);
                    label = tokens[0].value;
                }
            }

            return Utils.GetSimilarWords(label, true, maxDistance, true,
                _aliases.Keys,
                _builtinCmds.Keys,
                _cmds.Keys,
                _convars.Keys,
                _methods.Keys);
        }

        public string GetVariableValue(string name)
        {
            if (_variables.TryGetValue(name, out string value))
                return value;
                
            return "";
        }
        public void SetVariableValue(string name, string value)
        {
            if (_variables.ContainsKey(name))
                _variables[name] = value;
            else
                _variables.Add(name, value);
        }

        private void handleLog(string strLog, string stackTrace, LogType type)
        {
            Log log = new Log(type, strLog, stackTrace);
            foreach (IConsole console in _consoles.Values)
            {
                try
                {
                    console.AddLog(log);
                }
                catch { }
            }
        }
        #endregion

        #region BUILTIN COMMANDS
        public string[] GetSyntaxes(string label)
        {
            switch (label)
            {
                case "README":
                    return new string[] { "[page]" };
                case "echo":
                    return new string[] { "[message ...]" };
                case "alias":
                    return new string[] { "[name value]" };
                case "unalias":
                    return new string[] { "name" };
                case "history":
                    return new string[]
                    {
                        "[-c]",
                        "",
                        "-c"
                    };
                case "throw":
                    return new string[] { "[message]" };
                case "type":
                    return new string[] { "name" };
                case ".":
                case "source":
                    return new string[] { "file" };
                case "help":
                    return new string[]
                    {
                        "[-e] [command]",
                        "",
                        "-e",
                        "command"
                    };
                case "font":
                    return new string[]
                    {
                        "[-lD] [-rf [font-name]] [-d font-name]",
                        "",
                        "-l",
                        "-D",
                        "-r",
                        "-r font-name",
                        "-f",
                        "-f font-name",
                        "-d font-name"
                    };
                case "args":
                    return new string[] { "[arg ...]" };
                case "event":
                    return new string[]
                    {
                        "[-r event] [event cmd-line]",
                        "",
                        "-r event",
                        "event cmd-line",
                    };
            }

            return new string[0];
        }
        public string[] GetInfos(string label)
        {
            switch (label)
            {
                case "README":
                    return new string[] { "log the shell README" };
                case "echo":
                    return new string[] { "log a message" };
                case "alias":
                    return new string[] { "log and add aliases" };
                case "unalias":
                    return new string[] { "remove an alias" };
                case "history":
                    return new string[]
                    {
                        "manage the command history",
                        "log all the command history",
                        "clear the command history"
                    };
                case "help":
                    return new string[]
                    {
                        "log informations about registered commands",
                        "log all registered commands",
                        "synonym of ':help'",
                        "log all syntaxes of a command"
                    };
                case "version":
                    return new string[] { "log various versions" };
                case "throw":
                    return new string[] { "raise an exception with an optional message" };
                case "type":
                    return new string[] { "log the type of a command" };
                case ".":
                case "source":
                    return new string[] { "execute the content of a file as a command line" };
                case "font":
                    return new string[]
                    {
                        "manage shell fonts",
                        "list all available fonts",
                        "list all registered fonts",
                        "delete all registered fonts",
                        "list all font resources",
                        "load font resource",
                        "list all font files",
                        "load font file",
                        "delete a font"
                    };
                case "args":
                    return new string[] { "for debugging purpose" };
                case "convar":
                    return new string[] { "log all console variables" };
                case "event":
                    return new string[]
                    {
                        "manage events",
                        "log all registered events",
                        "remove all command lines associated with an event",
                        "associate a command line to an event. When the event will be raised, the command line will be executed",
                    };
            }
            return new string[0];
        }
        public string GetCompletion(string label, string[] args, out List<string> options)
        {
            if (label == "help")
                return Utils.GetCompletion(args[0], args.Length > 1 ? true : false, out options, _builtinCmds.Keys, _cmds.Keys, _methods.Keys);
            else if (label == "type")
                return Utils.GetCompletion(args[0], args.Length > 1 ? true : false, out options, _builtinCmds.Keys, _cmds.Keys, _aliases.Keys, _convars.Keys, _methods.Keys);
            else if (label == "unalias")
                return Utils.GetCompletion(args[0], args.Length > 1 ? true : false, out options, _aliases.Keys);
            else if (label == "event")
                return Utils.GetCompletion(args[0], args.Length > 1 ? true : false, out options, new string[] { "", "-r" }, _events.Keys);

            options = new List<string>();
            return "";
        }

        public void Execute(string label, string[] args)
        {
            switch (label)
            {
                case "README":
                    executeREADME(args);
                    break;
                case "echo":
                    executeEcho(args);
                    break;
                case "alias":
                    executeAlias(args);
                    break;
                case "unalias":
                    executeUnalias(args);
                    break;
                case "history":
                    executeHistory(args);
                    break;
                case "help":
                    executeHelp(args);
                    break;
                case "version":
                    executeVersion(args);
                    break;
                case "throw":
                    executeThrow(args);
                    break;
                case "type":
                    executeType(args);
                    break;
                case ".":
                case "source":
                    executeSource(args);
                    break;
                case "font":
                    executeFont(args);
                    break;
                case "args":
                    executeArgs(args);
                    break;
                case "convar":
                    executeConvar(args);
                    break;
                case "event":
                    executeEvent(args);
                    break;
                case "reflex":
                    executeReflex(args);
                    break;
            }
        }

        private void executeREADME(string[] args)
        {
            if (_readme == null || _readme.Length == 0)
            {
                Debug.Log("README empty");
                return;
            }

            if (args.Length == 0)
                args = new string[] { "1" };
            
            if (args.Length == 1)
            {
                int page = Utils.IntParse(args[0]);
                int index = page - 1;
                if (index >= 0 && index < _readme.Length)
                {
                    string log = string.Format("THIS IS THE #{0} PAGE OF THE README\n\n", page);
                    log += _readme[index];
                    if (page == _readme.Length)
                        log += "\n\nEND";
                    else
                        log += string.Format("\n\nTO GO TO THE NEXT PAGE, TYPE 'README {0}'", page + 1);

                    Debug.Log(log);
                }
                else
                    Debug.LogWarning("the page does not exist");
            }
            else
                throw new ArgumentException();
        }
        private void executeEcho(string[] args)
        {
            Debug.Log(string.Join(" ", args));
        }
        private void executeAlias(string[] args)
        {
            if (args.Length == 0)
            {
                StringBuilder log = new StringBuilder();
                foreach (KeyValuePair<string, string> pair in _aliases)
                    log.Append(pair.Key + " -> " + pair.Value + "\n");

                Debug.Log(log);
            }
            else if (args.Length == 2)
            {
                if (_aliases.ContainsKey(args[0]))
                    _aliases[args[0]] = args[1];
                else
                    _aliases.Add(args[0], args[1]);
            }
            else
                throw new ArgumentException();
        }
        private void executeUnalias(string[] args)
        {
            if (args.Length == 1)
            {
                if (_aliases.ContainsKey(args[0]))
                    _aliases.Remove(args[0]);
                else
                    Debug.LogWarning("no entry for " + args[0]);
            }
            else
                throw new ArgumentException();
        }
        private void executeHistory(string[] args)
        {
            if (args.Length == 0)
            {
                int width = _history.Count.ToString().Length + 2;
                StringBuilder log = new StringBuilder();

                for (int i = _history.Count; i > 0; i--)
                    log.Append(string.Format("{0, " + width + "}  {1}\n", i, this.GetCmdLineFromHistory(i)));

                Debug.Log(log);
            }
            else if (args.Length == 1)
            {
                switch (args[0])
                {
                    case "-c":
                        _history.Clear();
                        break;
                    default:
                        throw new ArgumentException();
                }
            }
            else
                throw new ArgumentException();
        }
        private void executeHelp(string[] args)
        {
            StringBuilder strBuilder = new StringBuilder();
            ICommand cmd;
            List<MethodCmd> methods;

            if (args.Length == 0)
            {
                strBuilder.Append("commands: " + (_builtinCmds.Count + _cmds.Count));
                getHelp(strBuilder, _builtinCmds, true);
                getHelp(strBuilder, _cmds, false);
                getHelp(strBuilder, _methods);

                Debug.Log(strBuilder.ToString());
            }
            else if (args.Length == 1)
            {
                if (_builtinCmds.TryGetValue(args[0], out cmd))
                {
                    getHelpFromLabel(strBuilder, cmd, args[0]);
                    Debug.Log(strBuilder);
                }
                else if (_cmds.TryGetValue(args[0], out cmd))
                {
                    getHelpFromLabel(strBuilder, cmd, args[0]);
                    Debug.Log(strBuilder);
                }
                else if (_methods.TryGetValue(args[0], out methods))
                {
                    getHelpFromLabel(strBuilder, methods, args[0]);
                    Debug.Log(strBuilder);
                }
                else
                    Debug.LogWarning(args[0] + ": unknown command");
            }
            else
                throw new ArgumentException();
        }
        private void executeVersion(string[] args)
        {
            Debug.Log(
                "build   "   + Application.version +
                "\nushell  " + _version +
                "\nunity   " + Application.unityVersion +
                "\nclr     " + Environment.Version +
                "\nos      " + SystemInfo.operatingSystem)
            ;
        }
        private void executeThrow(string[] args)
        {
            throw new Exception(string.Join(" ", args));
        }
        private void executeType(string[] args)
        {
            string log;

            if (args.Length == 1)
            {
                if (_aliases.ContainsKey(args[0]))
                    log = "alias";
                else if (args[0][0] == ':')
                    log = "event";
                else if (_builtinCmds.ContainsKey(args[0]))
                    log = "builtin";
                else if (_cmds.ContainsKey(args[0]))
                    log = "regular";
                else if (_convars.ContainsKey(args[0]))
                    log = "convar";
                else if (_methods.ContainsKey(args[0]))
                    log = "method";
                else
                    log = "unknown";

                Debug.Log(log);
            }
            else
                throw new ArgumentException();
        }
        private void executeSource(string[] args)
        {
            if (args.Length == 1)
            {
                string scriptPath = args[0];
                if (File.Exists(scriptPath))
                {
                    try
                    {
                        string cmd = File.ReadAllText(scriptPath);
                        processCmdLine(scriptPath, cmd, false);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("error while reading '" + scriptPath + "' file");
                        Debug.LogException(e);
                    }
                }
                else
                    Debug.LogWarning("the file does not exist");
            }
            else
                throw new ArgumentException();
        }
        private void executeFont(string[] args)
        {
            //If we remove the font currently used, game over...

            //StringBuilder
            string log;
            Font font;
            UFont uFont;
            Font[] fonts;

            if (args.Length == 0)
            {
                //LIST ALL AVAILABLE FONTS 
                log = "BUILTIN     Arial";
                fonts = Resources.LoadAll<Font>("");
                foreach (Font fontt in fonts)
                    log += "\n" + "RESOURCE" + new String(' ', 12 - 8) + fontt.name;

                string[] fontNames = Font.GetOSInstalledFontNames();
                for (int i = 0; i < fontNames.Length; i++)
                    log += "\n" + "FILE" + new String(' ', 12 - 4) + fontNames[i];

                Debug.Log(log);
            }
            else if (args.Length == 1)
            {
                switch (args[0])
                {
                    case "-l": //LIST ALL REGISTERED FONTS
                        log = "";
                        foreach (UFont uFontt in _uFonts.Values)
                            log += uFontt.fontType + new String(' ', 12 - uFontt.fontType.ToString().Length) + uFontt.font.name + "\n";
                        Debug.Log(log);
                        break;
                    case "-r": //LIST ALL FONT RESOURCES
                        log = "";
                        fonts = Resources.LoadAll<Font>("");
                        foreach (Font fontt in fonts)
                            log += fontt.name + "\n";
                        Debug.Log(log);
                        break;
                    case "-f": //LIST ALL FONT FILES
                        log = "";
                        string[] fontNames = Font.GetOSInstalledFontNames();
                        string[] fontPaths = Font.GetPathsToOSFonts();
                        for (int i = 0; i < fontNames.Length; i++)
                            log += fontNames[i] + " (" + fontPaths[i] + ")\n";
                        Debug.Log(log);
                        break;
                    case "-D": //DELETE ALL REGISTERED FONTS
                        deleteAllFonts();
                        break;
                    default:
                        throw new ArgumentException();
                }
            }
            else if (args.Length == 2)
            {
                switch (args[0])
                {
                    case "-r": //LOAD FONT RESOURCE
                        font = Resources.Load<Font>(name);
                        if (font != null)
                        {
                            uFont = new UFont(FontType.RESOURCE, font);
                            _uFonts.Add(name, uFont);
                        }
                        break;
                    case "-f": //LOAD FONT FILE
                        font = Font.CreateDynamicFontFromOSFont(args[1], 1);
                        uFont = new UFont(FontType.FILE, font);
                        _uFonts.Add(args[1], uFont);
                        break;
                    case "-d": //DELETE FONT
                        if (_uFonts.ContainsKey(args[1]))
                        {
                            _uFonts[args[1]].Free();
                            _uFonts.Remove(args[1]);
                        }
                        break;
                    default:
                        throw new ArgumentException();
                }
            }
            else
                throw new ArgumentException();
        }
        private void executeArgs(string[] args)
        {
            StringBuilder log = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
                log.Append(string.Format("arg[{0}]: `{1}'\n", i, args[i]));

            Debug.Log(log);
        }
        private void executeConvar(string[] args)
        {
            StringBuilder log = new StringBuilder();
            log.Append("convars: " + _convars.Count);

            foreach (KeyValuePair<string, ConvarCmd> convar in _convars)
            {
                string convarType = convar.Value.IsStatic ? "-" : "+";
                log.Append("\n\t").Append(convarType).Append(" ").Append(convar.Key);

                if (convar.Value.CanWrite)
                    log.Append(string.Format(" [{0}]", convar.Value.Type.Name));

                if (!convar.Value.IsStatic)
                {
                    int instanceCount = 0;
                    Type declaringType = convar.Value.DeclaringType;

                    if (_instances.ContainsKey(declaringType))
                        instanceCount = _instances[declaringType].Count;
                        
                    log.Append(string.Format(" ({0})", instanceCount));
                }

                string info = convar.Value.Info;
                if (!string.IsNullOrEmpty(info))
                    log.Append(": ").Append(info);
            }

            Debug.Log(log.ToString());
        }
        private void executeEvent(string[] args)
        {
            EventCmd eventCmd;

            if (args.Length == 0)
            {
                StringBuilder log = new StringBuilder();
                log.Append("events: " + _events.Count);

                foreach (var @event in _events)
                {
                    string convarType = @event.Value.IsStatic ? "-" : "+";
                    log.Append("\n\t").Append(convarType).Append(" ").Append(@event.Key);

                    if (!@event.Value.IsStatic)
                    {
                        int instanceCount = 0;
                        Type declaringType = @event.Value.DeclaringType;

                        if (_instances.ContainsKey(declaringType))
                            instanceCount = _instances[declaringType].Count;

                        log.Append(string.Format(" ({0})", instanceCount));
                    }

                    string info = @event.Value.Info;
                    if (!string.IsNullOrEmpty(info))
                        log.Append(": ").Append(info);
                }

                Debug.Log(log.ToString());
            }
            else if (args.Length == 2)
            {
                switch (args[0])
                {
                    case "-r":
                        if (_events.TryGetValue(args[1], out eventCmd))
                            eventCmd.RemoveAllEventHandlers();
                        else
                            Debug.LogWarning(args[1] + ": unknown event");
                        break;
                    default:
                        if (_events.TryGetValue(args[0], out eventCmd))
                            eventCmd.AddEventHandler(args[1], _eventsAssemblyBuilder, _instances);
                        else
                            Debug.LogWarning(args[0] + ": unknown event");
                        break;
                }
            }
            else
                throw new ArgumentException();
        }
        private void executeReflex(string[] args)
        {
            if (args.Length == 0)
            {
                StringBuilder strBuilder = new StringBuilder();
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                    strBuilder.AppendLine(assemblies[i].FullName);

                Debug.Log(strBuilder);
            }
            else if (args.Length == 1)
            {
                StringBuilder strBuilder = new StringBuilder();
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].FullName.StartsWith(args[0]))
                    {
                        Type[] types = assemblies[i].GetTypes();
                        for (int j = 0; j < types.Length; j++)
                            strBuilder.AppendLine(types[j].FullName);
                        break;
                    }
                }

                Debug.Log(strBuilder);
            }
            else
                throw new ArgumentException();
        }

        private void getHelp(StringBuilder strBuilder, Dictionary<string, ICommand> cmds, bool isBuiltin)
        {
            foreach (KeyValuePair<string, ICommand> cmd in cmds)
            {
                strBuilder.Append("\n\t");

                if (isBuiltin)
                    strBuilder.Append("+ ");
                else
                    strBuilder.Append("- ");

                strBuilder.Append(cmd.Key);

                string[] syntaxes = cmd.Value.GetSyntaxes(cmd.Key);
                if (syntaxes != null && syntaxes.Length > 0 && !string.IsNullOrEmpty(syntaxes[0]))
                    strBuilder.Append(" ").Append(syntaxes[0]);
            }
        }
        private void getHelp(StringBuilder strBuilder, Dictionary<string, List<MethodCmd>> methods)
        {
            foreach (KeyValuePair<string, List<MethodCmd>> methodList in methods)
            {
                strBuilder.Append("\n\t- ");
                strBuilder.Append(methodList.Key);
            }
        }
        private void getHelpFromLabel(StringBuilder strBuilder, ICommand cmd, string label)
        {
            string[] syntaxes = cmd.GetSyntaxes(label);
            string[] infos = cmd.GetInfos(label);

            if (syntaxes.Length > infos.Length)
            {
                for (int i = 0; i < syntaxes.Length; i++)
                {
                    if (i >= infos.Length)
                        strBuilder.Append(label + " " + syntaxes[i] + "\n");
                    else
                        strBuilder.Append(label + " " + syntaxes[i] + ": " + infos[i] + "\n");
                }
            }
            else
            {
                for (int i = 0; i < infos.Length; i++)
                {
                    if (i >= syntaxes.Length)
                        strBuilder.Append(label + ": " + infos[i] + "\n");
                    else
                        strBuilder.Append(label + " " + syntaxes[i] + ": " + infos[i] + "\n");
                }
            }
        }
        private void getHelpFromLabel(StringBuilder strBuilder, List<MethodCmd> methods, string label)
        {
            foreach (MethodCmd method in methods)
            {
                strBuilder.Append(label);

                ParameterInfo[] parameterInfos = method.Parameters;
                for (int i = 0; i < parameterInfos.Length; i++)
                {
                    strBuilder.Append(" ");
                    if (parameterInfos[i].IsOut)
                        strBuilder.Append("[out]");
                    else if (parameterInfos[i].ParameterType.IsByRef && !parameterInfos[i].IsIn)
                        strBuilder.Append("[ref]");

                    strBuilder.Append(parameterInfos[i].Name + ":");
                    if (parameterInfos[i].ParameterType.IsByRef)
                        strBuilder.Append(parameterInfos[i].ParameterType.GetElementType().Name);
                    else
                        strBuilder.Append(parameterInfos[i].ParameterType.Name);
                }

                string info = method.Info;
                if (!string.IsNullOrEmpty(info))
                    strBuilder.Append(": " + info);

                strBuilder.Append("\n");
            }
        }
        #endregion
        #endregion

        #region TYPES
        private class History
        {
            private string[] _values = new string[10];
            private string _name = "history";
            private int _pos;
            private int _count;

            public int Count { get { return _count; } }

            public History() { }
            public History(int capacity)
            {
                _values = new string[capacity];
            }
            public History(string name)
            {
                _name = name;
            }
            public History(int capacity, string name)
            {
                _values = new string[capacity];
                _name = name;
            }

            public void Clear()
            {
                _pos = _count = 0;
            }

            public void SaveToDisk()
            {
                StringBuilder history = new StringBuilder();

                for (int i = _count; i >= 1; i--)
                    history.Append(GetValue(i) + "\n");

                try
                {
                    PlayerPrefs.SetString(_name, history.ToString());
                }
                catch (PlayerPrefsException e)
                {
                    Debug.LogError(e.Message);
                }
            }
            public void LoadFromDisk()
            {
                string history = PlayerPrefs.GetString(_name);
                string[] split = history.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < _values.Length; i++)
                {
                    if (i >= split.Length)
                        break;

                    _values[i] = split[i];
                }

                _count = Math.Min(split.Length, _values.Length);
                if (_count == _values.Length)
                    _pos = 0;
                else
                    _pos = _count;
            }

            public string GetValue(int index)
            {
                if (index <= 0 || index > _values.Length)
                    return null;

                int targetPos = (_pos - index + _values.Length) % _values.Length;
                return _values[targetPos];
            }
            public void AddValue(string value)
            {
                _values[_pos] = value;
                _pos = (_pos + 1) % _values.Length;
                _count = Math.Min(_values.Length, _count + 1);
            }
        }

        private struct ConvarCmd
        {
            private FieldInfo _fieldInfo;
            private PropertyInfo _propertyInfo;


            public bool IsValid { get => IsField ^ IsProperty; }
            public bool IsField { get => _fieldInfo != null; }
            public bool IsProperty { get => _propertyInfo != null; }

            public Type Type
            {
                get
                {
                    if (IsField) return _fieldInfo.FieldType;
                    if (IsProperty) return _propertyInfo.PropertyType;

                    return typeof(object);
                }
            }
            public Type DeclaringType
            {
                get
                {
                    if (IsField) return _fieldInfo.DeclaringType;
                    if (IsProperty) return _propertyInfo.DeclaringType;

                    return typeof(object);
                }
            }
            public bool IsStatic
            {
                get
                {
                    if (IsField)
                        return _fieldInfo.IsStatic;
                    if (IsProperty)
                    {
                        MethodInfo methodInfo = _propertyInfo.GetMethod;
                        if (methodInfo != null)
                            return methodInfo.IsStatic;
                    }

                    return true;
                }
            }

            public string Name
            {
                get
                {
                    if (IsField)
                    {
                        ConvarAttribute attribute = _fieldInfo.GetCustomAttribute<ConvarAttribute>();
                        if (attribute != null && !string.IsNullOrEmpty(attribute.Label))
                            return attribute.Label;
                        else
                            return _fieldInfo.Name;
                    }
                    if (IsProperty)
                    {
                        ConvarAttribute attribute = _propertyInfo.GetCustomAttribute<ConvarAttribute>();
                        if (attribute != null && !string.IsNullOrEmpty(attribute.Label))
                            return attribute.Label;
                        else
                            return _propertyInfo.Name;
                    }

                    return "";
                }
            }
            public string Info
            {
                get
                {
                    if (IsField)
                    {
                        ConvarAttribute attribute = _fieldInfo.GetCustomAttribute<ConvarAttribute>();
                        if (attribute != null && !string.IsNullOrEmpty(attribute.Info))
                            return attribute.Info;
                    }
                    if (IsProperty)
                    {
                        ConvarAttribute attribute = _propertyInfo.GetCustomAttribute<ConvarAttribute>();
                        if (attribute != null && !string.IsNullOrEmpty(attribute.Info))
                            return attribute.Info;
                    }

                    return "";
                }
            }
            public bool CanRead
            {
                get
                {
                    if (IsField)
                        return true;
                    if (IsProperty)
                        return _propertyInfo.CanRead;

                    return false;
                }
            }
            public bool CanWrite
            {
                get
                {
                    if (IsField)
                    {
                        ConvarAttribute attribute = _fieldInfo.GetCustomAttribute<ConvarAttribute>();
                        if (attribute != null)
                            return !attribute.ReadOnly;
                    }
                    if (IsProperty)
                    {
                        ConvarAttribute attribute = _propertyInfo.GetCustomAttribute<ConvarAttribute>();
                        if (attribute != null)
                            return _propertyInfo.CanWrite && !attribute.ReadOnly;
                    }

                    return false;
                }
            }


            public ConvarCmd(FieldInfo fieldInfo)
            {
                _fieldInfo = fieldInfo;
                _propertyInfo = null;
            }
            public ConvarCmd(PropertyInfo propertyInfo)
            {
                _fieldInfo = null;
                _propertyInfo = propertyInfo;
            }

            public object GetValue(object obj)
            {
                if (IsField)
                    return _fieldInfo.GetValue(obj);
                else if (IsProperty)
                    return _propertyInfo.GetValue(obj);

                return null;
            }
            public void SetValue(object obj, object value)
            {
                if (IsField)
                    _fieldInfo.SetValue(obj, value);
                else if (IsProperty)
                    _propertyInfo.SetValue(obj, value);
            }
        }
        private struct MethodCmd
        {
            private MethodInfo _method;
            private ParameterInfo[] _parameters;


            public Type ReturnType { get => _method.ReturnType; }
            public Type DeclaringType { get => _method.DeclaringType; }
            public bool IsStatic { get => _method.IsStatic; }
            public int ParametersCount { get => _parameters.Length; }
            public ParameterInfo[] Parameters { get => _parameters; }

            public string Name
            {
                get
                {
                    CmdAttribute attribute = _method.GetCustomAttribute<CmdAttribute>();
                    if (attribute != null && !string.IsNullOrEmpty(attribute.Label))
                        return attribute.Label;

                    return _method.Name;
                }
            }
            public string Info
            {
                get
                {
                    CmdAttribute attribute = _method.GetCustomAttribute<CmdAttribute>();
                    if (attribute != null && !string.IsNullOrEmpty(attribute.Info))
                        return attribute.Info;

                    return "";
                }
            }


            public MethodCmd(MethodInfo methodInfo)
            {
                _method = methodInfo;
                _parameters = _method.GetParameters();
            }

            public object Invoke(object obj, object[] parameters)
            {
                return _method.Invoke(obj, parameters);
            }
        }
        private struct EventCmd
        {
            private EventInfo _event;
            private MethodInfo _invoke;
            private ParameterInfo[] _parameters;
            private MethodInfo _addMethod;

            private List<Tuple<object, Delegate>> _targetsAndHandlers;


            public Type ReturnType { get => _invoke.ReturnType; }
            public Type DeclaringType { get => _event.DeclaringType; }
            public Type EventHandlerType { get => _event.EventHandlerType; }
            public bool IsStatic { get => _addMethod.IsStatic; }
            public int ParametersCount { get => _parameters.Length; }
            public Type[] ParametersTypes
            {
                get
                {
                    Type[] paramTypes = new Type[_parameters.Length];
                    for (int j = 0; j < _parameters.Length; j++)
                        paramTypes[j] = _parameters[j].ParameterType;

                    return paramTypes;
                }
            }

            public string Name
            {
                get
                {
                    EventAttribute attribute = _event.GetCustomAttribute<EventAttribute>();
                    if (attribute != null && !string.IsNullOrEmpty(attribute.Label))
                        return attribute.Label;

                    return _event.Name;
                }
            }
            public string Info
            {
                get
                {
                    EventAttribute attribute = _event.GetCustomAttribute<EventAttribute>();
                    if (attribute != null && !string.IsNullOrEmpty(attribute.Info))
                        return attribute.Info;

                    return "";
                }
            }


            public EventCmd(EventInfo eventInfo)
            {
                _event = eventInfo;
                _invoke = eventInfo.EventHandlerType.GetMethod("Invoke");
                _parameters = _invoke.GetParameters();
                _addMethod = eventInfo.GetAddMethod();

                _targetsAndHandlers = new List<Tuple<object, Delegate>>();
            }

            public void AddEventHandler(string cmdLine, AssemblyBuilder assemblyBuilder, Dictionary<Type, List<object>> instances)
            {
                ModuleBuilder mb = assemblyBuilder.DefineDynamicModule(this.Name + "_" + Environment.TickCount); //Environment.TickCount: the module name must be unique
                MethodBuilder meb = mb.DefineGlobalMethod(this.Name, MethodAttributes.Public | MethodAttributes.Static, this.ReturnType, this.ParametersTypes);
                ILGenerator il = meb.GetILGenerator();

                #region CIL
                MethodInfo processCmdLineMethod = typeof(Shell).GetMethod(nameof(Shell.ProcessCmdLine), new Type[] { typeof(string), typeof(string) });
                MethodInfo mainPropertyMethod = typeof(Shell).GetProperty(nameof(Shell.Main), typeof(Shell)).GetMethod;
                MethodInfo setVariableValueMethod = typeof(Shell).GetMethod(nameof(Shell.SetVariableValue), BindingFlags.Public | BindingFlags.Instance);
                MethodInfo convertToStringMethod = typeof(Utils).GetMethod(nameof(Utils.ConvertToString), BindingFlags.Public | BindingFlags.Static);

                il.EmitCall(OpCodes.Call, mainPropertyMethod, null);
                il.Emit(OpCodes.Ldstr, "0");
                il.Emit(OpCodes.Ldstr, this.Name);
                il.EmitCall(OpCodes.Call, setVariableValueMethod, null);

                for (int i = 0; i < this.ParametersTypes.Length; i++)
                {
                    il.EmitCall(OpCodes.Call, mainPropertyMethod, null);
                    il.Emit(OpCodes.Ldstr, (i + 1).ToString());
                    il.Emit(OpCodes.Ldarg, i);
                    if (this.ParametersTypes[i].IsValueType)
                        il.Emit(OpCodes.Box, this.ParametersTypes[i]);
                    il.EmitCall(OpCodes.Call, convertToStringMethod, null);
                    il.EmitCall(OpCodes.Call, setVariableValueMethod, null);
                }

                il.EmitCall(OpCodes.Call, mainPropertyMethod, null);
                il.Emit(OpCodes.Ldstr, this.Name);
                il.Emit(OpCodes.Ldstr, cmdLine);
                il.EmitCall(OpCodes.Call, processCmdLineMethod, null);
                if (this.ReturnType != typeof(void))
                {
                    //There is currently no way of knowing what a command returns, so we return 0 or null
                    if (this.ReturnType.IsValueType) il.Emit(OpCodes.Ldc_I4_0);
                    else il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Ret);
                #endregion CIL

                mb.CreateGlobalFunctions();
                MethodInfo eventHandler = mb.GetMethod(this.Name);
                Delegate handler = Delegate.CreateDelegate(this.EventHandlerType, eventHandler);
                foreach (var target in instances[this.DeclaringType])
                {
                    _event.AddEventHandler(target, handler);
                    _targetsAndHandlers.Add(new Tuple<object, Delegate>(target, handler));
                }
            }
            public void RemoveAllEventHandlers()
            {
                foreach (var e in _targetsAndHandlers)
                    _event.RemoveEventHandler(e.Item1, e.Item2);
            }
        }
        #endregion

        #region FONTS
        private enum FontType
        {
            BUILTIN,
            RESOURCE,
            FILE
        }
        private class UFont
        {
            public readonly FontType fontType;
            public readonly Font font;

            public UFont(FontType fontType, Font font)
            {
                this.fontType = fontType;
                this.font = font;
            }
            public void Free()
            {
                if (fontType == FontType.RESOURCE)
                    Resources.UnloadAsset(font);
                else if (fontType == FontType.FILE)
                    Font.Destroy(font);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Font GetFont(string name)
        {
            Font font;
            UFont uFont;

            if (_uFonts.ContainsKey(name))
                return _uFonts[name].font;

            if (name == "Arial")
            {
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (font != null)
                {
                    uFont = new UFont(FontType.BUILTIN, font);
                    _uFonts.Add(name, uFont);
                    return font;
                }
            }

            font = Resources.Load<Font>(name);
            if (font != null)
            {
                uFont = new UFont(FontType.RESOURCE, font);
                _uFonts.Add(name, uFont);
                return font;
            }

            string[] fontNames = Font.GetOSInstalledFontNames();
            if (Array.IndexOf<string>(fontNames, name) > 0)
            {
                font = Font.CreateDynamicFontFromOSFont(name, 1);
                uFont = new UFont(FontType.FILE, font);
                _uFonts.Add(name, uFont);
                return font;
            }

            return null;
        }
        private void deleteAllFonts()
        {
            foreach (UFont uFont in _uFonts.Values)
                uFont.Free();

            _uFonts.Clear();
        }
        #endregion
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class ConvarAttribute : Attribute
    {
        public string Label { get; }
        public string Info { get; }
        public bool ReadOnly { get; }

        public ConvarAttribute() { }
        public ConvarAttribute(string label)
        {
            this.Label = label;
        }
        public ConvarAttribute(string label, string info)
        {
            this.Label = label;
            this.Info = info;
        }
        public ConvarAttribute(string label, string info, bool readOnly)
        {
            this.Label = label;
            this.Info = info;
            this.ReadOnly = readOnly;
        }
    }
    [AttributeUsage(AttributeTargets.Method)]
    public class CmdAttribute : Attribute
    {
        public string Label { get; }
        public string Info { get; }

        public CmdAttribute() { }
        public CmdAttribute(string label)
        {
            this.Label = label;
        }
        public CmdAttribute(string label, string info)
        {
            this.Label = label;
            this.Info = info;
        }
    }
    [AttributeUsage(AttributeTargets.Event)]
    public class EventAttribute : Attribute
    {
        public string Label { get; }
        public string Info { get; }

        public EventAttribute() { }
        public EventAttribute(string label)
        {
            this.Label = label;
        }
        public EventAttribute(string label, string info)
        {
            this.Label = label;
            this.Info = info;
        }
    }

    public interface IConsole
    {
        void Init(bool headless);
        void AddLog(Log log);
        bool ProcessEvent(string label, string[] args);
    }
    public interface ICommand
    {
        string[] GetSyntaxes(string label);
        string[] GetInfos(string label);
        string GetCompletion(string label, string[] args, out List<string> options);

        void Execute(string label, string[] args);
    }

    public class Log
    {
        public LogType LogType { get; }
        public string Value { get; }
        public string StackTrace { get; }

        public Log(LogType logType, string value, string stackTrace)
        {
            LogType = logType;
            Value = value;
            StackTrace = stackTrace;
        }
    }
}