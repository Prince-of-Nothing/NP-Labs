using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Usage: Subscriber <brokerUrl> <topic>"); return; }
        string broker = args[0].TrimEnd('/');
        string topic = args[1];
        var http = new HttpClient();
        http.Timeout = TimeSpan.FromHours(24);
        Console.WriteLine($"Subscribing to {broker} topic={topic}");
        var resp = http.GetAsync($"{broker}/topics/{topic}/stream", HttpCompletionOption.ResponseHeadersRead).Result;
        using var stream = resp.Content.ReadAsStream();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                Console.WriteLine($"[{r.GetProperty("topic").GetString()}] {r.GetProperty("sender").GetString()}: {r.GetProperty("body").GetString()}");
            }
            catch { Console.WriteLine(line); }
        }
        Console.WriteLine("Stream ended");
    }
}