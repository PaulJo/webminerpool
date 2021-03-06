// The MIT License (MIT)

// Copyright (c) 2018 - the webminerpool developer

// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Fleck;
using TinyJson;

using JsonData = System.Collections.Generic.Dictionary<string, object>;

namespace Server {

    public class Client {

        public PoolConnection PoolConnection;
        public IWebSocketConnection WebSocket;
        public string Pool = string.Empty;
        public string Login;
        public string Password;
        public bool GotHandshake;
        public bool GotPoolInfo;
        public DateTime Created;
        public DateTime LastPoolJobTime;
        public string LastTarget = string.Empty;
        public string UserId;
        public int NumChecked = 0;
        public double Fee = 0.03;
        public int Version = 1;
    }

    public class JobInfo {
        public string JobId;
        public string InnerId;
        public string Blob;
        public string Target;
        public CcHashset<string> Solved;
        public bool DevJob;
    }

    public class Job {
        public string Blob;
        public string Target;
        public string JobId;
        public DateTime Age = DateTime.MinValue;
    }

    public struct Credentials {
        public string Pool;
        public string Login;
        public string Password;
    }

    class MainClass {

        [DllImport ("libhash.so", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr hash_cn (string hex, int light);

        public const string SEP = "<-|->";
        public const string RegexIsHex = "^[a-fA-F0-9]+$";

        public const string RegexIsXMR = "[a-zA-Z|\\d]{95}";

        public const int JobCacheSize = (int) 1e5;

        private static bool libHashAvailable = false;

#if (AEON)
        private const string DevAddress = "WmtUFkPrboCKzL5iZhia4iNHKw9UmUXzGgbm5Uo3HPYwWcsY1JTyJ2n335gYiejNysLEs1G2JZxEm3uXUX93ArrV1yrXDyfPH";
        private const string DevPoolUrl = "pool.aeon.hashvault.pro";
        private const string DevPoolPwd = "x";
        private const int DevPoolPort = 3333;
#else

        // by default a 3% dev fee is submitted to the following address.
        // thank you for leaving this in.
        private const string DevAddress = "49kkH7rdoKyFsb1kYPKjCYiR2xy1XdnJNAY1e7XerwQFb57XQaRP7Npfk5xm1MezGn2yRBz6FWtGCFVKnzNTwSGJ3ZrLtHU";
        private const string DevPoolUrl = "de.moneroocean.stream";
        private const string DevPoolPwd = "x"; // if you want you can change this to something funny
        private const int DevPoolPort = 10064;
#endif

        private struct PoolInfo {
            public int Port;
            public string Url;
            public string EmptyPassword; // some pools require a non-empty password
            public PoolInfo (string url, int port, string emptypw = "") { Port = port; Url = url; EmptyPassword = emptypw; }
        }

        private static Dictionary<string, PoolInfo> PoolPool = new Dictionary<string, PoolInfo> ();

        private const int GraceConnectionTime = 16;             // time to connect to a pool in seconds 
        private const int HeartbeatRate = 10;                   // server logic every x seconds
        private const int TimeDevJobsAreOld = 600;              // after that job-age we do not forward dev jobs 
        private const int PoolTimeout = 60 * 12;                // in seconds, pool is not sending new jobs 
        private const int SpeedAverageOverXHeartbeats = 10;     // for the statistics shown every heartbeat
        private const int MaxHashChecksPerHeartbeat = 20;       // try not to kill ourselfs  
        private const int ForceGCEveryXHeartbeat = 40;          // so we can keep an eye on the memory 
        private const int SaveStatisticsEveryXHeartbeat = 40;   // save statistics 
        public const int BatchSize = 200;                       // mining with the same credentials (pool, login, password)
                                                                // results in connections beeing "bundled" to a single connection
                                                                // seen by the pool. that can result in large difficulties and
                                                                // hashrate fluctuations. this parameter sets the number of clients
                                                                // in one batch, e.g. for BatchSize = 100 and 1000 clients
                                                                // there will be 10 pool connections.

        private static int Heartbeats = 0;
        private static int HashesCheckedThisHeartbeat = 0;

        private static string jsonPools = "";
        private static long totalHashes = 0;
        private static long totalDevHashes = 0;
        private static long exceptionCounter = 0;

        private static bool saveLoginIdsNextHeartbeat = false;

        private static CcDictionary<Guid, Client> clients = new CcDictionary<Guid, Client> ();
        private static CcDictionary<string, JobInfo> jobInfos = new CcDictionary<string, JobInfo> ();
        private static CcDictionary<string, long> statistics = new CcDictionary<string, long> ();
        private static CcDictionary<string, Credentials> loginids = new CcDictionary<string, Credentials> ();
        private static CcDictionary<string, int> credentialSpamProtector = new CcDictionary<string, int> ();

        private static CcHashset<Client> slaves = new CcHashset<Client> ();

        private static CcQueue<string> jobQueue = new CcQueue<string> ();

        private static Job devJob = new Job ();

        static Client ourself;

        private static void FillPoolPool () {
            PoolPool.Clear ();

#if (AEON)
            PoolPool.Add ("aeon-pool.com", new PoolInfo ("mine.aeon-pool.com", 5555));
            PoolPool.Add ("minereasy.com", new PoolInfo ("aeon.minereasy.com", 3333));
            PoolPool.Add ("aeon.sumominer.com", new PoolInfo ("aeon.sumominer.com", 3333));
            PoolPool.Add ("aeon.rupool.tk", new PoolInfo ("aeon.rupool.tk", 4444));
            PoolPool.Add ("aeon.hashvault.pro", new PoolInfo ("pool.aeon.hashvault.pro", 3333, "x"));
            PoolPool.Add ("aeon.n-engine.com", new PoolInfo ("aeon.n-engine.com", 7333));
            PoolPool.Add ("aeonpool.xyz", new PoolInfo ("mine.aeonpool.xyz", 3333));
            PoolPool.Add ("aeonpool.dreamitsystems.com", new PoolInfo ("aeonpool.dreamitsystems.com", 13333, "x"));
            PoolPool.Add ("aeonminingpool.com", new PoolInfo ("pool.aeonminingpool.com", 3333, "x"));
            PoolPool.Add ("aeonhash.com", new PoolInfo ("pool.aeonhash.com", 3333));
            PoolPool.Add ("durinsmine.com", new PoolInfo ("mine.durinsmine.com", 3333, "x"));
            PoolPool.Add ("aeon.uax.io", new PoolInfo ("mine.uax.io", 4446));
            PoolPool.Add ("aeon-pool.sytes.net", new PoolInfo ("aeon-pool.sytes.net", 3333));
            PoolPool.Add ("aeonpool.net", new PoolInfo ("pool.aeonpool.net", 3333, "x"));
            PoolPool.Add ("supportaeon.com", new PoolInfo ("pool.supportaeon.com", 3333, "x"));
            PoolPool.Add ("pooltupi.com", new PoolInfo ("pooltupi.com", 8080, "x"));
            PoolPool.Add ("aeon.semipool.com", new PoolInfo ("pool.aeon.semipool.com", 3333, "x"));
#else
            PoolPool.Add ("xmrpool.eu", new PoolInfo ("xmrpool.eu", 3333));
            PoolPool.Add ("moneropool.com", new PoolInfo ("mine.moneropool.com", 3333));
            PoolPool.Add ("monero.crypto-pool.fr", new PoolInfo ("xmr.crypto-pool.fr", 3333));
            PoolPool.Add ("monerohash.com", new PoolInfo ("monerohash.com", 3333));
            PoolPool.Add ("minexmr.com", new PoolInfo ("pool.minexmr.com", 4444));
            PoolPool.Add ("usxmrpool.com", new PoolInfo ("pool.usxmrpool.com", 3333, "x"));
            PoolPool.Add ("supportxmr.com", new PoolInfo ("pool.supportxmr.com", 5555, "x"));
            PoolPool.Add ("moneroocean.stream:100", new PoolInfo ("gulf.moneroocean.stream", 80, "x"));
            PoolPool.Add ("moneroocean.stream", new PoolInfo ("gulf.moneroocean.stream", 10001, "x"));
            PoolPool.Add ("poolmining.org", new PoolInfo ("xmr.poolmining.org", 3032, "x"));
            PoolPool.Add ("minemonero.pro", new PoolInfo ("pool.minemonero.pro", 3333, "x"));
            PoolPool.Add ("xmr.prohash.net", new PoolInfo ("xmr.prohash.net", 1111));
            PoolPool.Add ("minercircle.com", new PoolInfo ("xmr.minercircle.com", 3333));
            PoolPool.Add ("xmr.nanopool.org", new PoolInfo ("xmr-eu1.nanopool.org", 14444, "x"));
            PoolPool.Add ("xmrminerpro.com", new PoolInfo ("xmrminerpro.com", 3333, "x"));
            PoolPool.Add ("clawde.xyz", new PoolInfo ("clawde.xyz", 3333, "x"));
            PoolPool.Add ("dwarfpool.com", new PoolInfo ("xmr-eu.dwarfpool.com", 8005));
            PoolPool.Add ("xmrpool.net", new PoolInfo ("mine.xmrpool.net", 3333, "x"));
            PoolPool.Add ("monero.hashvault.pro", new PoolInfo ("pool.monero.hashvault.pro", 5555, "x"));
            PoolPool.Add ("osiamining.com", new PoolInfo ("osiamining.com", 4545, ""));
            PoolPool.Add ("killallasics", new PoolInfo ("killallasics.moneroworld.com", 3333));
            PoolPool.Add ("arhash.xyz", new PoolInfo ("arhash.xyz", 3333, "x"));

            // Due to POW changes the following
            // pools mights not work anymore with the current hashfunction.

            // TURTLE  - bye bye turtle
            // PoolPool.Add ("slowandsteady.fun", new PoolInfo ("slowandsteady.fun", 3333));
            // PoolPool.Add ("trtl.flashpool.club", new PoolInfo ("trtl.flashpool.club", 3333));

            // SUMOKOIN - bye bye sumokoin
            // PoolPool.Add ("sumokoin.com", new PoolInfo ("pool.sumokoin.com", 3333));
            // PoolPool.Add ("sumokoin.hashvault.pro", new PoolInfo ("pool.sumokoin.hashvault.pro", 3333, "x"));
            // PoolPool.Add ("sumopool.sonofatech.com", new PoolInfo ("sumopool.sonofatech.com", 3333));
            // PoolPool.Add ("sumo.bohemianpool.com", new PoolInfo ("sumo.bohemianpool.com", 4444, "x"));
            // PoolPool.Add ("pool.sumokoin.ch", new PoolInfo ("pool.sumokoin.ch", 4444));

            // ELECTRONEUM
            PoolPool.Add ("etn.poolmining.org", new PoolInfo ("etn.poolmining.org", 3102));
            PoolPool.Add ("etn.nanopool.org", new PoolInfo ("etn-eu1.nanopool.org", 13333, "x"));
            PoolPool.Add ("etn.hashvault.pro", new PoolInfo ("pool.electroneum.hashvault.pro", 80, "x"));
#endif

            int counter = 0;

            jsonPools = "{\"identifier\":\"" + "poolinfo";

            foreach (var pool in PoolPool) {
                counter++;
                jsonPools += "\",\"pool" + counter.ToString () + "\":\"" + pool.Key;
            }

            jsonPools += "\"}\n";

        }

        private static UInt32 HexToUInt32 (String hex) {
            int NumberChars = hex.Length;
            byte[] bytes = new byte[NumberChars / 2];
            for (int i = 0; i < NumberChars; i += 2)
                bytes[i / 2] = Convert.ToByte (hex.Substring (i, 2), 16);
            return BitConverter.ToUInt32 (bytes, 0);;
        }

        private static bool CheckHashTarget (string target, string result) {
            // first check if result meets target 
            string ourtarget = result.Substring (56, 8);

            if (HexToUInt32 (ourtarget) >= HexToUInt32 (target))
                return false;
            else
                return true;
        }

        private static object hashLocker = new object ();
        private static bool CheckHash (string blob, string nonce, string target, string result, bool fullcheck) {

            // first check if result meets target
            string ourtarget = result.Substring (56, 8);

            if (HexToUInt32 (ourtarget) >= HexToUInt32 (target))
                return false;

            if (libHashAvailable && fullcheck) {
                // recalculate the hash

                string parta = blob.Substring (0, 78);
                string partb = blob.Substring (86, blob.Length - 86);

                lock (hashLocker) {

#if (AEON)
                    IntPtr pStr = hash_cn (parta + nonce + partb, 1);
#else
                    IntPtr pStr = hash_cn (parta + nonce + partb, 0);
#endif
                    string ourresult = Marshal.PtrToStringAnsi (pStr);
                    if (ourresult != result) return false;
                }

            }

            return true;
        }

        private static void PoolDisconnectCallback (Client client, string reason) {
            DisconnectClient (client, reason);
        }

        private static void PoolErrorCallback (Client client, JsonData msg) {

            if (msg["error"] == null) {
                // looks good
                string forward = "{\"identifier\":\"" + "hashsolved" + "\"}\n";

                client.WebSocket.Send (forward);

                Console.WriteLine ("{0}: solved job", client.WebSocket.ConnectionInfo.Id);

            } else {

                if (client != ourself) {

                    string forward = "{\"identifier\":\"" + "error" +
                        "\",\"param\":\"" + "pool rejected hash" + "\"}\n";

                    client.WebSocket.Send (forward);

                    Console.WriteLine ("{0}: got a pool rejection", client.WebSocket.ConnectionInfo.Id);
                }

            }
        }

        private static void PoolReceiveCallback (Client client, JsonData msg, CcHashset<string> hashset) {
            string jobId = Guid.NewGuid ().ToString ("N");

            client.LastPoolJobTime = DateTime.Now;

            JobInfo ji = new JobInfo ();
            ji.JobId = jobId;
            ji.Blob = msg["blob"].GetString ();
            ji.Target = msg["target"].GetString ();
            ji.InnerId = msg["job_id"].GetString ();
            ji.Solved = hashset;
            ji.DevJob = (client == ourself);

            jobInfos.TryAdd (jobId, ji);
            jobQueue.Enqueue (jobId);

            if (client == ourself) {
                devJob.Blob = msg["blob"].GetString ();
                devJob.JobId = jobId;
                devJob.Age = DateTime.Now;
                devJob.Target = msg["target"].GetString ();

                List<Client> slavelist = new List<Client> (slaves.Values);

                foreach (Client slave in slavelist) {

                    string forward = string.Empty;
                    string newtarget = string.Empty;

                    if (string.IsNullOrEmpty (slave.LastTarget)) {
                        newtarget = devJob.Target;
                    } else {
                        uint diff1 = HexToUInt32 (slave.LastTarget);
                        uint diff2 = HexToUInt32 (devJob.Target);
                        if (diff1 > diff2)
                            newtarget = slave.LastTarget;
                        else
                            newtarget = devJob.Target;
                    }

                    forward = "{\"identifier\":\"" + "job" +
                        "\",\"job_id\":\"" + devJob.JobId +
                        "\",\"blob\":\"" + devJob.Blob +
                        "\",\"target\":\"" + newtarget + "\"}\n";

                    slave.WebSocket.Send (forward);
                    Console.WriteLine ("Sending job to slave {0}", slave.WebSocket.ConnectionInfo.Id);
                }

            } else {
                // forward this to the websocket!

                string forward = string.Empty;

                bool tookdev = false;

                if (Random2.NextDouble () < client.Fee) {

                    if ((DateTime.Now - devJob.Age).TotalSeconds < TimeDevJobsAreOld) {

                        // okay, do not send devjob.Target, but
                        // the last difficulty

                        string newtarget = string.Empty;

                        if (string.IsNullOrEmpty (client.LastTarget)) {
                            newtarget = devJob.Target;
                        } else {
                            uint diff1 = HexToUInt32 (client.LastTarget);
                            uint diff2 = HexToUInt32 (devJob.Target);
                            if (diff1 > diff2)
                                newtarget = client.LastTarget;
                            else
                                newtarget = devJob.Target;
                        }

                        forward = "{\"identifier\":\"" + "job" +
                            "\",\"job_id\":\"" + devJob.JobId +
                            "\",\"blob\":\"" + devJob.Blob +
                            "\",\"target\":\"" + newtarget + "\"}\n";
                        tookdev = true;
                    }
                }

                if (!tookdev) {
                    forward = "{\"identifier\":\"" + "job" +
                        "\",\"job_id\":\"" + jobId +
                        "\",\"blob\":\"" + msg["blob"].GetString () +
                        "\",\"target\":\"" + msg["target"].GetString () + "\"}\n";

                    client.LastTarget = msg["target"].GetString ();
                }

                if (tookdev) {
                    if (!slaves.Contains (client)) slaves.TryAdd (client);
                    Console.WriteLine ("Send dev job!");
                } else {
                    slaves.TryRemove (client);
                }

                client.WebSocket.Send (forward);
                Console.WriteLine ("{0}: got job from pool", client.WebSocket.ConnectionInfo.Id);

            }
        }

        public static void RemoveClient (Guid guid) {
            Client client;

            if (!clients.TryRemove (guid, out client)) return;

            slaves.TryRemove (client);

            try {
                var wsoc = client.WebSocket as WebSocketConnection;
                if (wsoc != null) wsoc.CloseSocket ();
            } catch { }

            try { client.WebSocket.Close (); } catch { }

            if (client.PoolConnection != null)
                PoolConnectionFactory.Close (client.PoolConnection, client);
        }

        public static void DisconnectClient (Client client, string reason) {
            if (client.WebSocket.IsAvailable) {

                string msg = "{\"identifier\":\"" + "error" +
                    "\",\"param\":\"" + reason + "\"}\n";

                System.Threading.Tasks.Task t = client.WebSocket.Send (msg);

                t.ContinueWith ((prevTask) => {
                    prevTask.Wait ();
                    RemoveClient (client.WebSocket.ConnectionInfo.Id);
                });

            } else {
                RemoveClient (client.WebSocket.ConnectionInfo.Id);
            }
        }

        public static void DisconnectClient (Guid guid, string reason) {
            Client client = clients[guid];
            DisconnectClient (client, reason);
        }

        private static void CreateOurself () {
            ourself = new Client ();

            ourself.Login = DevAddress;
            ourself.Pool = DevPoolUrl;
            ourself.Created = ourself.LastPoolJobTime = DateTime.Now;
            ourself.Password = DevPoolPwd;
            ourself.WebSocket = new EmptyWebsocket ();

            clients.TryAdd (Guid.Empty, ourself);

            ourself.PoolConnection = PoolConnectionFactory.CreatePoolConnection (ourself, DevPoolUrl, DevPoolPort, DevAddress, DevPoolPwd);
        }

        private static bool CheckLibHash (out Exception ex) {

            // just check if we can successfully calculate a cn-hash.
            string testStr = new string ('1', 151) + '3';
            string hashedResult = string.Empty;

            try {
                IntPtr pStr = hash_cn (testStr, 0);
                hashedResult = Marshal.PtrToStringAnsi (pStr);
            } catch (Exception e) {
                ex = e;
                return false;
            }

            if (!hashedResult.StartsWith ("843ae6fc006")) {
                ex = new Exception ("Hash function returned wrong hash");
                return false;
            }

            ex = null;
            return true;
        }

        public static void Main (string[] args) {

            CConsole.ColorInfo (() => {

#if (DEBUG)
                Console.WriteLine ("[{0}] webminerpool server started - DEBUG MODE", DateTime.Now);
#else
                Console.WriteLine ("[{0}] webminerpool server started", DateTime.Now);
#endif

                double devfee = (new Client ()).Fee;
                if (devfee > double.Epsilon)
                    Console.WriteLine ("Developer fee of {0}% enabled.", (devfee * 100.0d).ToString ("F1"));

                Console.WriteLine ();
            });

   

            Exception exception = null;

            libHashAvailable = CheckLibHash (out exception);

            if (!libHashAvailable) CConsole.ColorWarning (() =>
                Console.WriteLine ("libhash.so is not available. Checking user submitted hashes disabled.")
            );

            PoolConnectionFactory.RegisterCallbacks (PoolReceiveCallback, PoolErrorCallback, PoolDisconnectCallback);

            FillPoolPool ();

            if (File.Exists ("statistics.dat")) {

                try {
                    statistics.Clear ();

                    string[] lines = File.ReadAllLines ("statistics.dat");

                    foreach (string line in lines) {
                        string[] statisticsdata = line.Split (new string[] { SEP }, StringSplitOptions.None);

                        string statid = statisticsdata[1];
                        long statnum = 0;
                        long.TryParse (statisticsdata[0], out statnum);

                        statistics.TryAdd (statid, statnum);
                    }

                } catch (Exception ex) {
                    CConsole.ColorAlert (() =>
                        Console.WriteLine ("Error while reading statistics: {0}", ex));
                }
            }

            if (File.Exists ("logins.dat")) {

                try {

                    loginids.Clear ();

                    string[] lines = File.ReadAllLines ("logins.dat");

                    foreach (string line in lines) {
                        string[] logindata = line.Split (new string[] { SEP }, StringSplitOptions.None);

                        Credentials cred = new Credentials ();
                        cred.Pool = logindata[1];
                        cred.Login = logindata[2];
                        cred.Password = logindata[3];

                        loginids.TryAdd (logindata[0], cred);
                    }

                } catch (Exception ex) {
                    CConsole.ColorAlert (() =>
                        Console.WriteLine ("Error while reading logins: {0}", ex));
                }

            }

            X509Certificate2 cert = null;

            try { cert = new X509Certificate2 ("certificate.pfx", "miner"); } catch (Exception e) { exception = e; cert = null; }

            bool certAvailable = (cert != null);

            if (!certAvailable)
                CConsole.ColorWarning (() => Console.WriteLine ("SSL certificate could not be loaded. Secure connection disabled."));

            WebSocketServer server;

#if (AEON)
            string localAddr = (certAvailable ? "wss://" : "ws://") + "0.0.0.0:8282";
#else
            string localAddr = (certAvailable ? "wss://" : "ws://") + "0.0.0.0:8181";
#endif

            server = new WebSocketServer (localAddr);

            server.Certificate = cert;

            FleckLog.LogAction = (level, message, ex) => {
                switch (level) {
                    case LogLevel.Debug:
#if (DEBUG)
                        Console.WriteLine ("FLECK (Debug): " + message);
#endif
                        break;
                    case LogLevel.Error:
                        if (ex != null && !string.IsNullOrEmpty (ex.Message)) {

                            CConsole.ColorAlert (() => Console.WriteLine ("FLECK: " + message + " " + ex.Message));

                            exceptionCounter++;
                            if ((exceptionCounter % 200) == 0) {
                                Helper.WriteTextAsyncWrapper ("fleck_error.txt", ex.ToString ());
                            }

                        } else Console.WriteLine ("FLECK: " + message);
                        break;
                    case LogLevel.Warn:
                        if (ex != null && !string.IsNullOrEmpty (ex.Message)) {
                            Console.WriteLine ("FLECK: " + message + " " + ex.Message);

                            exceptionCounter++;
                            if ((exceptionCounter % 200) == 0) {
                                Helper.WriteTextAsyncWrapper ("fleck_warn.txt", ex.ToString ());
                            }
                        } else Console.WriteLine ("FLECK: " + message);
                        break;
                    default:
                        Console.WriteLine ("FLECK: " + message);
                        break;
                }
            };

            server.RestartAfterListenError = true;
            server.ListenerSocket.NoDelay = false;

            server.Start (socket => {
                socket.OnOpen = () => {
                    string ipadr = string.Empty;
                    try { ipadr = socket.ConnectionInfo.ClientIpAddress; } catch { }

                    Client client = new Client ();
                    client.WebSocket = socket;
                    client.Created = client.LastPoolJobTime = DateTime.Now;

                    Guid guid = socket.ConnectionInfo.Id;
                    clients.TryAdd (guid, client);

                    Console.WriteLine ("{0}: connected with ip {1}", guid, ipadr);
                };
                socket.OnClose = () => {
                    Guid guid = socket.ConnectionInfo.Id;
                    RemoveClient (socket.ConnectionInfo.Id);

                    Console.WriteLine (guid + ": closed");
                };
                socket.OnError = error => {
                    Guid guid = socket.ConnectionInfo.Id;
                    RemoveClient (socket.ConnectionInfo.Id);

                    Console.WriteLine (guid + ": unexpected close");
                };
                socket.OnMessage = message => {
                    string ipadr = string.Empty;
                    try { ipadr = socket.ConnectionInfo.ClientIpAddress; } catch { }

                    Guid guid = socket.ConnectionInfo.Id;

                    if (message.Length > 3000) {
                        RemoveClient (guid); // that can't be valid, do not even try to parse
                    }

                    JsonData msg = message.FromJson<JsonData> ();
                    if (msg == null || !msg.ContainsKey ("identifier")) return;

                    Client client = null;

                    // in very rare occasions, we get interference with onopen()
                    // due to async code. wait a second and retry.
                    for (int tries = 0; tries < 4; tries++) {
                        if (clients.TryGetValue (guid, out client)) break;
                        Task.Run (async delegate { await Task.Delay (TimeSpan.FromSeconds (1)); }).Wait ();
                    }

                    if (client == null) {
                        // famous comment: this should not happen
                        RemoveClient (guid);
                        return;
                    }

                    string identifier = (string) msg["identifier"];

                    if (identifier == "handshake") {

                        if (client.GotHandshake) {
                            // no merci for malformed data.
                            DisconnectClient (client, "Handshake already performed.");
                            return;
                        }

                        client.GotHandshake = true;

                        if (msg.ContainsKey ("version")) {
                            int.TryParse (msg["version"].GetString (), out client.Version);
                        }

                        if (msg.ContainsKey ("loginid")) {
                            string loginid = msg["loginid"].GetString ();

                            if (loginid.Length != 36 && loginid.Length != 32) {
                                Console.WriteLine ("Invalid LoginId!");
                                DisconnectClient (client, "Invalid loginid.");
                                return;
                            }

                            Credentials crdts;
                            if (!loginids.TryGetValue (loginid, out crdts)) {
                                Console.WriteLine ("Unregistered LoginId! {0}", loginid);
                                DisconnectClient (client, "Loginid not registered!");
                                return;
                            }

                            client.Login = crdts.Login;
                            client.Password = crdts.Password;
                            client.Pool = crdts.Pool;

                        } else if (msg.ContainsKey ("login") && msg.ContainsKey ("password") && msg.ContainsKey ("pool")) {
                            client.Login = msg["login"].GetString ();
                            client.Password = msg["password"].GetString ();
                            client.Pool = msg["pool"].GetString ();
                        } else {
                            // no merci for malformed data.
                            Console.WriteLine ("Malformed handshake");
                            DisconnectClient (client, "Login, password and pool have to be specified.");
                            return;
                        }

                        client.UserId = string.Empty;

                        if (msg.ContainsKey ("userid")) {
                            string uid = msg["userid"].GetString ();

                            if (uid.Length > 200) { RemoveClient (socket.ConnectionInfo.Id); return; }
                            client.UserId = uid;
                        }

                        Console.WriteLine ("{0}: handshake - {1}, {2}", guid, client.Pool,
                         (client.Login.Length > 8 ? client.Login.Substring(0,8) + "..." : client.Login ) );

                        if (!string.IsNullOrEmpty (ipadr)) Firewall.Update (ipadr, Firewall.UpdateEntry.Handshake);

                        PoolInfo pi;

                        if (!PoolPool.TryGetValue (client.Pool, out pi)) {
                            // we dont have that pool?
                            DisconnectClient (client, "pool not known");
                            return;
                        }

                        // if pools have some default password
                        if (client.Password == "") client.Password = pi.EmptyPassword;

                        client.PoolConnection = PoolConnectionFactory.CreatePoolConnection (
                            client, pi.Url, pi.Port, client.Login, client.Password);

                    } else if (identifier == "solved") {

                        if (!client.GotHandshake) {
                            // no merci
                            RemoveClient (socket.ConnectionInfo.Id);
                            return;
                        }

                        Console.WriteLine ("{0}: reports solved hash", guid);

                        new Task (() => {

                            if (!msg.ContainsKey ("job_id") ||
                                !msg.ContainsKey ("nonce") ||
                                !msg.ContainsKey ("result")) {
                                // no merci for malformed data.
                                RemoveClient (guid);
                                return;
                            }

                            string jobid = msg["job_id"].GetString ();

                            JobInfo ji;

                            if (!jobInfos.TryGetValue (jobid, out ji)) {
                                // this job id is not known to us
                                Console.WriteLine ("Job unknown!");
                                return;
                            }

                            string reportedNonce = msg["nonce"].GetString ();
                            string reportedResult = msg["result"].GetString ();

                            if (ji.Solved.Contains (reportedNonce.ToLower ())) {
                                Console.WriteLine ("Nonce collision!");
                                return;
                            }

                            if (reportedNonce.Length != 8 || (!Regex.IsMatch (reportedNonce, RegexIsHex))) {
                                DisconnectClient (client, "nonce malformed");
                                return;
                            }

                            if (reportedResult.Length != 64 || (!Regex.IsMatch (reportedResult, RegexIsHex))) {
                                DisconnectClient (client, "result malformed");
                                return;
                            }

                            double prob = ((double) HexToUInt32 (ji.Target)) / ((double) 0xffffffff);
                            long howmanyhashes = ((long) (1.0 / prob));

                            totalHashes += howmanyhashes;

                            if (ji.DevJob) {
                                // that was an "dev" job. could be that the target does not match

                                if (!CheckHashTarget (ji.Target, reportedResult)) {
                                    Console.WriteLine ("Hash does not reach our target difficulty.");
                                    return;
                                }

                                totalDevHashes += howmanyhashes;
                            }

                            // default chance to get hash-checked is 10%
                            double chanceForACheck = 0.1;

                            // check new clients more often, but prevent that to happen the first 30s the server is running
                            if (Heartbeats > 3 && client.NumChecked < 9) chanceForACheck = 1.0 - 0.1 * client.NumChecked;

                            bool performFullCheck = (Random2.NextDouble () < chanceForACheck && HashesCheckedThisHeartbeat < MaxHashChecksPerHeartbeat);

                            if (performFullCheck) {
                                client.NumChecked++;
                                HashesCheckedThisHeartbeat++;
                            }

                            bool validHash = CheckHash (ji.Blob, reportedNonce, ji.Target, reportedResult, performFullCheck);

                            if (!validHash) {

                                CConsole.ColorWarning (() =>
                                    Console.WriteLine ("{0} got disconnected for WRONG hash.", client.WebSocket.ConnectionInfo.Id.ToString ()));

                                if (!string.IsNullOrEmpty (ipadr)) Firewall.Update (ipadr, Firewall.UpdateEntry.WrongHash);
                                RemoveClient (client.WebSocket.ConnectionInfo.Id);
                            } else {

                                if (performFullCheck)
                                    Console.WriteLine ("{0}: got hash-checked", client.WebSocket.ConnectionInfo.Id.ToString ());

                                if (!string.IsNullOrEmpty (ipadr)) Firewall.Update (ipadr, Firewall.UpdateEntry.SolvedJob);

                                ji.Solved.TryAdd (reportedNonce.ToLower ());

                                if (client.UserId != string.Empty) {
                                    long currentstat = 0;

                                    bool exists = statistics.TryGetValue (client.UserId, out currentstat);

                                    if (exists) statistics[client.UserId] = currentstat + howmanyhashes;
                                    else statistics.TryAdd (client.UserId, howmanyhashes);
                                }

                                if (!ji.DevJob) client.PoolConnection.Hashes += howmanyhashes;

                                Client jiClient = client;
                                if (ji.DevJob) jiClient = ourself;

                                string msg1 = "{\"id\":\"" + jiClient.PoolConnection.PoolId +
                                    "\",\"job_id\":\"" + ji.InnerId +
                                    "\",\"nonce\":\"" + msg["nonce"].GetString () +
                                    "\",\"result\":\"" + msg["result"].GetString () +
                                    "\"}";

                                string msg0 = "{\"method\":\"" + "submit" +
                                    "\",\"params\":" + msg1 +
                                    ",\"id\":\"" + "1" + "\"}\n"; // TODO: check the "1"

                                jiClient.PoolConnection.Send (jiClient, msg0);
                            }

                        }).Start ();

                    } else if (identifier == "poolinfo") {
                        if (!client.GotPoolInfo) {
                            client.GotPoolInfo = true;
                            client.WebSocket.Send (jsonPools);
                        }

                    } else
                    if (identifier == "register") {

                        string registerip = string.Empty;

                        try { registerip = client.WebSocket.ConnectionInfo.ClientIpAddress; } catch { };

                        if (string.IsNullOrEmpty (registerip)) { DisconnectClient (guid, "Unknown error."); return; }

                        int registeredThisSession = 0;
                        if (credentialSpamProtector.TryGetValue (registerip, out registeredThisSession)) {
                            registeredThisSession++;
                            credentialSpamProtector[registerip] = registeredThisSession;
                        } else {
                            credentialSpamProtector.TryAdd (registerip, 0);
                        }

                        if (registeredThisSession > 10) {
                            DisconnectClient (guid, "Too many registrations. You need to wait.");
                            return;
                        }

                        if (!msg.ContainsKey ("login") ||
                            !msg.ContainsKey ("password") ||
                            !msg.ContainsKey ("pool")) {
                            // no merci for malformed data.
                            DisconnectClient (guid, "Login, password and pool have to be specified!");
                            return;
                        }

                        // everything seems to be okay
                        Credentials crdts = new Credentials ();
                        crdts.Login = msg["login"].GetString ();
                        crdts.Pool = msg["pool"].GetString ();
                        crdts.Password = msg["password"].GetString ();

                        PoolInfo pi;

                        if (!PoolPool.TryGetValue (crdts.Pool, out pi)) {
                            // we dont have that pool?
                            DisconnectClient (client, "Pool not known!");
                            return;
                        }

                        bool loginok = false;
                        try { loginok = Regex.IsMatch (crdts.Login, RegexIsXMR); } catch { }

                        if (!loginok) {
                            DisconnectClient (client, "Not a valid address.");
                            return;
                        }

                        if (crdts.Password.Length > 120) {
                            DisconnectClient (client, "Password too long.");
                            return;
                        }

                        string newloginguid = Guid.NewGuid ().ToString ("N");
                        loginids.TryAdd (newloginguid, crdts);

                        string smsg = "{\"identifier\":\"" + "registered" +
                            "\",\"loginid\":\"" + newloginguid +
                            "\"}";

                        client.WebSocket.Send (smsg);

                        Console.WriteLine ("Client registered!");

                        saveLoginIdsNextHeartbeat = true;

                    } else if (identifier == "userstats") {
                        if (!msg.ContainsKey ("userid")) return;

                        Console.WriteLine ("Userstat request");

                        string uid = msg["userid"].GetString ();

                        long hashn = 0;
                        statistics.TryGetValue (uid, out hashn);

                        string smsg = "{\"identifier\":\"" + "userstats" +
                            "\",\"userid\":\"" + uid +
                            "\",\"value\":" + hashn.ToString () + "}\n";

                        client.WebSocket.Send (smsg);
                    }

                };
            });

            bool running = true;

            double totalSpeed = 0, totalDevSpeed = 0;

            while (running) {

                Heartbeats++;

                Firewall.Heartbeat (Heartbeats);

                try {
                    if (Heartbeats % SaveStatisticsEveryXHeartbeat == 0) {
                        CConsole.ColorInfo (() => Console.WriteLine ("Saving statistics."));

                        StringBuilder sb = new StringBuilder ();

                        foreach (var stat in statistics) {
                            sb.AppendLine (stat.Value.ToString () + SEP + stat.Key);
                        }

                        File.WriteAllText ("statistics.dat", sb.ToString ().TrimEnd ('\r', '\n'));
                    }

                } catch (Exception ex) {
                    CConsole.ColorAlert (() => Console.WriteLine ("Error saving statistics.dat: {0}", ex));
                }

                try {
                    if (saveLoginIdsNextHeartbeat) {

                        saveLoginIdsNextHeartbeat = false;
                        CConsole.ColorInfo (() => Console.WriteLine ("Saving logins."));

                        StringBuilder sb = new StringBuilder ();

                        foreach (var lins in loginids) {
                            sb.AppendLine (lins.Key + SEP + lins.Value.Pool + SEP + lins.Value.Login + SEP + lins.Value.Password);
                        }

                        File.WriteAllText ("logins.dat", sb.ToString ().TrimEnd ('\r', '\n'));
                    }
                } catch (Exception ex) {
                    CConsole.ColorAlert (() => Console.WriteLine ("Error saving logins.dat: {0}", ex));
                }

                try {

                    Task.Run (async delegate { await Task.Delay (TimeSpan.FromSeconds (HeartbeatRate)); }).Wait ();

                    if (Heartbeats % SpeedAverageOverXHeartbeats == 0) {
                        totalSpeed = (double) totalHashes / (double) (HeartbeatRate * SpeedAverageOverXHeartbeats);
                        totalDevSpeed = (double) totalDevHashes / (double) (HeartbeatRate * SpeedAverageOverXHeartbeats);

                        totalHashes = 0;
                        totalDevHashes = 0;
                    }

                    CConsole.ColorInfo (() =>
                        Console.WriteLine ("[{0}] heartbeat; connections client/pool: {1}/{2}; jobqueue: {3}k; speed total: {4}kH/s",
                            DateTime.Now.ToString (),
                            clients.Count,
                            PoolConnectionFactory.Connections.Count,
                            ((double) jobQueue.Count / 1000.0d).ToString ("F1"),
                            ((double) totalSpeed / 1000.0d).ToString ("F2")));

                    while (jobQueue.Count > JobCacheSize) {
                        string deq;
                        if (jobQueue.TryDequeue (out deq)) {
                            jobInfos.TryRemove (deq);
                        }
                    }

                    DateTime now = DateTime.Now;

                    List<PoolConnection> pcc = new List<PoolConnection> (PoolConnectionFactory.Connections.Values);
                    foreach (PoolConnection pc in pcc) {
                        PoolConnectionFactory.CheckPoolConnection (pc);
                    }

                    List<Client> cc = new List<Client> (clients.Values);

                    foreach (Client c in cc) {

                        try {

                            if ((now - c.Created).TotalSeconds > GraceConnectionTime) {
                                if (c.PoolConnection == null || c.PoolConnection.TcpClient == null) DisconnectClient (c, "timeout.");
                                else if (!c.PoolConnection.TcpClient.Connected) DisconnectClient (c, "lost pool connection.");
                                else if ((now - c.LastPoolJobTime).TotalSeconds > PoolTimeout) {
                                    DisconnectClient (c, "pool is not sending new jobs.");
                                }

                            }
                        } catch { RemoveClient (c.WebSocket.ConnectionInfo.Id); }

                    }

                    if (clients.ContainsKey (Guid.Empty)) {
                        if (clients.Count == 1)
                            RemoveClient (Guid.Empty);
                    } else {
                        // we removed ourself because we got disconnected from the pool
                        // make us alive again!
                        if (clients.Count > 0) {
                            CConsole.ColorWarning (() =>
                                Console.WriteLine ("disconnected from dev pool. trying to reconnect."));
                            devJob = new Job ();
                            CreateOurself ();
                        }
                    }

                    HashesCheckedThisHeartbeat = 0;

                    if (Heartbeats % ForceGCEveryXHeartbeat == 0) {
                        CConsole.ColorInfo (() => {

                            Console.WriteLine ("Garbage collection. Currently using {0} MB.", Math.Round (((double) (GC.GetTotalMemory (false)) / 1024 / 1024)));

                            DateTime tbc = DateTime.Now;

                            // trust me, I am a professional
                            GC.Collect (GC.MaxGeneration, GCCollectionMode.Forced); // DON'T DO THIS!!!
                            Console.WriteLine ("Garbage collected in {0} ms. Currently using {1} MB ({2} clients).", (DateTime.Now - tbc).Milliseconds,
                                Math.Round (((double) (GC.GetTotalMemory (false)) / 1024 / 1024)), clients.Count);

                        });
                    }

                } catch (Exception ex) {
                    CConsole.ColorAlert (() =>
                        Console.WriteLine ("Exception caught in the main loop ! {0}", ex));
                }

            }

        }
    }
}
