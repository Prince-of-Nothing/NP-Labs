using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Linq;

namespace CoffeeShop.Server
{
    class Program
    {
        static readonly string[] Products = { "coffee", "tea", "croissant" };
        static readonly Dictionary<string, int> PrepTime = new()
        {
            ["coffee"] = 4000,
            ["tea"] = 3000,
            ["croissant"] = 2000,
        };

        static readonly object StockLock = new();
        static readonly Dictionary<string, int> Stock = new()
        {
            ["coffee"] = 0, ["tea"] = 0, ["croissant"] = 0,
        };

        static readonly object SessionsLock = new();
        static readonly List<ClientSession> Sessions = new();
        static readonly List<ClientSession> Providers = new();

        static readonly BlockingCollection<Order> OrderQueue = new();
        static int nextOrderId = 0;
        static readonly object IdLock = new();

        static int MaxMsg = 4096;

        static readonly object ConsoleLock = new();
        static void Log(string msg)
        {
            lock (ConsoleLock) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1) { Console.WriteLine("Usage: Server <port>"); return; }
            int port = int.Parse(args[0]);

            var rng = new Random();
            lock (StockLock)
            {
                foreach (var p in Products) Stock[p] = rng.Next(10, 51);
            }
            Log($"Stock initialized: {StockStr()}");

            int workers = Environment.ProcessorCount;
            if (workers < 1) workers = 1;
            for (int i = 0; i < workers; i++)
            {
                var t = new Thread(WorkerLoop) { IsBackground = true, Name = $"worker-{i}" };
                t.Start();
            }
            Log($"Started {workers} workers");

            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Any, port));
            listener.Listen(128);
            Log($"Listening on 0.0.0.0:{port}");

            while (true)
            {
                try
                {
                    var client = listener.Accept();
                    var ep = client.RemoteEndPoint?.ToString() ?? "?";
                    Log($"Connection from {ep}");
                    var session = new ClientSession { Socket = client, Endpoint = ep };
                    lock (SessionsLock) Sessions.Add(session);
                    var t = new Thread(() => HandleClient(session)) { IsBackground = true };
                    t.Start();
                }
                catch (Exception e)
                {
                    Log($"Accept error: {e.Message}");
                }
            }
        }

        static string StockStr()
        {
            lock (StockLock)
            {
                return string.Join(", ", Products.Select(p => $"{p}={Stock[p]}"));
            }
        }

        static void HandleClient(ClientSession s)
        {
            try
            {
                // require hello first
                bool identified = false;
                string buffer = "";
                byte[] buf = new byte[4096];
                while (true)
                {
                    int n;
                    try { n = s.Socket.Receive(buf); }
                    catch { n = 0; }
                    if (n <= 0)
                    {
                        // disconnected
                        HandleDisconnect(s);
                        return;
                    }
                    buffer += Encoding.UTF8.GetString(buf, 0, n);
                    if (buffer.Length > MaxMsg * 4)
                    {
                        SendLine(s, Err("message too long"));
                        HandleDisconnect(s);
                        return;
                    }
                    int idx;
                    while ((idx = buffer.IndexOf('\n')) >= 0)
                    {
                        string line = buffer.Substring(0, idx);
                        buffer = buffer.Substring(idx + 1);
                        if (line.Length > MaxMsg)
                        {
                            SendLine(s, Err("message too long"));
                            continue;
                        }
                        line = line.TrimEnd('\r');
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        // handle
                        HandleLine(s, line, ref identified);
                        if (s.Closed) return;
                    }
                }
            }
            catch (Exception e)
            {
                Log($"Error handling {s.Endpoint}: {e.Message}");
                HandleDisconnect(s);
            }
        }

        static void HandleLine(ClientSession s, string line, ref bool identified)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch
            {
                SendLine(s, Err("invalid json"));
                Log($"Invalid input from {s.Endpoint}: {line}");
                return;
            }
            using (doc)
            {
                var root = doc.RootElement;
                string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
                if (!identified)
                {
                    if (cmd != "hello")
                    {
                        SendLine(s, Err("expected hello first"));
                        return;
                    }
                    string role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "client" : "client";
                    s.IsProvider = role == "provider";
                    identified = true;
                    if (s.IsProvider)
                    {
                        lock (SessionsLock) Providers.Add(s);
                        Log($"Provider identified: {s.Endpoint}");
                        // send alerts for low stock
                        SendAlerts(s);
                    }
                    SendLine(s, Ok("hello"));
                    return;
                }

                switch (cmd)
                {
                    case "menu":
                        HandleMenu(s);
                        break;
                    case "order":
                        HandleOrder(s, root);
                        break;
                    case "deliver":
                        HandleDeliver(s, root);
                        break;
                    case "quit":
                        SendLine(s, Ok("bye"));
                        s.Closed = true;
                        HandleDisconnect(s);
                        break;
                    default:
                        SendLine(s, Err("unknown command"));
                        Log($"Unknown command from {s.Endpoint}: {cmd}");
                        break;
                }
            }
        }

        static void HandleMenu(ClientSession s)
        {
            var stockCopy = new Dictionary<string, int>();
            lock (StockLock) foreach (var p in Products) stockCopy[p] = Stock[p];
            var msg = new { type = "menu", stock = stockCopy };
            SendJson(s, msg);
            Log($"MENU to {s.Endpoint}");
        }

        static void HandleOrder(ClientSession s, JsonElement root)
        {
            // parse items
            if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            {
                SendLine(s, Err("missing items")); return;
            }
            var items = new Dictionary<string, int>();
            foreach (var it in itemsEl.EnumerateArray())
            {
                string prod = it.TryGetProperty("product", out var pe) ? pe.GetString() ?? "" : "";
                int qty = it.TryGetProperty("qty", out var qe) && qe.TryGetInt32(out var qi) ? qi : 0;
                if (!Products.Contains(prod) || qty <= 0)
                {
                    SendLine(s, Err($"invalid product or quantity: {prod}"));
                    Log($"Invalid order from {s.Endpoint}");
                    return;
                }
                items[prod] = items.TryGetValue(prod, out var v) ? v + qty : qty;
            }
            if (items.Count == 0) { SendLine(s, Err("empty order")); return; }

            // check stock and decrement
            lock (StockLock)
            {
                foreach (var kv in items)
                {
                    if (Stock[kv.Key] < kv.Value)
                    {
                        var avail = Stock[kv.Key];
                        SendLine(s, new { type = "order_refused", product = kv.Key, available = avail });
                        Log($"Order refused for {s.Endpoint}: {kv.Key} need {kv.Value} have {avail}");
                        return;
                    }
                }
                foreach (var kv in items) Stock[kv.Key] -= kv.Value;
            }

            int id;
            lock (IdLock) { id = ++nextOrderId; }
            int prep = 0;
            foreach (var kv in items) prep += PrepTime[kv.Key] * kv.Value; // per item time * qty? lab: sum times of all items: 2 croissants = 2*2. yes qty multiplier
            // Actually prep time per product fixed per unit? "2 croissants, 1 tea, 1 coffee take 2*2+3+4" yes qty multiplier.

            var order = new Order { Id = id, Client = s, Items = items, PrepTime = prep };
            lock (s.Lock) s.Orders.Add(order);

            OrderQueue.Add(order);

            SendLine(s, new { type = "order_accepted", id = id, prep = prep });
            Log($"Order {id} accepted from {s.Endpoint}, prep {prep}ms");

            // alert low stock
            CheckAlerts();
        }

        static void HandleDeliver(ClientSession s, JsonElement root)
        {
            if (!s.IsProvider)
            {
                SendLine(s, Err("only provider can deliver"));
                Log($"Deliver refused (not provider) from {s.Endpoint}");
                return;
            }
            if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            { SendLine(s, Err("missing items")); return; }
            var items = new Dictionary<string, int>();
            foreach (var it in itemsEl.EnumerateArray())
            {
                string prod = it.TryGetProperty("product", out var pe) ? pe.GetString() ?? "" : "";
                int qty = it.TryGetProperty("qty", out var qe) && qe.TryGetInt32(out var qi) ? qi : 0;
                if (!Products.Contains(prod) || qty <= 0) { SendLine(s, Err("invalid product/qty")); return; }
                items[prod] = items.TryGetValue(prod, out var v) ? v + qty : qty;
            }
            lock (StockLock)
            {
                foreach (var kv in items) Stock[kv.Key] += kv.Value;
            }
            SendLine(s, new { type = "delivery_ok", items = items });
            Log($"Delivery from {s.Endpoint}: {string.Join(",", items.Select(kv => kv.Key + "=" + kv.Value))}");
            // no alerts on increase? still check
            CheckAlerts();
        }

        static void CheckAlerts()
        {
            var low = new List<string>();
            lock (StockLock)
            {
                foreach (var p in Products) if (Stock[p] < 5) low.Add(p);
            }
            if (low.Count == 0) return;
            List<ClientSession> provs;
            lock (SessionsLock) provs = new List<ClientSession>(Providers);
            foreach (var p in provs)
            {
                if (p.Closed) continue;
                foreach (var prod in low)
                {
                    int st;
                    lock (StockLock) st = Stock[prod];
                    SendLine(p, new { type = "alert", product = prod, stock = st });
                }
            }
            if (provs.Count > 0) Log($"Alert sent for {string.Join(",", low)}");
        }

        static void SendAlerts(ClientSession s)
        {
            // send for currently low
            var low = new List<string>();
            lock (StockLock) foreach (var p in Products) if (Stock[p] < 5) low.Add(p);
            foreach (var prod in low)
            {
                int st; lock (StockLock) st = Stock[prod];
                SendLine(s, new { type = "alert", product = prod, stock = st });
            }
        }

        static void WorkerLoop()
        {
            foreach (var order in OrderQueue.GetConsumingEnumerable())
            {
                try
                {
                    bool cancelled = false;
                    int remaining = order.PrepTime;
                    int chunk = 100;
                    while (remaining > 0)
                    {
                        if (order.Cts.Token.IsCancellationRequested) { cancelled = true; break; }
                        int sleep = Math.Min(chunk, remaining);
                        Thread.Sleep(sleep);
                        remaining -= sleep;
                    }
                    if (order.Cts.Token.IsCancellationRequested) cancelled = true;

                    if (cancelled)
                    {
                        // return products to stock
                        lock (StockLock)
                        {
                            foreach (var kv in order.Items) Stock[kv.Key] += kv.Value;
                        }
                        lock (order.Client.Lock) order.Client.Orders.Remove(order);
                        Log($"Order {order.Id} cancelled (client gone); products returned");
                        CheckAlerts();
                        continue;
                    }

                    // ready
                    try
                    {
                        SendLine(order.Client, new { type = "order_ready", id = order.Id });
                        Log($"Order {order.Id} ready for {order.Client.Endpoint}");
                    }
                    catch { }
                    lock (order.Client.Lock) order.Client.Orders.Remove(order);
                }
                catch (Exception e)
                {
                    Log($"Worker error: {e.Message}");
                }
            }
        }

        static void HandleDisconnect(ClientSession s)
        {
            if (s.Closed && s.Socket == null) return;
            bool already = s.Closed;
            s.Closed = true;
            try { s.Socket.Shutdown(SocketShutdown.Both); } catch { }
            try { s.Socket.Close(); } catch { }
            // cancel orders
            List<Order> toCancel;
            lock (s.Lock) { toCancel = new List<Order>(s.Orders); s.Orders.Clear(); }
            foreach (var o in toCancel) { try { o.Cts.Cancel(); } catch { } }
            // remove from sessions/providers
            lock (SessionsLock)
            {
                Sessions.Remove(s);
                Providers.Remove(s);
            }
            if (!already) Log($"Disconnected: {s.Endpoint}");
        }

        static object Ok(string msg) => new { type = "ok", msg = msg };
        static object Err(string msg) => new { type = "error", msg = msg };

        static void SendJson(ClientSession s, object obj)
        {
            string json = JsonSerializer.Serialize(obj);
            SendRaw(s, json + "\n");
        }
        static void SendLine(ClientSession s, object obj) => SendJson(s, obj);

        static void SendRaw(ClientSession s, string text)
        {
            if (s.Closed) return;
            byte[] data = Encoding.UTF8.GetBytes(text);
            int sent = 0;
            while (sent < data.Length)
            {
                int n;
                try { n = s.Socket.Send(data, sent, data.Length - sent, SocketFlags.None); }
                catch { s.Closed = true; return; }
                if (n <= 0) { s.Closed = true; return; }
                sent += n;
            }
        }
    }

    class ClientSession
    {
        public Socket Socket;
        public string Endpoint = "";
        public bool IsProvider;
        public bool Closed;
        public List<Order> Orders = new();
        public object Lock = new();
    }

    class Order
    {
        public int Id;
        public ClientSession Client;
        public Dictionary<string, int> Items = new();
        public int PrepTime;
        public CancellationTokenSource Cts = new();
    }
}
