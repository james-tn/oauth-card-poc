// OAuth POC (.NET / new Microsoft Agents SDK) entry point.
//
// Mirrors microsoft/Agents-for-net/src/samples/Authorization/AutoSignIn/Program.cs
// so we get the same hosting and JWT auth behavior as the customer's setup.

using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using OAuthPocDotnet;

// WORKAROUND for the M365 Copilot magic-code-page bug:
// When opt-in, every inbound Activity.ChannelId is constructed with
// fullNotation=false, so ChannelId.ToString() (and therefore the value the SDK
// sends to BF Token Service GetSignInResource) is just "msteams" instead of
// "msteams:COPILOT". BF's PostSignInCallback page exact-matches "msteams" to
// pick the silent postMessage+window.close() variant; without this the
// "msteams:COPILOT" value falls through to the static 6-digit code page.
// SubChannel is still populated on each ChannelId (e.g. "COPILOT") so any code
// that wants to know the surface can still inspect activity.ChannelId.SubChannel.
// Toggle via env var so we can A/B test in deployed environments.
if (string.Equals(Environment.GetEnvironmentVariable("DISABLE_CHANNEL_PRODUCT_SUFFIX"),
                  "true", StringComparison.OrdinalIgnoreCase))
{
    ProtocolJsonSerializer.ChannelIdIncludesProduct = false;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

// Capture full URL + body for outbound BF calls — prove what channelId
// reaches BF Token Service. Toggled with HTTP_DUMP=true.
if (string.Equals(Environment.GetEnvironmentVariable("HTTP_DUMP"),
                  "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter,
        OAuthPocDotnet.HttpDumpFilter>();
}

// Strip conversation.conversation.tenantId from BF Token Service state= payload.
// Hypothesis test for whether BF's PostSignInCallback uses tenantId to decide
// silent SSO vs. magic-code page (legacy Python SDK omits tenantId and gets
// silent SSO; new .NET SDK includes it and gets the magic-code page).
// Gated by STRIP_TENANT_ID=true.
if (string.Equals(Environment.GetEnvironmentVariable("STRIP_TENANT_ID"),
                  "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter,
        OAuthPocDotnet.StripTenantIdFilter>();
}

// Register our agent. The Agents SDK will instantiate it per turn.
builder.AddAgent<OAuthPocAgent>();

// In-memory state. Fine for a single-instance POC.
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// Paycor FIX A (Mirko's workaround): restrict auto-sign-in to Message activities
// only. By default ("AutoSignIn": true => AutoSignInOnForAny) the SDK evaluates
// auto-sign-in on EVERY activity, so a conversationUpdate (membersAdded) fired
// when a Copilot chat opens starts the OAuth flow prematurely (FlowStarted=true)
// with no usable card. The user's first real message then lands in OnContinueFlow
// and gets "Invalid sign in code" before ever seeing a card.
//
// Registering an AutoSignInSelector singleton overrides the config bool (the
// UserAuthorizationOptions ctor prefers a DI'd selector). This is exactly what
// builder.AddAgentApplicationOptions(AutoSignInOnForMessages) does internally.
// Gated so we can A/B test against the default AutoSignInOnForAny behavior.
if (string.Equals(Environment.GetEnvironmentVariable("AUTO_SIGNIN_MESSAGES_ONLY"),
                  "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<AutoSignInSelector>(
        UserAuthorizationOptions.AutoSignInOnForMessages);
}

// Generic OAuth diagnostics middleware (https://github.com/james-tn/cea-oauth-diagnostics).
// Toggled with OAUTH_DIAG=true so it can be enabled in deployed environments.
if (string.Equals(Environment.GetEnvironmentVariable("OAUTH_DIAG"),
                  "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<CeaOAuthDiagnostics.OAuthDiagnostics.InboundActivityLoggerMiddleware>();
}

// JWT validation for inbound activities from Bot Framework / Azure Bot Service.
// Configured by the "TokenValidation" section in appsettings.json.
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

if (string.Equals(Environment.GetEnvironmentVariable("OAUTH_DIAG"),
                  "true", StringComparison.OrdinalIgnoreCase))
{
    app.UseMiddleware<CeaOAuthDiagnostics.OAuthDiagnostics.InboundActivityLoggerMiddleware>();
}

// Diagnostic: dump raw inbound activity JSON BEFORE any SDK handler. Toggle
// with INBOUND_HTTP_DUMP=true. Used to observe the invoke sequence
// (signin/tokenExchange, signin/verifyState) coming from Copilot Chat
// after silent SSO.
//
// The same middleware ALSO mutates verifyState's value.state if
// INBOUND_MUTATE_VERIFYSTATE_CODE is set — used to reproduce the Paycor
// "Invalid sign in code" symptom on demand.
if (string.Equals(Environment.GetEnvironmentVariable("INBOUND_HTTP_DUMP"),
                  "true", StringComparison.OrdinalIgnoreCase)
    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INBOUND_MUTATE_VERIFYSTATE_CODE")))
{
    app.UseMiddleware<OAuthPocDotnet.InboundHttpDump>();
}

app.MapAgentRootEndpoint();
app.MapAgentApplicationEndpoints(requireAuth: !app.Environment.IsDevelopment());

if (app.Environment.IsDevelopment())
{
    app.Urls.Add("http://localhost:3978");
}

app.Run();
