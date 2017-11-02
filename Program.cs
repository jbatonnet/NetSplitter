using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

using log4net;
using log4net.Core;
using Newtonsoft.Json.Linq;
using PInvoke;

namespace NetSplitter
{
    public enum SplitterMode
    {
        Tcp,
        Http
    }
    public enum BalancingMode
    {
        Random,
        IPHash,
        LeastConn
    }

    class DefaultLogger : ILog
    {
        public bool IsDebugEnabled => true;
        public bool IsErrorEnabled => true;
        public bool IsFatalEnabled => true;
        public bool IsInfoEnabled => true;
        public bool IsWarnEnabled => true;

        public ILogger Logger => null;

        public void Debug(object message)
        {
            Log("[D] " + message);
        }
        public void Debug(object message, Exception exception) => Debug((message.ToString() + " " + exception));
        public void DebugFormat(string format, object arg0) => Debug(string.Format(format, arg0));
        public void DebugFormat(string format, params object[] args) => Debug(string.Format(format, args));
        public void DebugFormat(IFormatProvider provider, string format, params object[] args) => Debug(string.Format(provider, format, args));
        public void DebugFormat(string format, object arg0, object arg1) => Debug(string.Format(format, arg0, arg1));
        public void DebugFormat(string format, object arg0, object arg1, object arg2) => Debug(string.Format(format, arg0, arg1, arg2));

        public void Error(object message)
        {
            Log("[E] " + message);
        }
        public void Error(object message, Exception exception) => Error((message.ToString() + " " + exception));
        public void ErrorFormat(string format, object arg0) => Error(string.Format(format, arg0));
        public void ErrorFormat(string format, params object[] args) => Error(string.Format(format, args));
        public void ErrorFormat(IFormatProvider provider, string format, params object[] args) => Error(string.Format(provider, format, args));
        public void ErrorFormat(string format, object arg0, object arg1) => Error(string.Format(format, arg0, arg1));
        public void ErrorFormat(string format, object arg0, object arg1, object arg2) => Error(string.Format(format, arg0, arg1, arg2));

        public void Fatal(object message)
        {
            Log("[F] " + message);
        }
        public void Fatal(object message, Exception exception) => Fatal((message.ToString() + " " + exception));
        public void FatalFormat(string format, object arg0) => Fatal(string.Format(format, arg0));
        public void FatalFormat(string format, params object[] args) => Fatal(string.Format(format, args));
        public void FatalFormat(IFormatProvider provider, string format, params object[] args) => Fatal(string.Format(provider, format, args));
        public void FatalFormat(string format, object arg0, object arg1) => Fatal(string.Format(format, arg0, arg1));
        public void FatalFormat(string format, object arg0, object arg1, object arg2) => Fatal(string.Format(format, arg0, arg1, arg2));

        public void Info(object message)
        {
            Log("[I] " + message);
        }
        public void Info(object message, Exception exception) => Info((message.ToString() + " " + exception));
        public void InfoFormat(string format, object arg0) => Info(string.Format(format, arg0));
        public void InfoFormat(string format, params object[] args) => Info(string.Format(format, args));
        public void InfoFormat(IFormatProvider provider, string format, params object[] args) => Info(string.Format(provider, format, args));
        public void InfoFormat(string format, object arg0, object arg1) => Info(string.Format(format, arg0, arg1));
        public void InfoFormat(string format, object arg0, object arg1, object arg2) => Info(string.Format(format, arg0, arg1, arg2));

        public void Warn(object message)
        {
            Log("[W] " + message);
        }
        public void Warn(object message, Exception exception) => Warn((message.ToString() + " " + exception));
        public void WarnFormat(string format, object arg0) => Warn(string.Format(format, arg0));
        public void WarnFormat(string format, params object[] args) => Warn(string.Format(format, args));
        public void WarnFormat(IFormatProvider provider, string format, params object[] args) => Warn(string.Format(provider, format, args));
        public void WarnFormat(string format, object arg0, object arg1) => Warn(string.Format(format, arg0, arg1));
        public void WarnFormat(string format, object arg0, object arg1, object arg2) => Warn(string.Format(format, arg0, arg1, arg2));

        private object mutex = new object();

        private void Log(string message)
        {
            lock (mutex)
            {
                Console.WriteLine(message);

                try
                {
                    File.AppendAllText("NetSplitter.log", message + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    public static class Program
    {
        private const int defaultBufferSize = 1 * 1024 * 1024;
        private const int defaultTimeout = 1000;

        private const string settingsFileName = "Settings.json";

        public static SplitterMode SplitterMode { get; private set; } = SplitterMode.Tcp;
        public static BalancingMode BalancingMode { get; private set; } = BalancingMode.LeastConn;

        public static ushort Port { get; private set; } = 0;
        public static ulong BufferSize { get; private set; } = defaultBufferSize;
        public static TimeSpan Timeout { get; private set; } = TimeSpan.FromMilliseconds(defaultTimeout);

        private static readonly ILog logger = new DefaultLogger(); // LogManager.GetLogger(typeof(Program));
        private static FileSystemWatcher fileSystemWatcher;
        private static Kernel32.HandlerRoutine consoleCtrlHandler;

        private static Dictionary<HostInfo, double> balancingTargets = new Dictionary<HostInfo, double>();
        private static List<HostInfo> cloningTargets = new List<HostInfo>();
        private static Splitter splitter;

        private static bool running = true;
        private static double balancingTargetsTotal;
        private static Random balancingTargetsRandom = new Random();

        private static ConcurrentDictionary<HostInfo, int> currentBalancing = new ConcurrentDictionary<HostInfo, int>();
        private static ConcurrentDictionary<HostInfo, HostInfo> activeConnections = new ConcurrentDictionary<HostInfo, HostInfo>();

        static void Main(string[] args)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            Console.Title = $"NetSplitter {version}";

            logger.Info($"--- NetSplitter {Assembly.GetExecutingAssembly().GetName().Version} ---");
            logger.Debug("");

            // Monitor settings file changes
            fileSystemWatcher = new FileSystemWatcher(Environment.CurrentDirectory, settingsFileName);
            fileSystemWatcher.Changed += (s, e) => OnReloadConfiguration();
            fileSystemWatcher.Created += (s, e) => OnReloadConfiguration();
            fileSystemWatcher.NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName;
            fileSystemWatcher.EnableRaisingEvents = true;

            OnReloadConfiguration();
            if (Port == 0 || splitter == null)
            {
                if (Debugger.IsAttached)
                {
                    Console.WriteLine("Press any key to continue . . . ");
                    Console.ReadKey(true);
                }

                return;
            }

            // Listen to control console handler
            consoleCtrlHandler = eventType =>
            {
                if (running)
                {
                    logger.Warn("Stopping NetSplitter, waiting for active connections to stop. Use Ctrl+C again to force quit");

                    running = false;
                    splitter.Stop();

                    return false;
                }
                else
                {
                    Environment.Exit(0);
                    return true;
                }
            };

            Console.CancelKeyPress += (s, e) => e.Cancel = !consoleCtrlHandler(0);
            //if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            //    Kernel32.SetConsoleCtrlHandler(consoleCtrlHandler, true);
            
            // Wait for all connections to stop
            while (running || activeConnections.Count > 0)
                Thread.Sleep(100);
        }

        private static void OnReloadConfiguration()
        {
            if (!running)
                return;

            JObject settingsObject;

            // Load JSON settings object
            try
            {
                if (!File.Exists(settingsFileName))
                    throw new Exception($"Could not find {settingsFileName} file");

                string settingsJson;

                using (FileStream fileStream = File.Open(settingsFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader streamReader = new StreamReader(fileStream))
                    settingsJson = streamReader.ReadToEnd();

                settingsObject = JObject.Parse(settingsJson);
            }
            catch (Exception ex)
            {
                logger.Warn($"Error while reloading {settingsFileName}. Last settings will be kept. " + ex);
                return;
            }

            try
            {
                // Parse settings
                Dictionary<HostInfo, double> newBalancingTargets = new Dictionary<HostInfo, double>();

                JArray balancingArray = settingsObject["Balancing"] as JArray;
                if (balancingArray != null)
                {
                    foreach (JObject targetObject in balancingArray)
                    {
                        string hostname = (targetObject["Hostname"] as JValue)?.Value<string>();
                        ushort? port = (targetObject["Port"] as JValue)?.Value<ushort>();
                        double weight = (targetObject["Weight"] as JValue)?.Value<double>() ?? 1;
                        bool enabled = (targetObject["Enabled"] as JValue)?.Value<bool>() ?? true;

                        if (!enabled)
                            continue;
                        if (string.IsNullOrEmpty(hostname) || (port ?? 0) == 0)
                            throw new FormatException("One balancing target is not formatted correctly");

                        newBalancingTargets.Add(new HostInfo(hostname, port.Value), weight);
                    }
                }

                if (newBalancingTargets.Count == 0)
                    logger.Info("There are no balancing target defined. All connections will be skipped");

                List<HostInfo> newCloningTargets = new List<HostInfo>();

                JArray cloningArray = settingsObject["Cloning"] as JArray;
                if (cloningArray != null)
                {
                    foreach (JObject targetObject in cloningArray)
                    {
                        string hostname = (targetObject["Hostname"] as JValue)?.Value<string>();
                        ushort? port = (targetObject["Port"] as JValue)?.Value<ushort>();
                        bool enabled = (targetObject["Enabled"] as JValue)?.Value<bool>() ?? true;

                        if (!enabled)
                            continue;
                        if (string.IsNullOrEmpty(hostname) || (port ?? 0) == 0)
                            throw new FormatException("One cloning target is not formatted correctly");

                        newCloningTargets.Add(new HostInfo(hostname, port.Value));
                    }
                }

                settingsObject = settingsObject["Settings"] as JObject;
                if (settingsObject == null)
                    throw new Exception("Settings must be defined");

                ushort newPort = (settingsObject["Port"] as JValue)?.Value<ushort>() ?? 0;
                ulong newBufferSize = (settingsObject["BufferSize"] as JValue)?.Value<ulong>() ?? defaultBufferSize;
                int newTimeoutMs = (settingsObject["Timeout"] as JValue)?.Value<int>() ?? defaultTimeout;

                if (newPort == 0)
                    throw new Exception("A listening port must be defined");

                string newSplitterModeString = (settingsObject["Mode"] as JValue)?.Value<string>();
                SplitterMode newSplitterMode;

                if (string.IsNullOrEmpty(newSplitterModeString))
                {
                    if (newPort == 80 || newPort == 8080)
                        newSplitterMode = SplitterMode.Http;
                    else
                        newSplitterMode = SplitterMode.Tcp;
                }
                else if (!Enum.TryParse(newSplitterModeString, true, out newSplitterMode))
                    throw new Exception("Invalid splitter mode specified");

                string newBalancingModeString = (settingsObject["Balancing"] as JValue)?.Value<string>();
                BalancingMode newBalancingMode;

                if (string.IsNullOrEmpty(newBalancingModeString))
                {
                    if (newSplitterMode == SplitterMode.Http)
                        newBalancingMode = BalancingMode.IPHash;
                    else
                        newBalancingMode = BalancingMode.LeastConn;
                }
                else if (!Enum.TryParse(newBalancingModeString, true, out newBalancingMode))
                    throw new Exception("Invalid balancing mode specified");

                // Apply settings if needed
                if (newBalancingMode != BalancingMode)
                {
                    BalancingMode = newBalancingMode;
                    if (Port != 0)
                        logger.Info($"Setting balancing mode: {BalancingMode}");
                }

                if (newBufferSize != BufferSize)
                {
                    BufferSize = newBufferSize;
                    logger.Info($"Setting buffer size: {BufferSize}");
                }

                if (newTimeoutMs != (int)Timeout.TotalMilliseconds)
                {
                    Timeout = TimeSpan.FromMilliseconds(newTimeoutMs);
                    logger.Info($"Setting timeout: {Timeout}");
                }

                Func<Dictionary<HostInfo, double>, int> balancingTargetHasher = b =>
                {
                    int hash = 0x12345678;

                    foreach (var pair in b)
                    {
                        hash <<= 8;
                        hash ^= pair.Key.GetHashCode();
                        hash <<= 8;
                        hash ^= pair.Value.GetHashCode();
                    }

                    return hash;
                };
                if (balancingTargetHasher(balancingTargets) != balancingTargetHasher(newBalancingTargets))
                {
                    Interlocked.Exchange(ref balancingTargets, newBalancingTargets);
                    logger.Info($"{newBalancingTargets.Count} balancing targets:");

                    foreach (var pair in newBalancingTargets)
                        logger.Info($"- {pair.Key} (Weight: {pair.Value})");

                    balancingTargetsTotal = newBalancingTargets.Sum(p => p.Value);
                    foreach (var pair in newBalancingTargets)
                        currentBalancing.GetOrAdd(pair.Key, 0);
                }

                Func<List<HostInfo>, int> cloningTargetHasher = b =>
                {
                    int hash = 0x07654321;

                    foreach (HostInfo hostInfo in b)
                    {
                        hash <<= 8;
                        hash ^= hostInfo.GetHashCode();
                    }

                    return hash;
                };
                if (cloningTargetHasher(cloningTargets) != cloningTargetHasher(newCloningTargets))
                {
                    Interlocked.Exchange(ref cloningTargets, newCloningTargets);
                    logger.Info($"{newCloningTargets.Count} cloning targets");

                    foreach (HostInfo hostInfo in newCloningTargets)
                        logger.Info($"- {hostInfo}");
                }

                if (newPort != Port || newSplitterMode != SplitterMode)
                {
                    logger.Info($"Starting {newSplitterMode} splitter on port {newPort}");

                    splitter?.Stop();

                    try
                    {
                        Splitter newSplitter = null;

                        switch (newSplitterMode)
                        {
                            case SplitterMode.Tcp: newSplitter = new TcpSplitter(newPort, TargetBalancer, TargetCloner); break;
                            case SplitterMode.Http: newSplitter = new HttpSplitter(newPort, TargetBalancer, TargetCloner); break;
                        }

                        splitter = newSplitter;
                    }
                    catch
                    {
                        splitter?.Start();
                        throw;
                    }

                    splitter.HostConnected += Splitter_HostConnected;
                    splitter.HostDisconnected += Splitter_HostDisconnected;

                    splitter.Start();

                    Port = newPort;
                    SplitterMode = newSplitterMode;
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Error while parsing {settingsFileName}. Last settings will be kept. " + ex);
                return;
            }
        }

        private static void Splitter_HostConnected(object sender, HostInfo source)
        {
        }
        private static void Splitter_HostDisconnected(object sender, HostInfo source)
        {
            HostInfo target;
            activeConnections.TryRemove(source, out target);

            int targetConnections;
            lock (currentBalancing)
            {
                targetConnections = currentBalancing[target] - 1;
                currentBalancing[target] = targetConnections;
            }

            logger.Info($"{source} disconnected from {target} ({activeConnections.Count} total, {targetConnections} on target)");
        }

        private static HostInfo TargetBalancer(HostInfo source)
        {
            if (balancingTargets.Count == 0)
                return null;
            if (balancingTargets.Count == 1)
                return balancingTargets.Keys.First();

            HostInfo target = null;
            Func<Random, HostInfo> randomSelector = random =>
            {
                double value = random.NextDouble() * balancingTargetsTotal;
                double current = 0;

                foreach (var pair in balancingTargets)
                {
                    current += pair.Value;

                    if (value <= current)
                        return pair.Key;
                }

                return null;
            };

            if (BalancingMode == BalancingMode.Random)
            {
                target = randomSelector(balancingTargetsRandom);
            }
            else if (BalancingMode == BalancingMode.IPHash)
            {
                int hash = source.Hostname.GetHashCode();
                Random random = new Random(hash);

                target = randomSelector(random);
            }
            else if (BalancingMode == BalancingMode.LeastConn)
            {
                target = balancingTargets.OrderBy(p => currentBalancing[p.Key] / p.Value).FirstOrDefault().Key;
            }

            if (target != null)
            {
                activeConnections[source] = target;

                int targetConnections;
                lock (currentBalancing)
                {
                    targetConnections = currentBalancing[target] + 1;
                    currentBalancing[target] = targetConnections;
                }

                logger.Info($"{source} connected to {target} ({activeConnections.Count} total, {targetConnections} on target)");
            }
            else
            {
                logger.Info($"No balancing target, {source} will be skipped");
            }

            return target;
        }
        private static IEnumerable<HostInfo> TargetCloner(HostInfo source)
        {
            return cloningTargets;
        }
    }
}
