using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;

class Program
{
    static string dataDir = "broker-data";
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true };
    static string tokensFile = "tokens.txt";
    static readonly object TopicsLock = new();
    static readonly Dictionary<string, Topic> Topics = new();
    static readonly BlockingCollection<DeliveryItem> DeliveryQueue = new();
    static HttpListener listener;

    class Message
    {
        public long Id;
        public string Topic;
        public string Sender;
        public string Body;
        public DateTime Time;
    }
    class Topic
    {
        public string Name;
        public long NextId = 1;
        public List<Message> Messages = new();
        public List<Subscriber> Subscribers = new();
        public object Lock = new();
    }
    class Subscriber
    {
        public HttpListenerResponse Response;
        public Stream Stream;
        public volatile bool Alive = true;
        public object WriteLock = new();
    }
    class DeliveryItem { public Message Msg; }

    static void Log(string msg) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); }

    static void Main(string[] args)
    {
        string prefix = args.Length >= 1 ? args[0] : "http://localhost:8080/";
        if (!prefix.EndsWith("/")) prefix += "/";
        Directory.CreateDirectory(dataDir);
        LoadTopics();

        // load tokens (optional)
        // background delivery
        new Thread(DeliveryLoop) { IsBackground = true }.Start();

        listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        Log($"Broker listening on {prefix}");

        while (true)
        {
            try
            {
                var ctx = listener.GetContext();
                new Thread(() => HandleContext(ctx)) { IsBackground = true }.Start();
            }
            catch (Exception e) { Log($"Accept error: {e.Message}"); }
        }
    }

    static void LoadTopics()
    {
        foreach (var dir in Directory.GetDirectories(dataDir))
        {
            string topicName = Path.GetFileName(dir);
            string file = Path.Combine(dir, "messages.json");
            var topic = new Topic { Name = topicName };
            if (File.Exists(file))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var msgs = JsonSerializer.Deserialize<List<Message>>(json, FileJson);
                    if (msgs != null)
                    {
                        topic.Messages = msgs;
                        long maxId = 0;
                        foreach (var m in msgs) if (m.Id > maxId) maxId = m.Id;
                        topic.NextId = maxId + 1;
                    }
                }
                catch (Exception e) { Log($"Load error {topicName}: {e.Message}"); }
            }
            lock (TopicsLock) Topics[topicName] = topic;
        }
    }

    // Topic names become directory names, so reject anything that could escape broker-data.
    static bool ValidTopic(string name) =>
        name.Length is >= 1 and <= 64 && name != "." && name != ".." &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.');

    static readonly object PersistLock = new();

    static Topic GetOrCreateTopic(string name)
    {
        lock (TopicsLock)
        {
            if (!Topics.TryGetValue(name, out var t))
            {
                t = new Topic { Name = name };
                Topics[name] = t;
                Directory.CreateDirectory(Path.Combine(dataDir, name));
            }
            return t;
        }
    }

    static void HandleContext(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        string client = req.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            Log($"{req.HttpMethod} {req.Url.AbsolutePath} from {client}");
            string path = req.Url.AbsolutePath.TrimEnd('/');
            // GET /topics
            if (req.HttpMethod == "GET" && path == "/topics")
            {
                var list = new List<object>();
                lock (TopicsLock)
                {
                    foreach (var kv in Topics)
                    {
                        lock (kv.Value.Lock) list.Add(new { topic = kv.Key, size = kv.Value.Messages.Count });
                    }
                }
                WriteJson(res, 200, list);
                return;
            }
            // POST /topics/{topic}/messages
            if (req.HttpMethod == "POST" && path.StartsWith("/topics/") && path.EndsWith("/messages"))
            {
                string topicName = path.Substring("/topics/".Length, path.Length - "/topics/".Length - "/messages".Length);
                HandlePublish(ctx, topicName, client);
                return;
            }
            // GET /topics/{topic}/stream
            if (req.HttpMethod == "GET" && path.StartsWith("/topics/") && path.EndsWith("/stream"))
            {
                string topicName = path.Substring("/topics/".Length, path.Length - "/topics/".Length - "/stream".Length);
                HandleSubscribe(ctx, topicName, client);
                return;
            }
            Log($"404 {req.HttpMethod} {req.Url.AbsolutePath} from {client}");
            res.StatusCode = 404;
            res.Close();
        }
        catch (Exception e)
        {
            Log($"Handler error: {e.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    static void HandlePublish(HttpListenerContext ctx, string topicName, string client)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        if (!ValidTopic(topicName))
        {
            Log($"400 invalid topic name from {client}");
            res.StatusCode = 400; WriteText(res, ""); return;
        }
        // Task3: token auth
        string? auth = req.Headers["Authorization"];
        TokenInfo? tokenInfo = null;
        bool authRequired = File.Exists(tokensFile);
        if (authRequired)
        {
            if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 401;
                res.Headers["WWW-Authenticate"] = "Bearer";
                WriteText(res, "");
                Log($"Publish unauthorized (no token) {client} topic={topicName}");
                return;
            }
            string tok = auth.Substring("Bearer ".Length).Trim();
            tokenInfo = LookupToken(tok);
            if (tokenInfo == null)
            {
                res.StatusCode = 401;
                res.Headers["WWW-Authenticate"] = "Bearer";
                WriteText(res, "");
                Log($"Publish unauthorized (unknown token) {client} topic={topicName}");
                return;
            }
            if (!tokenInfo.Topics.Contains(topicName))
            {
                res.StatusCode = 403;
                WriteText(res, "");
                Log($"Publish forbidden {client} topic={topicName}");
                return;
            }
        }

        // read body
        string bodyStr;
        using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            bodyStr = sr.ReadToEnd();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bodyStr); }
        catch
        {
            Log($"400 invalid json from {client} topic={topicName}");
            res.StatusCode = 400; WriteText(res, ""); return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            string sender, body;
            try
            {
                if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
                sender = root.TryGetProperty("sender", out var s) ? s.GetString() ?? "" : "";
                body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            }
            catch (InvalidOperationException)
            {
                Log($"400 malformed message from {client} topic={topicName}");
                res.StatusCode = 400; WriteText(res, ""); return;
            }
            // With auth on, the sender is the token's owner, not a client-supplied claim.
            if (tokenInfo != null) sender = tokenInfo.Publisher;
            else if (string.IsNullOrEmpty(sender)) sender = "anonymous";

            var topic = GetOrCreateTopic(topicName);
            Message msg;
            lock (topic.Lock)
            {
                msg = new Message
                {
                    Id = topic.NextId++,
                    Topic = topicName,
                    Sender = sender,
                    Body = body,
                    Time = DateTime.UtcNow
                };
                topic.Messages.Add(msg);
                // enqueue under the topic lock so delivery order matches id order
                DeliveryQueue.Add(new DeliveryItem { Msg = msg });
            }
            PersistTopic(topic);
            // respond 202
            var respObj = new { id = msg.Id, topic = msg.Topic, sender = msg.Sender, body = msg.Body, time = msg.Time };
            res.StatusCode = 202;
            WriteJsonRaw(res, respObj);
            Log($"Publish accepted id={msg.Id} topic={topicName} from {client}");
        }
    }

    static void HandleSubscribe(HttpListenerContext ctx, string topicName, string client)
    {
        var res = ctx.Response;
        if (!ValidTopic(topicName))
        {
            Log($"400 invalid topic name from {client}");
            res.StatusCode = 400; WriteText(res, ""); return;
        }
        var topic = GetOrCreateTopic(topicName);
        res.StatusCode = 200;
        res.ContentType = "application/x-ndjson";
        res.SendChunked = true;
        var stream = res.OutputStream;
        var sub = new Subscriber { Response = res, Stream = stream, Alive = true };
        lock (topic.Lock) topic.Subscribers.Add(sub);
        Log($"Subscriber added topic={topicName} {client}");

        // keep open; detect disconnect via periodic heartbeat write
        try
        {
            while (sub.Alive)
            {
                Thread.Sleep(5000);
                if (!sub.Alive) break;
                try
                {
                    // heartbeat (empty line) to detect client disconnect
                    lock (sub.WriteLock)
                    {
                        byte[] hb = Encoding.UTF8.GetBytes("\n");
                        sub.Stream.Write(hb, 0, hb.Length);
                        sub.Stream.Flush();
                    }
                }
                catch
                {
                    sub.Alive = false;
                }
            }
        }
        catch { }
        // cleanup
        lock (topic.Lock) topic.Subscribers.Remove(sub);
        sub.Alive = false;
        try { stream.Close(); } catch { }
        try { res.Close(); } catch { }
        Log($"Subscriber gone topic={topicName} {client}");
    }

    // Note: HandleSubscribe above busy-waits; better to block on a wait handle. We'll instead keep thread blocked reading from request input? 
    // We'll implement a simpler approach: after registering, block until a closed flag set by delivery on write failure.

    static void DeliveryLoop()
    {
        foreach (var item in DeliveryQueue.GetConsumingEnumerable())
        {
            var msg = item.Msg;
            Topic topic;
            lock (TopicsLock) Topics.TryGetValue(msg.Topic, out topic);
            if (topic == null) continue;
            List<Subscriber> subs;
            lock (topic.Lock) subs = new List<Subscriber>(topic.Subscribers);
            string line = JsonSerializer.Serialize(new { id = msg.Id, topic = msg.Topic, sender = msg.Sender, body = msg.Body, time = msg.Time }) + "\n";
            byte[] data = Encoding.UTF8.GetBytes(line);
            foreach (var sub in subs)
            {
                if (!sub.Alive) continue;
                try
                {
                    lock (sub.WriteLock)
                    {
                        sub.Stream.Write(data, 0, data.Length);
                        sub.Stream.Flush();
                    }
                    Log($"Delivered id={msg.Id} topic={msg.Topic} to subscriber");
                }
                catch
                {
                    sub.Alive = false;
                    Log($"Subscriber write failed; removed");
                }
            }
            // cleanup dead
            lock (topic.Lock) topic.Subscribers.RemoveAll(s => !s.Alive);
        }
    }

    static void PersistTopic(Topic topic)
    {
        try
        {
            // Serialize writers: snapshot and write under one lock so an older snapshot
            // can never overwrite a newer one.
            lock (PersistLock)
            {
                string dir = Path.Combine(dataDir, topic.Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "messages.json");
                string tmp = file + ".tmp";
                List<Message> snapshot;
                lock (topic.Lock) snapshot = new List<Message>(topic.Messages);
                string json = JsonSerializer.Serialize(snapshot, FileJson);
                File.WriteAllText(tmp, json);
                File.Move(tmp, file, overwrite: true);
            }
        }
        catch (Exception e) { Log($"Persist error: {e.Message}"); }
    }

    static TokenInfo? LookupToken(string token)
    {
        if (!File.Exists(tokensFile)) return null;
        foreach (var line in File.ReadAllLines(tokensFile))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (parts[0] == token)
            {
                return new TokenInfo { Publisher = parts[1], Topics = new List<string>(parts[2].Split(',')) };
            }
        }
        return null;
    }

    class TokenInfo { public string Publisher; public List<string> Topics; }

    static void WriteJson(HttpListenerResponse res, int status, object obj)
    {
        res.StatusCode = status;
        res.ContentType = "application/json";
        string json = JsonSerializer.Serialize(obj);
        byte[] data = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = data.Length;
        res.OutputStream.Write(data, 0, data.Length);
        res.OutputStream.Close();
    }
    static void WriteJsonRaw(HttpListenerResponse res, object obj)
    {
        res.ContentType = "application/json";
        string json = JsonSerializer.Serialize(obj);
        byte[] data = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = data.Length;
        res.OutputStream.Write(data, 0, data.Length);
        res.OutputStream.Close();
    }
    static void WriteText(HttpListenerResponse res, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        res.ContentLength64 = data.Length;
        res.OutputStream.Write(data, 0, data.Length);
        res.OutputStream.Close();
    }
}
