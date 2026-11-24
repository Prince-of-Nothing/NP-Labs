using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

class Program
{
    static string userName = "";
    static int chatPort;
    static Socket genSock;
    static Socket privSock;
    static readonly object GenSockLock = new();
    static string privEndpoint = "";
    static readonly object PeersLock = new();
    static readonly Dictionary<string, Peer> Peers = new();
    static readonly HashSet<string> SeenGen = new();
    static readonly object SeenLock = new();
    static readonly object ConsoleLock = new();
    static readonly object LogLock = new();
    static StreamWriter logWriter;
    static volatile bool running = true;
    static int msgCounter = 0;
    static string passphrase = "";
    static byte[] key = null;

    class Peer { public string Name; public string Endpoint; public DateTime LastSeen; }

    static void Log(string msg) { lock (ConsoleLock) { Console.WriteLine(msg); } }

    static void LogDg(string dir, string endpoint, string kind, int size)
    {
        lock (LogLock)
        {
            try { logWriter.WriteLine($"{DateTime.Now:O} {dir} {endpoint} {kind} {size}"); logWriter.Flush(); } catch { }
        }
    }

    static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Usage: Chat <name> <port> [passphrase]"); return; }
        userName = args[0];
        chatPort = int.Parse(args[1]);
        if (args.Length >= 3) passphrase = args[2];
        if (!string.IsNullOrEmpty(passphrase))
        {
            using var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(passphrase, new byte[16], 100000, System.Security.Cryptography.HashAlgorithmName.SHA256);
            key = kdf.GetBytes(32);
        }

        string logPath = $"chat-{userName}-{chatPort}.log";
        logWriter = new StreamWriter(logPath, true) { AutoFlush = true };

        // general socket - EXACTLY like working bcast test
        genSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        genSock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        genSock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        genSock.Bind(new IPEndPoint(IPAddress.Any, chatPort));

        // private socket
        privSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        privSock.Bind(new IPEndPoint(IPAddress.Any, 0));
        var pe = (IPEndPoint)privSock.LocalEndPoint!;
        string localIp = GetLocalIp();
        privEndpoint = $"{localIp}:{pe.Port}";

        // receiver threads
        new Thread(RecvGeneral) { IsBackground = true }.Start();
        new Thread(RecvPrivate) { IsBackground = true }.Start();
        new Thread(HeartbeatLoop) { IsBackground = true }.Start();

        // wait for other peers to bind (like bcast test's 2s sleep)
        Thread.Sleep(2000);

        // announce join
        Broadcast(new { t = "join", name = userName, ep = privEndpoint });

        Thread.Sleep(1200);

        Log($"* {userName} joined (private ep {privEndpoint}). Type messages, /msg name text, /users, /quit");

        while (running)
        {
            string? line;
            try { line = Console.ReadLine(); } catch { line = null; }
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line == "/quit")
            {
                Broadcast(new { t = "bye", name = userName });
                running = false;
                break;
            }
            if (line == "/users")
            {
                lock (PeersLock)
                {
                    Console.WriteLine("Online:");
                    foreach (var p in Peers.Values) Console.WriteLine($"  {p.Name} {p.Endpoint}");
                }
                continue;
            }
            if (line.StartsWith("/msg "))
            {
                var rest = line.Substring(5);
                int sp = rest.IndexOf(' ');
                if (sp < 0) { Log("usage: /msg name text"); continue; }
                string target = rest.Substring(0, sp);
                string text = rest.Substring(sp + 1);
                if (text.Length > 500) { Log("message too long (max 500)"); continue; }
                Peer? targetPeer;
                lock (PeersLock) Peers.TryGetValue(target, out targetPeer);
                if (targetPeer == null) { Log($"ERROR: {target} not online"); continue; }
                SendPrivateMessage(targetPeer.Endpoint, target, text);
                Log($"[private] you -> {target}: {text}");
                continue;
            }
            if (line.Length > 500) { Log("message too long (max 500)"); continue; }
            int id = Interlocked.Increment(ref msgCounter);
            Broadcast(new { t = "gen", from = userName, text = line, id = id });
            Log($"[general] {userName}: {line}");
        }

        running = false;
        try { genSock.Close(); } catch { }
        try { privSock.Close(); } catch { }
        try { logWriter.Close(); } catch { }
    }

    static string GetLocalIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
                }
            }
        }
        catch { }
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 80);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    static IPAddress GetBroadcastAddress()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        var mask = ua.IPv4Mask;
                        if (mask != null)
                        {
                            byte[] ip = ua.Address.GetAddressBytes();
                            byte[] m = mask.GetAddressBytes();
                            byte[] bcast = new byte[4];
                            for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~m[i]);
                            return new IPAddress(bcast);
                        }
                    }
                }
            }
        }
        catch { }
        return IPAddress.Broadcast;
    }

    static void Broadcast(object obj)
    {
        try
        {
            string json = JsonSerializer.Serialize(obj);
            byte[] data = Encoding.UTF8.GetBytes(json);
            if (data.Length > 2000) return;
            // subnet broadcast (works cross-process on Windows) + limited broadcast (fallback)
            foreach (var addr in new[] { GetBroadcastAddress(), IPAddress.Broadcast })
            {
                try
                {
                    var ep = new IPEndPoint(addr, chatPort);
                    genSock.SendTo(data, ep);
                    LogDg("SEND", ep.ToString(), GetKind(obj), data.Length);
                }
                catch { }
            }
        }
        catch (Exception e) { Log($"send error: {e.Message}"); }
    }

    static string GetKind(object obj)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(obj));
            return doc.RootElement.TryGetProperty("t", out var t) ? t.GetString() ?? "?" : "?";
        }
        catch { return "?"; }
    }

    static void SendPrivate(string endpoint, object obj)
    {
        try
        {
            string json = JsonSerializer.Serialize(obj);
            byte[] data = Encoding.UTF8.GetBytes(json);
            var parts = endpoint.Split(':');
            var ep = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
            privSock.SendTo(data, ep);
            LogDg("SEND", ep.ToString(), GetKind(obj), data.Length);
        }
        catch (Exception e) { Log($"sendpriv error: {e.Message}"); }
    }

    static void SendPrivateMessage(string endpoint, string to, string text)
    {
        if (key == null)
        {
            SendPrivate(endpoint, new { t = "priv", from = userName, to = to, text = text });
            return;
        }
        try
        {
            byte[] nonce = new byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
            byte[] plaintext = Encoding.UTF8.GetBytes(text);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];
            using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }
            SendPrivate(endpoint, new
            {
                t = "privenc",
                from = userName,
                to = to,
                nonce = Convert.ToBase64String(nonce),
                tag = Convert.ToBase64String(tag),
                ct = Convert.ToBase64String(ciphertext)
            });
        }
        catch (Exception e) { Log($"encrypt error: {e.Message}"); }
    }

    static void RecvGeneral()
    {
        byte[] buf = new byte[4096];
        while (running)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n = genSock.ReceiveFrom(buf, ref remote);
                string text = Encoding.UTF8.GetString(buf, 0, n);
                LogDg("RECV", remote.ToString(), "general", n);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                string t = root.TryGetProperty("t", out var te) ? te.GetString() ?? "" : "";
                if (n > 2000) { LogDg("DROP", remote.ToString(), "too_long", n); continue; }
                switch (t)
                {
                    case "join":
                        {
                            string name = root.GetProperty("name").GetString() ?? "";
                            string ep = root.GetProperty("ep").GetString() ?? "";
                            if (name == userName && ep == privEndpoint) break; // our own loopback
                            if (name == userName)
                            {
                                // another peer tries to use our name
                                SendPrivate(ep, new { t = "nameinuse", name = name });
                                break;
                            }
                            lock (PeersLock)
                            {
                                if (!Peers.ContainsKey(name))
                                {
                                    Peers[name] = new Peer { Name = name, Endpoint = ep, LastSeen = DateTime.Now };
                                    Log($"* {name} joined the chat");
                                }
                            }
                            Broadcast(new { t = "hello", name = userName, ep = privEndpoint });
                            break;
                        }
                    case "hello":
                        {
                            string name = root.GetProperty("name").GetString() ?? "";
                            string ep = root.GetProperty("ep").GetString() ?? "";
                            if (name == userName) break;
                            lock (PeersLock)
                            {
                                if (!Peers.ContainsKey(name))
                                {
                                    Peers[name] = new Peer { Name = name, Endpoint = ep, LastSeen = DateTime.Now };
                                    Log($"* {name} joined the chat");
                                }
                                else
                                {
                                    Peers[name].Endpoint = ep;
                                    Peers[name].LastSeen = DateTime.Now;
                                }
                            }
                            break;
                        }
                    case "bye":
                        {
                            string name = root.GetProperty("name").GetString() ?? "";
                            if (name == userName) break;
                            lock (PeersLock)
                            {
                                if (Peers.Remove(name)) Log($"* {name} left the chat");
                            }
                            break;
                        }
                    case "heartbeat":
                        {
                            string name = root.GetProperty("name").GetString() ?? "";
                            if (name == userName) break;
                            lock (PeersLock)
                            {
                                if (Peers.TryGetValue(name, out var p))
                                {
                                    p.LastSeen = DateTime.Now;
                                }
                                else
                                {
                                    Peers[name] = new Peer { Name = name, Endpoint = root.TryGetProperty("ep", out var e) ? e.GetString() ?? "" : "", LastSeen = DateTime.Now };
                                    Log($"* {name} joined the chat");
                                }
                            }
                            break;
                        }
                    case "gen":
                        {
                            string from = root.GetProperty("from").GetString() ?? "";
                            string txt = root.GetProperty("text").GetString() ?? "";
                            if (txt.Length > 500) { LogDg("DROP", remote.ToString(), "gen_too_long", n); break; }
                            if (from == userName) break;
                            int mid = root.TryGetProperty("id", out var ide) && ide.TryGetInt32(out var iv) ? iv : 0;
                            if (mid != 0)
                            {
                                string key = from + ":" + mid;
                                lock (SeenLock)
                                {
                                    if (SeenGen.Contains(key)) break;
                                    SeenGen.Add(key);
                                }
                            }
                            Log($"[general] {from}: {txt}");
                            break;
                        }
                    default:
                        LogDg("DROP", remote.ToString(), "unknown_type", n);
                        break;
                }
            }
            catch (JsonException)
            {
                LogDg("DROP", "unknown", "invalid_json", 0);
            }
            catch (Exception e)
            {
                if (running) Log($"recvgen error: {e.Message}");
            }
        }
    }

    static void RecvPrivate()
    {
        byte[] buf = new byte[4096];
        while (running)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n = privSock.ReceiveFrom(buf, ref remote);
                string text = Encoding.UTF8.GetString(buf, 0, n);
                LogDg("RECV", remote.ToString(), "private", n);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                string t = root.TryGetProperty("t", out var te) ? te.GetString() ?? "" : "";
                switch (t)
                {
                    case "priv":
                        {
                            string from = root.GetProperty("from").GetString() ?? "";
                            string txt = root.GetProperty("text").GetString() ?? "";
                            if (txt.Length > 500) { LogDg("DROP", remote.ToString(), "priv_too_long", n); break; }
                            Log($"[private] {from}: {txt}");
                            break;
                        }
                    case "privenc":
                        {
                            string from = root.GetProperty("from").GetString() ?? "";
                            if (key == null) { Log($"[private] {from}: <encrypted, no passphrase>"); break; }
                            try
                            {
                                byte[] nonce = Convert.FromBase64String(root.GetProperty("nonce").GetString() ?? "");
                                byte[] tag = Convert.FromBase64String(root.GetProperty("tag").GetString() ?? "");
                                byte[] ct = Convert.FromBase64String(root.GetProperty("ct").GetString() ?? "");
                                byte[] pt = new byte[ct.Length];
                                using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
                                {
                                    aes.Decrypt(nonce, ct, tag, pt);
                                }
                                string msg = Encoding.UTF8.GetString(pt);
                                if (msg.Length > 500) { LogDg("DROP", remote.ToString(), "privenc_too_long", n); break; }
                                Log($"[private] {from}: {msg}");
                            }
                            catch (Exception e)
                            {
                                Log($"[private] {from}: <decryption failed: {e.Message}>");
                            }
                            break;
                        }
                    case "nameinuse":
                        {
                            Log($"ERROR: name '{root.GetProperty("name").GetString()}' already in use");
                            running = false;
                            Environment.Exit(1);
                            break;
                        }
                    default:
                        LogDg("DROP", remote.ToString(), "unknown_private", n);
                        break;
                }
            }
            catch (JsonException)
            {
                LogDg("DROP", "unknown", "invalid_json", 0);
            }
            catch (Exception e)
            {
                if (running) Log($"recvpriv error: {e.Message}");
            }
        }
    }

    static void HeartbeatLoop()
    {
        while (running)
        {
            Thread.Sleep(5000);
            if (!running) break;
            Broadcast(new { t = "heartbeat", name = userName, ep = privEndpoint });
            var now = DateTime.Now;
            var toRemove = new List<string>();
            lock (PeersLock)
            {
                foreach (var kv in Peers)
                {
                    if ((now - kv.Value.LastSeen).TotalSeconds > 15) toRemove.Add(kv.Key);
                }
                foreach (var name in toRemove)
                {
                    if (Peers.Remove(name)) Log($"* {name} left the chat");
                }
            }
        }
    }
}