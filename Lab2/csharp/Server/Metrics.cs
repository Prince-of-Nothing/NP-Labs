using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace CoffeeShop.Server
{
    static class Metrics
    {
        static readonly object Lock = new();
        static readonly DateTime Start = DateTime.UtcNow;
        static string dir = "metrics";
        static string ordersPath;
        static string summaryPath;
        static StreamWriter csv;
        static int workers;
        static bool initialized;

        static int connections, providers, accepted, refused, started, ready, cancelled, deliveries, alerts, errors;
        static double sumQueueWait, maxQueueWait, sumPrep, maxPrep;
        static int[] workerOrders = Array.Empty<int>();
        static double[] workerBusyMs = Array.Empty<double>();

        public static void Init(int workerCount)
        {
            lock (Lock)
            {
                if (initialized) return;
                workers = Math.Max(1, workerCount);
                workerOrders = new int[workers];
                workerBusyMs = new double[workers];
                Directory.CreateDirectory(dir);
                ordersPath = Path.Combine(dir, "server-orders.csv");
                summaryPath = Path.Combine(dir, "server-summary.json");
                bool fresh = !File.Exists(ordersPath);
                csv = new StreamWriter(ordersPath, append: true) { AutoFlush = true };
                if (fresh)
                    csv.WriteLine("order_id,endpoint,tag,items,expected_prep_ms,accepted_at,started_at,ready_at,queue_wait_ms,prep_actual_ms,worker_id,status");
                initialized = true;

                var t = new Thread(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(2000);
                        WriteSummary();
                    }
                }) { IsBackground = true, Name = "metrics-writer" };
                t.Start();

                Console.CancelKeyPress += (_, e) =>
                {
                    Shutdown();
                    e.Cancel = false;
                };
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            }
        }

        public static void Connection() { lock (Lock) connections++; }
        public static void Provider() { lock (Lock) providers++; }
        public static void Delivery() { lock (Lock) deliveries++; }
        public static void Alert() { lock (Lock) alerts++; }
        public static void Error() { lock (Lock) errors++; }
        public static void Refused() { lock (Lock) refused++; }

        public static void Accepted(Order o)
        {
            lock (Lock) accepted++;
        }

        public static void Started(Order o, int workerId)
        {
            lock (Lock)
            {
                o.StartedAt = DateTime.UtcNow;
                o.WorkerId = workerId;
                started++;
                if (o.AcceptedAt != default)
                {
                    double q = (o.StartedAt.Value - o.AcceptedAt).TotalMilliseconds;
                    sumQueueWait += q;
                    if (q > maxQueueWait) maxQueueWait = q;
                }
                workerOrders[workerId]++;
            }
        }

        public static void Finished(Order o, string status)
        {
            lock (Lock)
            {
                o.FinishedAt = DateTime.UtcNow;
                o.Status = status;
                if (status == "ready") ready++; else cancelled++;
                double prep = o.StartedAt.HasValue ? (o.FinishedAt.Value - o.StartedAt.Value).TotalMilliseconds : 0;
                if (o.StartedAt.HasValue)
                {
                    sumPrep += prep;
                    if (prep > maxPrep) maxPrep = prep;
                    if (o.WorkerId >= 0 && o.WorkerId < workerBusyMs.Length) workerBusyMs[o.WorkerId] += prep;
                }
                WriteOrderRow(o, prep);
            }
        }

        static void WriteOrderRow(Order o, double prepActual)
        {
            if (csv == null) return;
            double queueWait = o.StartedAt.HasValue && o.AcceptedAt != default
                ? (o.StartedAt.Value - o.AcceptedAt).TotalMilliseconds : -1;
            string started = o.StartedAt?.ToString("O") ?? "";
            string readyAt = o.FinishedAt?.ToString("O") ?? "";
            string endpoint = o.Client?.Endpoint ?? "";
            csv.WriteLine($"{o.Id},{endpoint},{o.Tag},{o.ItemsStr},{o.PrepTime},{o.AcceptedAt:O},{started},{readyAt},{queueWait:F0},{prepActual:F0},{o.WorkerId},{o.Status}");
        }

        public static object Summary()
        {
            lock (Lock) return BuildSummary();
        }

        static object BuildSummary()
        {
            double uptime = Math.Max((DateTime.UtcNow - Start).TotalSeconds, 0.001);
            int finished = ready + cancelled;
            return new
            {
                uptime_sec = Math.Round(uptime, 2),
                workers,
                connections,
                providers,
                orders_accepted = accepted,
                orders_refused = refused,
                orders_started = started,
                orders_ready = ready,
                orders_cancelled = cancelled,
                deliveries,
                alerts,
                errors,
                avg_queue_wait_ms = started > 0 ? Math.Round(sumQueueWait / started, 1) : 0,
                max_queue_wait_ms = Math.Round(maxQueueWait, 1),
                avg_prep_ms = started > 0 ? Math.Round(sumPrep / started, 1) : 0,
                max_prep_ms = Math.Round(maxPrep, 1),
                throughput_orders_per_sec = Math.Round(ready / uptime, 2),
                per_worker = Enumerable.Range(0, workers).Select(i => new
                {
                    worker = i,
                    orders = workerOrders[i],
                    busy_ms = Math.Round(workerBusyMs[i], 0),
                    utilization_pct = Math.Round(100.0 * workerBusyMs[i] / (uptime * 1000.0), 1)
                }).ToArray()
            };
        }

        static void WriteSummary()
        {
            lock (Lock)
            {
                if (!initialized) return;
                try
                {
                    string json = JsonSerializer.Serialize(BuildSummary(), new JsonSerializerOptions { WriteIndented = true });
                    string tmp = summaryPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, summaryPath, true);
                }
                catch { }
            }
        }

        public static void Shutdown()
        {
            lock (Lock)
            {
                if (!initialized) return;
                initialized = false;
                try { csv?.Flush(); csv?.Close(); } catch { }
                try
                {
                    string json = JsonSerializer.Serialize(BuildSummary(), new JsonSerializerOptions { WriteIndented = true });
                    string tmp = summaryPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, summaryPath, true);
                }
                catch { }
            }
        }
    }
}
