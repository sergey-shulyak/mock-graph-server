using MockGraph.Server.Auth;
using MockGraph.Server.Endpoints;
using MockGraph.Server.Options;
using MockGraph.Server.Serialization;
using MockGraph.Server.State;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<MockGraphOptions>(builder.Configuration.GetSection(MockGraphOptions.SectionName));
builder.Services.AddSingleton<GraphStore>();

var app = builder.Build();
_ = app.Services.GetRequiredService<GraphStore>();

app.UseMiddleware<GraphErrorMiddleware>();
app.UseMiddleware<AuthMiddleware>();
app.MapDirectoryEndpoints();

app.Run();

public partial class Program;