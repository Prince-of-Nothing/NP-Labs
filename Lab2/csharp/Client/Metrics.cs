using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CoffeeShop.Client
{
    static class Metrics
    {
        class Rec
        {
            public int Tag;
            public int Id;
            public string Items = "";
            public int Prep;
            public DateTime SentAt;
            public DateTime? AcceptedAt;
            public DateTime? ReadyAt;
            public string Status = "pending";
        }

        static readonly object Lock = new();
        static readonly Dictionary<int, Rec> Pending = new();
        static StreamWriter csv;
        static string summaryPath;
        static bool enabled;
        static int nextTag;
        static int sent, accepted, refused, ready;
        static double sumAccept, maxAccept, sumWait, maxWait, sumTotal, maxTotal;

        public static void Init(string role)
        {
            if (role != "client") return;
            lock (Lock)
            {
                Directory.CreateDirectory("metrics");
                // One file per client process: several clients can run at the same
                // time (the lab asks for many clients) without fighting over a file.
                string id = Environment.ProcessId.ToString();
                string ordersPath = Path.Combine("metrics", $"client-orders-{id}.csv");
                bool fresh = !File.Exists(ordersPath);
                csv = new StreamWriter(ordersPath, append: true) { AutoFlush = true };
                if (fresh)
                    csv.WriteLine("tag,order_id,items,prep_ms,sent_at,accepted_at,ready_at,accept_latency_ms,wait_ms,total_ms,status");
                summaryPath = Path.Combine("metrics", $"client-summary-{id}.json");
                enabled = true;
                Console.CancelKeyPress += (_, _) => Shutdown();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            }
        }

        public static int NextTag() { lock (Lock) { return ++nextTag; } }

        public static void Sent(int tag, string items)
        {
            lock (Lock)
            {
                if (!enabled) return;
                Pending[tag] = new Rec { Tag = tag, Items = items, SentAt = DateTime.UtcNow };
                sent++;
            }
        }

        public static void Accepted(int tag, int id, int prep)
        {
            lock (Lock)
            {
                if (!enabled || !Pending.TryGetValue(tag, out var r)) return;
                r.Id = id;
                r.Prep = prep;
                r.AcceptedAt = DateTime.UtcNow;
                accepted++;
                double lat = (r.AcceptedAt.Value - r.SentAt).TotalMilliseconds;
                sumAccept += lat;
                if (lat > maxAccept) maxAccept = lat;
            }
        }

        public static void Refused(int tag)
        {
            lock (Lock)
            {
                if (!enabled || !Pending.TryGetValue(tag, out var r)) return;
                r.Status = "refused";
                refused++;
                Pending.Remove(tag);
                WriteRow(r);
            }
        }

        public static void Ready(int tag, int id)
        {
            lock (Lock)
            {
                if (!enabled || !Pending.TryGetValue(tag, out var r)) return;
                r.Id = id;
                r.ReadyAt = DateTime.UtcNow;
                r.Status = "ready";
                ready++;
                double wait = r.AcceptedAt.HasValue ? (r.ReadyAt.Value - r.AcceptedAt.Value).TotalMilliseconds : 0;
                double total = (r.ReadyAt.Value - r.SentAt).TotalMilliseconds;
                sumWait += wait;
                if (wait > maxWait) maxWait = wait;
                sumTotal += total;
                if (total > maxTotal) maxTotal = total;
                Pending.Remove(tag);
                WriteRow(r);
            }
        }

        static void WriteRow(Rec r)
        {
            if (csv == null) return;
            double acceptLat = r.AcceptedAt.HasValue ? (r.AcceptedAt.Value - r.SentAt).TotalMilliseconds : -1;
            double wait = r.AcceptedAt.HasValue && r.ReadyAt.HasValue ? (r.ReadyAt.Value - r.AcceptedAt.Value).TotalMilliseconds : -1;
            double total = r.ReadyAt.HasValue ? (r.ReadyAt.Value - r.SentAt).TotalMilliseconds : -1;
            string acc = r.AcceptedAt?.ToString("O") ?? "";
            string rdy = r.ReadyAt?.ToString("O") ?? "";
            csv.WriteLine($"{r.Tag},{r.Id},{r.Items},{r.Prep},{r.SentAt:O},{acc},{rdy},{acceptLat:F0},{wait:F0},{total:F0},{r.Status}");
        }

        public static object Summary()
        {
            lock (Lock)
            {
                return new
                {
                    sent,
                    accepted,
                    refused,
                    ready,
                    pending = Pending.Count,
                    avg_accept_latency_ms = accepted > 0 ? Math.Round(sumAccept / accepted, 1) : 0,
                    max_accept_latency_ms = Math.Round(maxAccept, 1),
                    avg_ready_wait_ms = ready > 0 ? Math.Round(sumWait / ready, 1) : 0,
                    max_ready_wait_ms = Math.Round(maxWait, 1),
                    avg_total_ms = ready > 0 ? Math.Round(sumTotal / ready, 1) : 0,
                    max_total_ms = Math.Round(maxTotal, 1)
                };
            }
        }

        public static string SummaryText()
        {
            return JsonSerializer.Serialize(Summary(), new JsonSerializerOptions { WriteIndented = true });
        }

        public static void Shutdown()
        {
            lock (Lock)
            {
                if (!enabled) return;
                enabled = false;
                try { csv?.Flush(); csv?.Close(); } catch { }
                try
                {
                    string json = JsonSerializer.Serialize(Summary(), new JsonSerializerOptions { WriteIndented = true });
                    string tmp = summaryPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, summaryPath, true);
                }
                catch { }
            }
        }
    }
}
