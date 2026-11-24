using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CoffeeShop.Client
{
    class Program
    {
        static Socket sock;
        static string endpoint = "";
        static volatile bool running = true;
        static readonly object ConsoleLock = new();

        static void Log(string msg)
        {
            lock (ConsoleLock) { Console.WriteLine(msg); }
        }

        static void Main(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: Client <host> <port> [client|provider]"); return; }
            string host = args[0];
            int port = int.Parse(args[1]);
            string role = args.Length >= 3 ? args[2] : "client";
            if (role != "client" && role != "provider") { Console.WriteLine("role must be client or provider"); return; }

            sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.Connect(host, port);
            endpoint = sock.RemoteEndPoint?.ToString() ?? "";
            Console.WriteLine($"Connected to {endpoint} as {role}");

            // identify
            SendJson(new { cmd = "hello", role = role });

            // receiver thread
            var rt = new Thread(ReceiveLoop) { IsBackground = true };
            rt.Start();

            // main input loop
            Console.WriteLine("Commands: menu | order <product>:<qty> ... | deliver <product>:<qty> ... (provider) | quit");
            while (running)
            {
                string? line;
                try { line = Console.ReadLine(); }
                catch { line = null; }
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line == "quit" || line == "/quit")
                {
                    SendJson(new { cmd = "quit" });
                    break;
                }
                if (line == "menu")
                {
                    SendJson(new { cmd = "menu" });
                    continue;
                }
                if (line.StartsWith("order "))
                {
                    var items = ParseItems(line.Substring(6));
                    if (items == null) { Console.WriteLine("bad items"); continue; }
                    SendJson(new { cmd = "order", items = items });
                    continue;
                }
                if (line.StartsWith("deliver "))
                {
                    var items = ParseItems(line.Substring(8));
                    if (items == null) { Console.WriteLine("bad items"); continue; }
                    SendJson(new { cmd = "deliver", items = items });
                    continue;
                }
                // unknown -> send as? just error locally
                Console.WriteLine("Unknown command");
            }
            running = false;
            try { sock.Shutdown(SocketShutdown.Both); } catch { }
            try { sock.Close(); } catch { }
        }

        static object[]? ParseItems(string s)
        {
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<object>();
            foreach (var p in parts)
            {
                var kv = p.Split(':');
                if (kv.Length != 2) return null;
                if (!int.TryParse(kv[1], out int q) || q <= 0) return null;
                list.Add(new { product = kv[0], qty = q });
            }
            if (list.Count == 0) return null;
            return list.ToArray();
        }

        static void ReceiveLoop()
        {
            byte[] buf = new byte[4096];
            string leftover = "";
            while (running)
            {
                int n;
                try { n = sock.Receive(buf); }
                catch { n = 0; }
                if (n <= 0)
                {
                    Log("[server gone]");
                    running = false;
                    return;
                }
                leftover += Encoding.UTF8.GetString(buf, 0, n);
                int idx;
                while ((idx = leftover.IndexOf('\n')) >= 0)
                {
                    string line = leftover.Substring(0, idx).TrimEnd('\r');
                    leftover = leftover.Substring(idx + 1);
                    if (line.Length == 0) continue;
                    HandleServerMessage(line);
                }
                if (leftover.Length > 8192) { Log("message too long"); running = false; return; }
            }
        }

        static void HandleServerMessage(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "menu":
                        if (root.TryGetProperty("stock", out var st))
                        {
                            Console.Write("Stock: ");
                            foreach (var p in st.EnumerateObject()) Console.Write($"{p.Name}={p.Value} ");
                            Console.WriteLine();
                        }
                        break;
                    case "order_accepted":
                        Console.WriteLine($"Order {root.GetProperty("id").GetInt32()} accepted, prep {root.GetProperty("prep").GetInt32()} ms");
                        break;
                    case "order_refused":
                        Console.WriteLine($"Order refused: {root.GetProperty("product").GetString()} only {root.GetProperty("available").GetInt32()} left");
                        break;
                    case "order_ready":
                        Console.WriteLine($"Order {root.GetProperty("id").GetInt32()} READY");
                        break;
                    case "alert":
                        Console.WriteLine($"ALERT: {root.GetProperty("product").GetString()} stock={root.GetProperty("stock").GetInt32()}");
                        break;
                    case "delivery_ok":
                        Console.WriteLine("Delivery accepted");
                        break;
                    case "error":
                        Console.WriteLine($"ERROR: {root.GetProperty("msg").GetString()}");
                        break;
                    case "ok":
                        // hello/bye
                        break;
                    default:
                        Console.WriteLine($"MSG: {line}");
                        break;
                }
            }
            catch
            {
                Console.WriteLine($"MSG: {line}");
            }
        }

        static void SendJson(object obj)
        {
            try
            {
                string json = JsonSerializer.Serialize(obj) + "\n";
                byte[] data = Encoding.UTF8.GetBytes(json);
                int sent = 0;
                while (sent < data.Length)
                {
                    int n = sock.Send(data, sent, data.Length - sent, SocketFlags.None);
                    if (n <= 0) break;
                    sent += n;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"send error: {e.Message}");
            }
        }
    }
}
