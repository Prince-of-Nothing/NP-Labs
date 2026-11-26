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
        // Bonus: the stock survives a server restart. Atomic temp file + rename.
        const string StockStatePath = "stock-state.json";
        static readonly object StockFileLock = new();

        static readonly object SessionsLock = new();
        static readonly List<ClientSession> Sessions = new();
        static readonly List<ClientSession> Providers = new();

        static readonly BlockingCollection<Order> OrderQueue = new();
        static int nextOrderId = 0;
        static readonly object IdLock = new();

        static int MaxMsg = 4096;
        // Per-line cap on quantity: keeps summed quantities far from int overflow
        // (an overflowed negative sum would pass the stock check and *add* stock).
        const int MaxQty = 1_000_000;

        static readonly object ConsoleLock = new();
        static void Log(string msg)
        {
            lock (ConsoleLock) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1) { Console.WriteLine("Usage: Server <port>"); return; }
            int port = int.Parse(args[0]);

            if (TryLoadStock(out var loaded))
            {
                lock (StockLock) foreach (var p in Products) Stock[p] = loaded[p];
                Log($"Stock loaded from {StockStatePath}: {StockStr()}");
            }
            else
            {
                var rng = new Random();
                lock (StockLock)
                {
                    foreach (var p in Products) Stock[p] = rng.Next(10, 51);
                }
                PersistStock();
                Log($"Stock initialized: {StockStr()}");
            }

            int workers = Environment.ProcessorCount;
            if (workers < 1) workers = 1;
            Metrics.Init(workers);
            for (int i = 0; i < workers; i++)
            {
                int wid = i;
                var t = new Thread(() => WorkerLoop(wid)) { IsBackground = true, Name = $"worker-{i}" };
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
                    Metrics.Connection();
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

        // Bonus: save/load the stock so a restart does not bring back a random one.
        static void PersistStock()
        {
            try
            {
                lock (StockFileLock)
                {
                    Dictionary<string, int> snapshot;
                    lock (StockLock) snapshot = new Dictionary<string, int>(Stock);
                    string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    string tmp = StockStatePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, StockStatePath, overwrite: true);
                }
            }
            catch (Exception e) { Log($"Stock persist error: {e.Message}"); }
        }

        static bool TryLoadStock(out Dictionary<string, int> loaded)
        {
            loaded = null;
            try
            {
                if (!File.Exists(StockStatePath)) return false;
                var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(StockStatePath));
                if (data == null) return false;
                var clean = new Dictionary<string, int>();
                foreach (var p in Products)
                {
                    if (!data.TryGetValue(p, out int v) || v < 0 || v > 10_000_000) return false;
                    clean[p] = v;
                }
                loaded = clean;
                return true;
            }
            catch (Exception e)
            {
                Log($"Stock load failed ({e.Message}); starting with a random stock");
                return false;
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
                        try { HandleLine(s, line, ref identified); }
                        catch (Exception e) when (e is InvalidOperationException or FormatException)
                        {
                            // valid JSON of the wrong shape (e.g. a number, or "cmd": 5)
                            Metrics.Error();
                            SendLine(s, Err("malformed request"));
                            Log($"Malformed request from {s.Endpoint}: {line}");
                        }
                        if (s.Closed)
                        {
                            // a failed send marks the session closed; still cancel its orders
                            HandleDisconnect(s);
                            return;
                        }
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
                Metrics.Error();
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
                        Metrics.Provider();
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
                    case "stats":
                        SendJson(s, new { type = "stats", summary = Metrics.Summary() });
                        break;
                    case "quit":
                        SendLine(s, Ok("bye"));
                        s.Closed = true;
                        HandleDisconnect(s);
                        break;
                    default:
                        Metrics.Error();
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
                if (!Products.Contains(prod) || qty <= 0 || qty > MaxQty)
                {
                    Metrics.Error();
                    SendLine(s, Err($"invalid product or quantity: {prod}"));
                    Log($"Invalid order from {s.Endpoint}");
                    return;
                }
                items[prod] = items.TryGetValue(prod, out var v) ? v + qty : qty;
            }
            if (items.Count == 0) { SendLine(s, Err("empty order")); return; }
            int tag = root.TryGetProperty("tag", out var te) && te.TryGetInt32(out var tv) ? tv : 0;

            // check stock and decrement
            lock (StockLock)
            {
                foreach (var kv in items)
                {
                    if (Stock[kv.Key] < kv.Value)
                    {
                        var avail = Stock[kv.Key];
                        Metrics.Refused();
                        SendLine(s, new { type = "order_refused", product = kv.Key, available = avail, tag = tag });
                        Log($"Order refused for {s.Endpoint}: {kv.Key} need {kv.Value} have {avail}");
                        return;
                    }
                }
                foreach (var kv in items) Stock[kv.Key] -= kv.Value;
            }
            PersistStock();

            int id;
            lock (IdLock) { id = ++nextOrderId; }
            int prep = 0;
            foreach (var kv in items) prep += PrepTime[kv.Key] * kv.Value; // per item time * qty? lab: sum times of all items: 2 croissants = 2*2. yes qty multiplier
            // Actually prep time per product fixed per unit? "2 croissants, 1 tea, 1 coffee take 2*2+3+4" yes qty multiplier.

            string itemsStr = string.Join(",", items.Select(kv => kv.Key + "=" + kv.Value));
            var order = new Order
            {
                Id = id, Client = s, Items = items, PrepTime = prep, Tag = tag,
                ItemsStr = itemsStr, AcceptedAt = DateTime.UtcNow, WorkerId = -1
            };
            lock (s.Lock) s.Orders.Add(order);

            OrderQueue.Add(order);

            Metrics.Accepted(order);
            SendLine(s, new { type = "order_accepted", id = id, prep = prep, tag = tag });
            Log($"Order {id} accepted from {s.Endpoint}, prep {prep}ms");

            // alert low stock
            CheckAlerts();
        }

        static void HandleDeliver(ClientSession s, JsonElement root)
        {
            if (!s.IsProvider)
            {
                Metrics.Error();
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
                if (!Products.Contains(prod) || qty <= 0 || qty > MaxQty) { SendLine(s, Err("invalid product/qty")); return; }
                items[prod] = items.TryGetValue(prod, out var v) ? v + qty : qty;
            }
            lock (StockLock)
            {
                foreach (var kv in items) Stock[kv.Key] += kv.Value;
            }
            PersistStock();
            SendLine(s, new { type = "delivery_ok", items = items });
            Metrics.Delivery();
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
            if (provs.Count > 0) { Metrics.Alert(); Log($"Alert sent for {string.Join(",", low)}"); }
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

        static void WorkerLoop(int workerId)
        {
            foreach (var order in OrderQueue.GetConsumingEnumerable())
            {
                try
                {
                    Metrics.Started(order, workerId);
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
                        PersistStock();
                        lock (order.Client.Lock) order.Client.Orders.Remove(order);
                        Metrics.Finished(order, "cancelled");
                        Log($"Order {order.Id} cancelled (client gone); products returned");
                        CheckAlerts();
                        continue;
                    }

                    // ready
                    try
                    {
                        SendLine(order.Client, new { type = "order_ready", id = order.Id, tag = order.Tag });
                        Log($"Order {order.Id} ready for {order.Client.Endpoint}");
                    }
                    catch { }
                    Metrics.Finished(order, "ready");
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
            lock (s.WriteLock)
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
    }

    class ClientSession
    {
        public Socket Socket;
        public string Endpoint = "";
        public bool IsProvider;
        public volatile bool Closed;
        public List<Order> Orders = new();
        public object Lock = new();
        public object WriteLock = new();
    }

    class Order
    {
        public int Id;
        public ClientSession Client;
        public Dictionary<string, int> Items = new();
        public int PrepTime;
        public int Tag;
        public string ItemsStr = "";
        public DateTime AcceptedAt;
        public DateTime? StartedAt;
        public DateTime? FinishedAt;
        public int WorkerId = -1;
        public string Status = "";
        public CancellationTokenSource Cts = new();
    }
}
