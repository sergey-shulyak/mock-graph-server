using MockGraph.Server.Auth;
using MockGraph.Server.Endpoints;
using MockGraph.Server.Options;
using MockGraph.Server.Serialization;
using MockGraph.Server.State;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<MockGraphOptions>(builder.Configuration.GetSection(MockGraphOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GraphStore>();
builder.Services.AddSingleton<ThrottleState>();

var app = builder.Build();
_ = app.Services.GetRequiredService<GraphStore>();

app.UseMiddleware<GraphErrorMiddleware>();
app.UseMiddleware<ThrottleMiddleware>();
app.UseMiddleware<AuthMiddleware>();
app.UseMiddleware<BatchMiddleware>();
app.UseRouting(); // explicit, so the batch middleware runs before routing and can route each sub-request itself
app.MapDirectoryEndpoints();
app.MapSitesEndpoints();
app.MapDriveEndpoints();
app.MapDeltaEndpoints();
app.MapAdminEndpoints();

app.Run();

public partial class Program;