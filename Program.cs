using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using log4net;
using log4net.Core;

namespace NetSplitter
{
    public class TargetInfo
    {
        public string Hostname { get; }
        public ushort Port { get; }

        public TargetInfo(string hostname, ushort port)
        {
            Hostname = hostname;
            Port = port;
        }
    }

    public class TargetConnection
    {
        public TargetInfo Target { get; }
        public TcpClient Connection { get; }
        public NetworkStream Stream { get; }

        public ConcurrentQueue<ArraySegment<byte>> Buffers { get; } = new ConcurrentQueue<ArraySegment<byte>>();

        public TargetConnection(TargetInfo target, TcpClient connection, NetworkStream stream)
        {
            Target = target;
            Connection = connection;
            Stream = stream;
        }
    }
    public class ClientConnection
    {
        public TcpClient Connection { get; }
        public NetworkStream Stream { get; }

        public TargetConnection MainTarget { get; }
        public Dictionary<TargetInfo, TargetConnection> OutputTargets { get; }

        public ClientConnection(TcpClient connection, NetworkStream stream, TargetConnection mainTarget, Dictionary<TargetInfo, TargetConnection> outputTargets)
        {
            Connection = connection;
            Stream = stream;
            MainTarget = mainTarget;
            OutputTargets = outputTargets;
        }
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

    class Program
    {
        private delegate bool ConsoleEventDelegate(int eventType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        private const int bufferSize = 32 * 1024;
        private const string settingsFileName = "Settings.xml";

        private static readonly ILog logger = new DefaultLogger(); // LogManager.GetLogger(typeof(Program));
        private static FileSystemWatcher fileSystemWatcher;
        private static TcpListener tcpListener;
        private static ConsoleEventDelegate consoleCtrlHandler;

        private static List<ClientConnection> activeConnections = new List<ClientConnection>();

        private static bool settingsLoaded = false;
        private static ushort currentPort = 0;
        private static ulong currentBufferSize = 1 * 1024 * 1024;
        private static TimeSpan currentTimeout = TimeSpan.FromSeconds(5);
        private static TargetInfo currentMainTarget;
        private static TargetInfo[] currentOutputTargets;

        private static bool exiting = false;

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
            if (!settingsLoaded)
                return;

            // Listen to control console handler
            consoleCtrlHandler = eventType =>
            {
                if (!exiting)
                {
                    logger.Info("Stopping TCP listener, waiting for active connections to stop. Use Ctrl+C again to force quit");

                    exiting = true;
                    tcpListener.Stop();

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
            //    SetConsoleCtrlHandler(consoleCtrlHandler, true);

            // Create listener
            try
            {
                logger.Info($"Starting listener on port {currentPort} ...");

                tcpListener = new TcpListener(IPAddress.Any, currentPort);
                tcpListener.Start();

                logger.Info("Listener started successfully");

                tcpListener.BeginAcceptTcpClient(OnTcpConnection, null);
            }
            catch (Exception e)
            {
                logger.Error($"Error while starting TCP listener on port {currentPort}. " + e);
            }

            // Wait for all connections to stop
            while (!exiting || activeConnections.Count > 0)
                Thread.Sleep(100);
        }

        private static void OnReloadConfiguration()
        {
            ushort port = 0;
            ulong bufferSize = currentBufferSize;
            TimeSpan timeout = currentTimeout;
            TargetInfo mainTarget;
            List<TargetInfo> outputTargets = new List<TargetInfo>();

            logger.Info($"Reloading {settingsFileName} ...");

            try
            {
                if (!File.Exists(settingsFileName))
                    throw new Exception($"Could not find {settingsFileName} file");

                // Load settings
                XDocument settingsDocument;

                try
                {
                    using (FileStream stream = File.Open(settingsFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        settingsDocument = XDocument.Load(stream);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Exception while loading {settingsFileName} file", ex);
                }

                {
                    XElement settingsElement = settingsDocument.Root.Element("Settings");
                    if (settingsDocument == null)
                        throw new Exception("You must provide a Settings section");

                    XElement portElement = settingsElement.Element("Port");
                    if (portElement == null)
                        throw new Exception("You must provide a Settings/Port element");

                    if (!ushort.TryParse(portElement.Value, out port))
                        throw new Exception("The port must be a positive number < 65536");

                    if (settingsLoaded && (port != currentPort))
                        throw new Exception("Can't live change listening port");
                    
                    XElement targetElement = settingsElement.Element("Target");
                    if (targetElement == null)
                        throw new Exception("You must provide a Settings/Target element");

                    mainTarget = ReadTarget(targetElement);
                }

                {
                    XElement outputElement = settingsDocument.Root.Element("Output");
                    if (outputElement != null)
                    {
                        XAttribute bufferSizeAttribute = outputElement.Attribute("BufferSize");
                        if (bufferSizeAttribute != null)
                        {
                            if (!ulong.TryParse(bufferSizeAttribute.Value, out bufferSize))
                                throw new Exception("The specified BufferSize must be a positive integer number");
                        }

                        XAttribute timeoutAttribute = outputElement.Attribute("Timeout");
                        if (timeoutAttribute != null)
                        {
                            ulong timeoutValue;
                            if (!ulong.TryParse(timeoutAttribute.Value, out timeoutValue))
                                throw new Exception("The specified Timeout must be a positive integer number");

                            timeout = TimeSpan.FromMilliseconds(timeoutValue);
                        }

                        XElement[] targetElements = outputElement.Elements("Target")?.ToArray();
                        if (targetElements != null)
                        {
                            foreach (XElement targetElement in targetElements)
                                outputTargets.Add(ReadTarget(targetElement));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Error while reloading {settingsFileName}. Last settings will be kept. " + ex);
                return;
            }

            // Apply new settings
            currentPort = port;
            if (!settingsLoaded)
                logger.Info($"Setting Port to {port}");

            if (currentMainTarget == null || currentMainTarget.Hostname != mainTarget.Hostname || currentMainTarget.Port != mainTarget.Port)
            {
                logger.Info($"Setting MainTarget to {mainTarget.Hostname}:{mainTarget.Port}");
                currentMainTarget = mainTarget;
            }

            if (bufferSize != currentBufferSize)
            {
                logger.Info($"Setting BufferSize to {bufferSize}");
                currentBufferSize = bufferSize;
            }

            if (timeout != currentTimeout)
            {
                logger.Info($"Setting Timeout to {timeout}");
                currentTimeout = timeout;
            }

            logger.Info($"Updating OutputTargets ({outputTargets?.Count ?? 0} items)");
            currentOutputTargets = outputTargets?.ToArray() ?? new TargetInfo[0];

            settingsLoaded = true;
        }
        private static void OnTcpConnection(IAsyncResult asyncResult)
        {
            if (exiting)
                return;

            try
            {
                // Accept client
                TcpClient client = tcpListener.EndAcceptTcpClient(asyncResult);
                EndPoint clientEndPoint = client.Client.RemoteEndPoint;

                new Thread(() =>
                {
                    // Connect to main server
                    TargetInfo mainTargetInfo = currentMainTarget;
                    TargetConnection mainTarget;

                    try
                    {
                        TcpClient mainTargetConnection = new TcpClient(mainTargetInfo.Hostname, mainTargetInfo.Port);
                        mainTarget = new TargetConnection(mainTargetInfo, mainTargetConnection, mainTargetConnection.GetStream());
                    }
                    catch (Exception e)
                    {
                        logger.Warn($"Could not connect to output {mainTargetInfo.Hostname}:{mainTargetInfo.Port}. Skipping connection. {e}");
                        return;
                    }

                    Dictionary<TargetInfo, TargetConnection> outputTargets = currentOutputTargets.ToDictionary<TargetInfo, TargetInfo, TargetConnection>(t => t, t => null);

                    ClientConnection clientConnection = new ClientConnection(client, client.GetStream(), mainTarget, outputTargets);

                    lock (activeConnections)
                        activeConnections.Add(clientConnection);

                    logger.Info($"Got connection from {clientEndPoint}, {activeConnections.Count} active connections");

                    // Connect to output targets
                    foreach (var outputInfo in clientConnection.OutputTargets.ToArray())
                    {
                        try
                        {
                            TcpClient outputClient = new TcpClient();

                            Task connectTask = outputClient.ConnectAsync(outputInfo.Key.Hostname, outputInfo.Key.Port);
                            if (!connectTask.Wait(currentTimeout))
                            {
                                logger.Warn($"Could not connect to output {outputInfo.Key.Hostname}:{outputInfo.Key.Port} after {currentTimeout}. Output will be skipped");
                                continue;
                            }

                            TargetConnection outputConnection = new TargetConnection(outputInfo.Key, outputClient, outputClient.GetStream());
                            clientConnection.OutputTargets[outputInfo.Key] = outputConnection;
                        }
                        catch (Exception e)
                        {
                            logger.Warn($"Could not connect to output {outputInfo.Key.Hostname}:{outputInfo.Key.Port}. Output will be skipped. {e}");
                        }
                    }

                    // Client > Target
                    new Thread(() =>
                    {
                        try
                        {
                            byte[] buffer = new byte[bufferSize];

                            while (true)
                            {
                                int read = clientConnection.Stream.Read(buffer, 0, buffer.Length);
                                if (read == 0)
                                    break;

                                foreach (var targetInfo in outputTargets.ToArray())
                                {
                                    if (targetInfo.Value == null)
                                        continue;

                                    ulong totalSize = (ulong)targetInfo.Value.Buffers.Count * bufferSize + bufferSize;
                                    if (totalSize > currentBufferSize)
                                    {
                                        logger.Warn($"Output target {targetInfo.Key.Hostname}:{targetInfo.Key.Port} has reach its maximum buffer size. Output will be skipped");

                                        outputTargets[targetInfo.Key] = null;
                                        Try(targetInfo.Value.Connection.Close);

                                        continue;
                                    }

                                    targetInfo.Value.Buffers.Enqueue(new ArraySegment<byte>(buffer, 0, read));
                                }

                                mainTarget.Stream.Write(buffer, 0, read);
                                mainTarget.Stream.Flush();

                                buffer = new byte[bufferSize];
                            }
                        }
                        catch (Exception e)
                        {
                            Try(mainTarget.Connection.Close);

                            foreach (TargetConnection targetConnection in outputTargets.Values)
                                if (targetConnection != null)
                                    Try(targetConnection.Connection.Close);

                            lock (activeConnections)
                            {
                                if (activeConnections.Remove(clientConnection))
                                {
                                    if (!(e is IOException) || !(e.InnerException is SocketException))
                                        logger.Warn("Exception while reading from client. " + e);

                                    logger.Info($"Lost connection from {clientEndPoint}, {activeConnections.Count} active connections");
                                }
                            }
                        }
                    }).Start();

                    // Target > Client
                    new Thread(() =>
                    {
                        try
                        {
                            byte[] buffer = new byte[bufferSize];

                            while (true)
                            {
                                int read = mainTarget.Stream.Read(buffer, 0, buffer.Length);
                                if (read == 0)
                                    break;

                                clientConnection.Stream.Write(buffer, 0, read);
                                clientConnection.Stream.Flush();
                            }
                        }
                        catch (Exception e)
                        {
                            Try(client.Close);

                            foreach (TargetConnection targetConnection in outputTargets.Values)
                                if (targetConnection != null)
                                    Try(targetConnection.Connection.Close);

                            lock (activeConnections)
                            {
                                if (activeConnections.Remove(clientConnection))
                                {
                                    if (!(e is IOException) || !(e.InnerException is SocketException))
                                        logger.Warn("Exception while reading from target. " + e);

                                    logger.Info($"Lost connection from {clientEndPoint}, {activeConnections.Count} active connections");
                                }
                            }
                        }
                    }).Start();

                    // Flush output target connections
                    byte[] readBuffer = new byte[bufferSize];

                    while (true)
                    {
                        int outputTargetCount = 0;
                        int dequeuedBuffers = 0;

                        foreach (var outputInfo in clientConnection.OutputTargets.ToArray())
                        {
                            if (outputInfo.Value == null)
                                continue;

                            outputTargetCount++;

                            try
                            {
                                ArraySegment<byte> buffer;
                                while (outputInfo.Value.Buffers.TryDequeue(out buffer))
                                {
                                    dequeuedBuffers++;

                                    outputInfo.Value.Stream.Write(buffer.Array, buffer.Offset, buffer.Count);
                                    outputInfo.Value.Stream.Flush();
                                }

                                while (outputInfo.Value.Stream.DataAvailable)
                                    outputInfo.Value.Stream.Read(readBuffer, 0, readBuffer.Length);
                            }
                            catch
                            {
                                logger.Warn($"Could not send buffer to output target {outputInfo.Key.Hostname}:{outputInfo.Key.Port}. Output will be skipped");

                                outputTargets[outputInfo.Key] = null;
                                Try(outputInfo.Value.Connection.Close);

                                continue;
                            }
                        }

                        if (outputTargetCount == 0)
                            break;

                        if (dequeuedBuffers == 0)
                            Thread.Sleep(10);
                    }
                }).Start();
            }
            catch (Exception e)
            {
                logger.Warn($"Error while processing TCP client. " + e);
            }

            tcpListener.BeginAcceptTcpClient(OnTcpConnection, null);
        }

        private static TargetInfo ReadTarget(XElement targetElement)
        {
            if (targetElement == null)
                throw new ArgumentNullException(nameof(targetElement));

            XAttribute hostnameAttribute = targetElement.Attribute("Hostname");
            if (hostnameAttribute == null)
                throw new Exception("You must specify a Hostname to this target");

            string hostname = hostnameAttribute.Value;
            ushort port = currentPort;

            XAttribute portAttribute = targetElement.Attribute("Port");
            if (portAttribute != null)
            {
                if (!ushort.TryParse(portAttribute.Value, out port))
                    throw new Exception("The specified port for this target is invalid");
            }

            return new TargetInfo(hostname, port);
        }
        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch { }
        }
    }
}
