using Grpc.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Broker;

public class BrokerService : Broker.Broker.BrokerBase
{
    static string dataDir = "broker-data";
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true };
    static readonly object TopicsLock = new();
    static readonly Dictionary<string, Topic> Topics = new();
    static readonly Channel<DeliveryItem> DeliveryQueue = Channel.CreateUnbounded<DeliveryItem>();
    static readonly string tokensFile = "tokens.txt";

    static BrokerService()
    {
        Directory.CreateDirectory(dataDir);
        LoadTopics();
        _ = Task.Run(DeliveryLoop);
    }

    public BrokerService() { }

    public class MessageRec { public long Id; public string Topic; public string Sender; public string Body; public DateTime Time; }
    class Topic
    {
        public string Name;
        public long NextId = 1;
        public List<MessageRec> Messages = new();
        public List<Sub> Subscribers = new();
        public object Lock = new();
    }
    class Sub { public IServerStreamWriter<Broker.Message> Writer = null!; public string Client = "?"; }
    class DeliveryItem { public MessageRec Msg = null!; }

    static void LoadTopics()
    {
        foreach (var dir in Directory.GetDirectories(dataDir))
        {
            string name = Path.GetFileName(dir);
            string file = Path.Combine(dir, "messages.json");
            var topic = new Topic { Name = name };
            if (File.Exists(file))
            {
                try
                {
                    var msgs = JsonSerializer.Deserialize<List<MessageRec>>(File.ReadAllText(file), FileJson);
                    if (msgs != null) { topic.Messages = msgs; topic.NextId = msgs.Select(m => m.Id).DefaultIfEmpty(0).Max() + 1; }
                }
                catch { }
            }
            lock (TopicsLock) Topics[name] = topic;
        }
    }

    // Topic names become directory names, so reject anything that could escape broker-data.
    static bool ValidTopic(string name) =>
        name.Length is >= 1 and <= 64 && name != "." && name != ".." &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.');

    static readonly object PersistLock = new();

    static Topic GetOrCreate(string name)
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

    public override Task<PublishReply> Publish(PublishRequest request, ServerCallContext context)
    {
        string topicName = request.Topic;
        if (!ValidTopic(topicName)) throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid topic name"));
        // auth
        string? token = string.IsNullOrEmpty(request.Token) ? context.RequestHeaders.GetValue("authorization") : request.Token;
        if (token != null && token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token.Substring(7).Trim();
        bool authRequired = File.Exists(tokensFile);
        TokenInfo? ti = null;
        if (authRequired)
        {
            if (string.IsNullOrEmpty(token)) throw new RpcException(new Status(StatusCode.Unauthenticated, "missing token"));
            ti = LookupToken(token);
            if (ti == null) throw new RpcException(new Status(StatusCode.Unauthenticated, "unknown token"));
            if (!ti.Topics.Contains(topicName)) throw new RpcException(new Status(StatusCode.PermissionDenied, "topic not allowed"));
        }
        var topic = GetOrCreate(topicName);
        MessageRec msg;
        lock (topic.Lock)
        {
            // With auth on, the sender is the token's owner, not a client-supplied claim.
            string sender = ti != null ? ti.Publisher : request.Sender;
            msg = new MessageRec { Id = topic.NextId++, Topic = topicName, Sender = sender, Body = request.Body, Time = DateTime.UtcNow };
            topic.Messages.Add(msg);
            // unbounded channel: TryWrite always succeeds; doing it under the lock keeps delivery in id order
            DeliveryQueue.Writer.TryWrite(new DeliveryItem { Msg = msg });
        }
        Persist(topic);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Publish id={msg.Id} topic={topicName} from {context.Peer}: ok");
        return Task.FromResult(new PublishReply { Id = msg.Id, Topic = msg.Topic, Sender = msg.Sender, Body = msg.Body, Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(msg.Time.ToUniversalTime()) });
    }

    public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<Message> responseStream, ServerCallContext context)
    {
        if (!ValidTopic(request.Topic)) throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid topic name"));
        var topic = GetOrCreate(request.Topic);
        var me = new Sub { Writer = responseStream, Client = context.Peer };
        lock (topic.Lock) topic.Subscribers.Add(me);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Subscriber added topic={request.Topic} {context.Peer}");
        try
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (TaskCanceledException) { }
        lock (topic.Lock) topic.Subscribers.Remove(me);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Subscriber gone topic={request.Topic} {context.Peer}");
    }

    public override Task<ListTopicsReply> ListTopics(ListTopicsRequest request, ServerCallContext context)
    {
        var reply = new ListTopicsReply();
        lock (TopicsLock)
        {
            foreach (var kv in Topics)
            {
                lock (kv.Value.Lock) reply.Topics.Add(new TopicInfo { Name = kv.Key, Size = kv.Value.Messages.Count });
            }
        }
        return Task.FromResult(reply);
    }

    static async Task DeliveryLoop()
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] DeliveryLoop started");
        await foreach (var item in DeliveryQueue.Reader.ReadAllAsync())
        {
            var msg = item.Msg;
            Topic topic;
            lock (TopicsLock) Topics.TryGetValue(msg.Topic, out topic);
            if (topic == null) continue;
            List<Sub> subs;
            lock (topic.Lock) subs = new List<Sub>(topic.Subscribers);
            var m = new Broker.Message { Id = msg.Id, Topic = msg.Topic, Sender = msg.Sender, Body = msg.Body, Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(msg.Time.ToUniversalTime()) };
            foreach (var sub in subs)
            {
                try { await sub.Writer.WriteAsync(m); Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Delivered id={msg.Id} topic={msg.Topic} to {sub.Client}: ok"); }
                catch { Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Delivery id={msg.Id} topic={msg.Topic} to {sub.Client}: write failed"); }
            }
            lock (topic.Lock) topic.Subscribers.RemoveAll(s => false); // keep; failures handled by Subscribe cancellation? gRPC stream write failure throws; we can't easily detect here. 
        }
    }

    static void Persist(Topic topic)
    {
        try
        {
            // Snapshot and write under one lock so an older snapshot never overwrites a newer one.
            lock (PersistLock)
            {
                string dir = Path.Combine(dataDir, topic.Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "messages.json");
                string tmp = file + ".tmp";
                List<MessageRec> snapshot;
                lock (topic.Lock) snapshot = new List<MessageRec>(topic.Messages);
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, FileJson));
                File.Move(tmp, file, overwrite: true);
            }
        }
        catch { }
    }

    static TokenInfo? LookupToken(string token)
    {
        if (!File.Exists(tokensFile)) return null;
        foreach (var line in File.ReadAllLines(tokensFile))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (parts[0] == token) return new TokenInfo { Publisher = parts[1], Topics = new List<string>(parts[2].Split(',')) };
        }
        return null;
    }
    class TokenInfo { public string Publisher; public List<string> Topics; }
}
