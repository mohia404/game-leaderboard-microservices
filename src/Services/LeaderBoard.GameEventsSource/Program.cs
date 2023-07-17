using System.Reflection;
using Humanizer;
using LeaderBoard.DbMigrator;
using LeaderBoard.GameEventsSource;
using LeaderBoard.GameEventsSource.GameEvent.Features;
using LeaderBoard.GameEventsSource.Players.Features.CreatingPlayer;
using LeaderBoard.GameEventsSource.Players.Models;
using LeaderBoard.GameEventsSource.Shared.Data.EFDbContext;
using LeaderBoard.GameEventsSource.Shared.Extensions.WebApplicationBuilderExtensions;
using LeaderBoard.SharedKernel.Application.Data.EFContext;
using LeaderBoard.SharedKernel.Bus;
using LeaderBoard.SharedKernel.Contracts.Data;
using LeaderBoard.SharedKernel.Core.Exceptions;
using LeaderBoard.SharedKernel.Core.Extensions.ServiceCollectionExtensions;
using LeaderBoard.SharedKernel.Postgres;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseDefaultServiceProvider(
    (context, options) =>
    {
        var isDevMode =
            context.HostingEnvironment.IsDevelopment()
            || context.HostingEnvironment.IsEnvironment("test")
            || context.HostingEnvironment.IsStaging();

        // Handling Captive Dependency Problem
        // https://ankitvijay.net/2020/03/17/net-core-and-di-beware-of-captive-dependency/
        // https://levelup.gitconnected.com/top-misconceptions-about-dependency-injection-in-asp-net-core-c6a7afd14eb4
        // https://blog.ploeh.dk/2014/06/02/captive-dependency/
        // https://andrewlock.net/new-in-asp-net-core-3-service-provider-validation/
        // https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/web-host?view=aspnetcore-7.0&viewFallbackFrom=aspnetcore-2.2#scope-validation
        // CreateDefaultBuilder and WebApplicationBuilder in minimal apis sets `ServiceProviderOptions.ValidateScopes` and `ServiceProviderOptions.ValidateOnBuild` to true if the app's environment is Development.
        // check dependencies are used in a valid life time scope
        options.ValidateScopes = isDevMode;
        // validate dependencies on the startup immediately instead of waiting for using the service
        options.ValidateOnBuild = isDevMode;
    }
);

builder.Host.UseSerilog(
    (context, services, configuration) =>
    {
        //https://github.com/serilog/serilog-aspnetcore#two-stage-initialization
        configuration.ReadFrom
            .Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console();
    }
);

builder.AddAppProblemDetails();

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddValidatedOptions<GameEventSourceOptions>();

builder.AddPostgresDbContext<GameEventSourceDbContext>(
    migrationAssembly: Assembly.GetExecutingAssembly()
);
builder.AddPostgresDbContext<InboxOutboxDbContext>(
    migrationAssembly: typeof(MigrationRootMetadata).Assembly
);
builder.Services.AddTransient<ISeeder, DataSeeder>();

builder.Services.AddHostedService<GameEventsWorker>();

builder.Services.AddMassTransit(x =>
{
    // setup masstransit for outbox and producing messages through `IPublishEndpoint`
    x.AddEntityFrameworkOutbox<InboxOutboxDbContext>(o =>
    {
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.UsingRabbitMq(
        (_, cfg) =>
        {
            cfg.AutoStart = true;
            // https://masstransit-project.com/usage/exceptions.html#retry
            // https://markgossa.com/2022/06/masstransit-exponential-back-off.html
            cfg.UseMessageRetry(r =>
            {
                r.Exponential(
                        3,
                        TimeSpan.FromMilliseconds(200),
                        TimeSpan.FromMinutes(120),
                        TimeSpan.FromMilliseconds(200)
                    )
                    .Ignore<ValidationException>(); // don't retry if we have invalid data and message goes to _error queue masstransit
            });
        }
    );
});
builder.Services.AddScoped<IBusPublisher, BusPublisher>();

var app = builder.Build();

app.UseExceptionHandler(options: new ExceptionHandlerOptions { AllowStatusCode404Response = true });

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("test"))
{
    // https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/handle-errrors
    app.UseDeveloperExceptionPage();
}

app.UseSerilogRequestLogging();

var playerGroup = app.MapGroup("players").WithTags(nameof(Player).Pluralize());
playerGroup.MapCreatePlayerEndpoint();

var gameEventGroup = app.MapGroup("game-events").WithTags("GameEvents");
gameEventGroup.MapCreateGameEventEndpoint();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

using (var scope = app.Services.CreateScope())
{
    var leaderBoardDbContext = scope.ServiceProvider.GetRequiredService<GameEventSourceDbContext>();
    await leaderBoardDbContext.Database.MigrateAsync();

    var inboxOutboxDbContext = scope.ServiceProvider.GetRequiredService<InboxOutboxDbContext>();
    await inboxOutboxDbContext.Database.MigrateAsync();

    var seeders = scope.ServiceProvider.GetServices<ISeeder>();
    foreach (var seeder in seeders)
        await seeder.SeedAsync();
}

app.Run();
