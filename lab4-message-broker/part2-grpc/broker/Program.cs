using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(5000, lo => lo.Protocols = HttpProtocols.Http2));
var app = builder.Build();
app.MapGrpcService<BrokerService>();
app.MapGet("/", () => "gRPC broker");
app.Run();