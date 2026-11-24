using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("Usage: Publisher <brokerUrl> <topic> <sender> [token]"); return; }
        string broker = args[0].TrimEnd('/');
        string topic = args[1];
        string sender = args[2];
        string? token = args.Length >= 4 ? args[3] : null;

        var http = new HttpClient();
        Console.WriteLine($"Publishing to {broker} topic={topic} as {sender}. Type lines:");
        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null) break;
            if (line.Trim().Length == 0) continue;
            var payload = new { sender = sender, body = line };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            if (token != null) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            try
            {
                var resp = http.PostAsync($"{broker}/topics/{topic}/messages", content).Result;
                string respBody = resp.Content.ReadAsStringAsync().Result;
                Console.WriteLine($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {respBody}");
            }
            catch (Exception e) { Console.WriteLine($"error: {e.Message}"); }
            if (token != null) http.DefaultRequestHeaders.Authorization = null;
        }
    }
}