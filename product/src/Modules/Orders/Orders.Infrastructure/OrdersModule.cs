using EventTicketing.Orders.Application;
using EventTicketing.Orders.Application.Checkout;
using EventTicketing.Orders.Infrastructure.Persistence;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Infrastructure.Http;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Orders.Infrastructure;

public sealed record StartCheckoutRequest(Guid HoldId);

public sealed record OrderStatusResponse(Guid OrderId, string Status, long TotalMinorUnits, string Currency);

public sealed class OrdersModule : IModule
{
    public string Name => "Orders";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<OrdersDbContext>(host, OrdersDbContext.SchemaName);

        var application = typeof(StartCheckout).Assembly;
        builder.Services.AddCommandHandlers(application);
        builder.Services.AddMessageHandlers(application);

        builder.Services.AddSingleton(builder.Configuration.GetSection("Checkout").Get<CheckoutPolicy>() ?? new CheckoutPolicy());
        builder.Services.AddScoped<IOrderRepository, OrderRepository>();
        builder.Services.AddScoped<IPriceList, PriceList>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host != HostKind.PublicApi)
        {
            return;
        }

        var orders = endpoints.MapGroup("/orders").WithTags("Orders");

        orders.MapPost("/", async (StartCheckoutRequest request, ICommandHandler<StartCheckout, CheckoutStarted> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new StartCheckout(request.HoldId), cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/orders/{result.Value.OrderId}", result.Value)
                    : result.Error.ToProblem();
            })
            .WithName("StartCheckout")
            .WithSummary("Turn a hold into an order and get the URL to pay on")
            .Produces<CheckoutStarted>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // The provider's redirect proves nothing; the return page polls this until the webhook has confirmed the order.
        orders.MapGet("/{orderId:guid}", async (Guid orderId, OrdersDbContext db, IExecutionContext caller, CancellationToken cancellationToken) =>
            {
                var order = await db.Orders.AsNoTracking()
                    .Where(o => o.Id == orderId && o.CustomerId == caller.UserId)
                    .Select(o => new OrderStatusResponse(o.Id, o.Status.ToString(), o.TotalMinorUnits, o.Currency))
                    .SingleOrDefaultAsync(cancellationToken);

                return order is null ? Results.NotFound() : Results.Ok(order);
            })
            .WithName("GetOrder")
            .WithSummary("Your order's status")
            .Produces<OrderStatusResponse>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
