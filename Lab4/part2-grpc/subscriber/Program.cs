using System;
using System.Threading;
using Grpc.Net.Client;
using Broker;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Usage: Subscriber <brokerUrl> <topic>"); return; }
        string broker = args[0];
        string topic = args[1];
        using var channel = GrpcChannel.ForAddress(broker);
        var client = new Broker.Broker.BrokerClient(channel);
        Console.WriteLine($"Subscribing to {broker} topic={topic}");
        using var call = client.Subscribe(new SubscribeRequest { Topic = topic });
        while (call.ResponseStream.MoveNext(CancellationToken.None).Result)
        {
            var m = call.ResponseStream.Current;
            Console.WriteLine($"[{m.Topic}] {m.Sender}: {m.Body}");
        }
        Console.WriteLine("Stream ended");
    }
}