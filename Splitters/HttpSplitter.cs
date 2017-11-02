using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using log4net;

namespace NetSplitter
{
    public class HttpSplitter : Splitter
    {
        private const int bufferSize = 32 * 1024;

        private static readonly ILog logger = new DefaultLogger(); // LogManager.GetLogger(typeof(HttpSplitter));

        public ushort Port { get; }

        public override event EventHandler<HostInfo> HostConnected;
        public override event EventHandler<HostInfo> HostDisconnected;

        private HttpListener httpListener;
        private bool running = false;
        private List<ClientConnection> activeConnections = new List<ClientConnection>();

        public HttpSplitter(ushort port, Func<HostInfo, HostInfo> targetBalancer, Func<HostInfo, IEnumerable<HostInfo>> targetCloner) : base(targetBalancer, targetCloner)
        {
            Port = port;
        }

        public override void Start()
        {
            httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://+:{Port}/");

            httpListener.Start();

            running = true;
            httpListener.BeginGetContext(OnHttpConnection, null);
        }
        public override void Stop()
        {
            running = false;

            try
            {
                httpListener.Stop();
                httpListener.Close();
            }
            catch
            {
                httpListener = null;
            }
        }

        private void OnHttpConnection(IAsyncResult asyncResult)
        {
            if (!running)
                return;

            try
            {
                // Accept client
                HttpListenerContext context = httpListener.EndGetContext(asyncResult);

                IPEndPoint clientEndPoint = context.Request.RemoteEndPoint;
                HostInfo clientInfo = new HostInfo(clientEndPoint.Address.ToString(), (ushort)clientEndPoint.Port);

                new Thread(() =>
                {
                    // Connect to main server
                    HostInfo mainTargetInfo = targetBalancer(clientInfo);
                    if (mainTargetInfo == null)
                        return;

                    HostConnected?.Invoke(this, clientInfo);

                    // Create a HttpWebRequest
                    Func<HostInfo, HttpWebRequest> requestBuilder = (HostInfo target) =>
                    {
                        Uri uri = new Uri(new Uri($"http://{target.Hostname}:{target.Port}"), context.Request.RawUrl);
                        HttpWebRequest request = WebRequest.CreateHttp(uri);

                        // Copy headers
                        string xff = "";

                        foreach (string key in context.Request.Headers.Keys)
                        {
                            string value = context.Request.Headers[key];

                            switch (key.ToLower())
                            {
                                case "connection":
                                {
                                    switch (value.ToLower())
                                    {
                                        case "keep-alive": request.KeepAlive = true; break;
                                        case "close": break;
                                        default: request.Connection = value; break;
                                    }

                                    break;
                                }

                                case "host": break;
                                case "accept": request.Accept = value; break;
                                case "user-agent": request.UserAgent = value; break;
                                case "x-forwarded-for": xff = value; break;
                                case "content-length": request.ContentLength = int.Parse(value); break;
                                case "content-type": request.ContentType = value; break;
                                case "referer": request.Referer = value; break;

                                default:
                                    request.Headers.Add(key, value);
                                    break;
                            }
                        }

                        request.Method = context.Request.HttpMethod;
                        request.Host = $"{target.Hostname}:{target.Port}";
                        request.Headers["X-Forwarded-For"] = string.IsNullOrEmpty(xff) ? clientInfo.Hostname : $"{xff}, {clientInfo.Hostname}";
                        
                        return request;
                    };

                    // Copy stream if needed
                    Stream requestStream = context.Request.InputStream;

                    HostInfo[] otherTargets = targetCloner(clientInfo).ToArray();
                    if (otherTargets.Length > 0)
                    {
                        MemoryStream bufferedStream = new MemoryStream();
                        requestStream.CopyTo(bufferedStream);
                        requestStream = bufferedStream;

                        requestStream.Seek(0, SeekOrigin.Begin);
                    }

                    // Execute request and copy stream
                    HttpWebRequest mainTargetRequest = requestBuilder(mainTargetInfo);

                    if (context.Request.HttpMethod != "GET")
                    {
                        using (Stream mainTargetStream = mainTargetRequest.GetRequestStream())
                            requestStream.CopyTo(mainTargetStream);
                    }

                    // Send request to secondary targets
                    new Thread(() =>
                    {
                        foreach (HostInfo target in otherTargets)
                        {
                            HttpWebRequest targetRequest = requestBuilder(target);

                            if (context.Request.HttpMethod != "GET")
                            {
                                requestStream.Seek(0, SeekOrigin.Begin);

                                using (Stream targetStream = targetRequest.GetRequestStream())
                                    requestStream.CopyTo(targetStream);
                            }

                            try
                            {
                                targetRequest.GetResponse();
                            }
                            catch { }
                        }
                    }).Start();

                    HttpWebResponse mainTargetResponse;

                    try
                    {
                        mainTargetResponse = mainTargetRequest.GetResponse() as HttpWebResponse;
                    }
                    catch (WebException e)
                    {
                        mainTargetResponse = e.Response as HttpWebResponse;
                    }

                    // Copy headers
                    foreach (string key in mainTargetResponse.Headers.Keys)
                    {
                        string value = mainTargetResponse.Headers[key];

                        switch (key.ToLower())
                        {
                            case "transfer-encoding": break;
                            case "content-length": context.Response.ContentLength64 = int.Parse(value); break;

                            default:
                                context.Response.Headers.Add(key, value);
                                break;
                        }
                    }

                    context.Response.StatusCode = (int)mainTargetResponse.StatusCode;

                    try
                    {
                        using (Stream mainTargetStream = mainTargetResponse.GetResponseStream())
                            mainTargetStream.CopyTo(context.Response.OutputStream);
                    }
                    catch (Exception e)
                    {
                        logger.Warn($"Error while writing HTTP response. The client might have disconnected. " + e);
                    }

                    context.Response.Close();

                    HostDisconnected?.Invoke(this, clientInfo);
                }).Start();

            }
            catch (Exception e)
            {
                logger.Warn($"Error while processing HTTP client. " + e);
            }

            httpListener.BeginGetContext(OnHttpConnection, null);
        }
    }
}
