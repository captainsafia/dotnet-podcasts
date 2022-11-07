using Asp.Versioning;
using Asp.Versioning.Conventions;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Podcast.API.Routes;
using Podcast.Infrastructure.Data;
using Podcast.Infrastructure.Http.Feeds;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

#region DB_SERVICES
// Database and storage related-services
var connectionString = builder.Configuration.GetConnectionString("PodcastDb");
builder.Services.AddSqlServer<PodcastDbContext>(connectionString);
var queueConnectionString = builder.Configuration.GetConnectionString("FeedQueue");
builder.Services.AddSingleton(new QueueClient(queueConnectionString, "feed-queue"));
builder.Services.AddHttpClient<IFeedClient, FeedClient>();
#endregion

#region AUTH
// Authentication and authorization-related services
builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration);
builder.Services.AddAuthorizationBuilder().AddPolicy("modify_feeds", policy => policy.RequireScope("API.Access"));
#endregion

#region API_DOCS
// OpenAPI and versioning-related services
builder.Services.AddSwaggerGen();
builder.Services.Configure<SwaggerGeneratorOptions>(opts => {
    opts.InferSecuritySchemes = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(2, 0);
    options.ReportApiVersions = true;
    options.AssumeDefaultVersionWhenUnspecified = true;
});
#endregion

#region CORS
builder.Services.AddCors(setup =>
{
    setup.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});
#endregion

#region RATELIMITING
// Rate-limiting and output caching-related services
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("feeds", options =>
{
    options.PermitLimit = 5;
    options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    options.QueueLimit = 0;
    options.Window = TimeSpan.FromSeconds(2);
    options.AutoReplenishment = false;
}));
#endregion

var app = builder.Build();

await EnsureDbAsync(app.Services);

#region MIDDLEWARES
// Register required middlewares
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", ".NET Podcast API");
});
app.UseCors();
app.UseRateLimiter();
#endregion

var versionSet = app.NewApiVersionSet()
                    .HasApiVersion(1.0)
                    .HasApiVersion(2.0)
                    .ReportApiVersions()
                    .Build();

var root = app.MapGroup("");
root.WithApiVersionSet(versionSet);
var shows = root.MapGroup("/shows");
var categories = root.MapGroup("/categories");
var episodes = root.MapGroup("/episodes");

shows
    .MapShowsApi()
    .MapToApiVersion(1.0);

categories
    .MapCategoriesApi()
    .MapToApiVersion(1.0);

episodes
    .MapEpisodesApi()
    .MapToApiVersion(1.0);

var feedIngestionEnabled = app.Configuration.GetValue<bool>("Features:FeedIngestion");

if (feedIngestionEnabled)
{
    var feeds = app.MapGroup("/feeds");
    feeds.MapFeedsApi()
        .WithApiVersionSet(versionSet)
        .MapToApiVersion(2.0)
        .RequireRateLimiting("feeds");
}

app.Run();

static async Task EnsureDbAsync(IServiceProvider sp)
{
    await using var db = sp.CreateScope().ServiceProvider.GetRequiredService<PodcastDbContext>();
    await db.Database.MigrateAsync();
}