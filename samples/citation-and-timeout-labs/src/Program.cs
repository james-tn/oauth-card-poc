using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using CopilotLabs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.AddAgent<LabsAgent>();
builder.Services.AddSingleton<IStorage, MemoryStorage>();
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapAgentRootEndpoint();
app.MapAgentApplicationEndpoints(requireAuth: !app.Environment.IsDevelopment());

if (app.Environment.IsDevelopment())
{
    app.Urls.Add("http://localhost:3978");
}

app.Run();
