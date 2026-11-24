using System;
using System.Threading;
using Grpc.Net.Client;
using Broker;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("Usage: Publisher <brokerUrl> <topic> <sender> [token]"); return; }
        string broker = args[0];
        string topic = args[1];
        string sender = args[2];
        string? token = args.Length >= 4 ? args[3] : null;

        using var channel = GrpcChannel.ForAddress(broker);
        var client = new Broker.Broker.BrokerClient(channel);
        Console.WriteLine($"Publishing to {broker} topic={topic} as {sender}. Type lines:");
        var md = new Grpc.Core.Metadata();
        if (token != null) md.Add("authorization", $"Bearer {token}");
        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null) break;
            if (line.Trim().Length == 0) continue;
            try
            {
                var reply = client.Publish(new PublishRequest { Topic = topic, Sender = sender, Body = line, Token = token ?? "" }, md);
                Console.WriteLine($"Published id={reply.Id}");
            }
            catch (Grpc.Core.RpcException e)
            {
                Console.WriteLine($"RPC error: {e.Status.StatusCode} {e.Status.Detail}");
            }
            catch (Exception e) { Console.WriteLine($"error: {e.Message}"); }
        }
    }
}