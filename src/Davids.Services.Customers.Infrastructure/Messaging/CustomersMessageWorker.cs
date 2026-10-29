using System.Text;
using System.Text.Json;
using Davids.Services.Customers.Application.Events;
using Davids.Services.Customers.Application.Services;
using Davids.Services.Customers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Davids.Services.Customers.Infrastructure.Messaging;

public sealed class CustomersMessageWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<CustomersMessageWorker> logger) : BackgroundService
{
    private const string SignUpQueue = "customers-service/identity.signed_up";
    private const string OrderCompletedQueue = "customers-service/orders.order_completed";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Customers RabbitMQ worker disconnected; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("messaging")
            ?? throw new InvalidOperationException("Missing RabbitMQ messaging connection string.");
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var publisher = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await using var consumerChannel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await publisher.ExchangeDeclareAsync("customers", ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await consumerChannel.ExchangeDeclareAsync("identity", ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await consumerChannel.ExchangeDeclareAsync("orders", ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await consumerChannel.ExchangeDeclareAsync("customers.dead", ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await consumerChannel.QueueDeclareAsync("customers-service.dead", durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await consumerChannel.QueueBindAsync("customers-service.dead", "customers.dead", "#",
            cancellationToken: cancellationToken);

        var deadLetterArguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = "customers.dead"
        };
        await consumerChannel.QueueDeclareAsync(SignUpQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: deadLetterArguments, cancellationToken: cancellationToken);
        await consumerChannel.QueueBindAsync(SignUpQueue, "identity", "signed_up", cancellationToken: cancellationToken);
        await consumerChannel.QueueDeclareAsync(OrderCompletedQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: deadLetterArguments, cancellationToken: cancellationToken);
        await consumerChannel.QueueBindAsync(OrderCompletedQueue, "orders", "order_completed", cancellationToken: cancellationToken);
        await consumerChannel.BasicQosAsync(0, 1, global: false, cancellationToken);

        var signUpConsumer = new AsyncEventingBasicConsumer(consumerChannel);
        signUpConsumer.ReceivedAsync += async (_, args) =>
            await ProcessMessageAsync<SignedUp>(args, consumerChannel, ProcessSignedUpAsync, cancellationToken);
        await consumerChannel.BasicConsumeAsync(SignUpQueue, autoAck: false, signUpConsumer, cancellationToken);

        var orderConsumer = new AsyncEventingBasicConsumer(consumerChannel);
        orderConsumer.ReceivedAsync += async (_, args) =>
            await ProcessMessageAsync<OrderCompleted>(args, consumerChannel, ProcessOrderCompletedAsync, cancellationToken);
        await consumerChannel.BasicConsumeAsync(OrderCompletedQueue, autoAck: false, orderConsumer, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            var messages = await db.Outbox.Where(message => message.PublishedAt == null)
                .OrderBy(message => message.CreatedAt)
                .Take(100)
                .ToListAsync(cancellationToken);

            foreach (var message in messages)
            {
                var properties = new BasicProperties
                {
                    Persistent = true,
                    MessageId = message.Id,
                    ContentType = "application/json",
                    Type = message.Type
                };
                await publisher.BasicPublishAsync(
                    "customers",
                    message.Type,
                    mandatory: false,
                    properties,
                    Encoding.UTF8.GetBytes(message.Payload),
                    cancellationToken);
                message.PublishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task ProcessMessageAsync<TMessage>(
        BasicDeliverEventArgs args,
        IChannel channel,
        Func<TMessage, ICustomersService, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var messageId = args.BasicProperties.MessageId;
            if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 128)
            {
                throw new InvalidDataException("A stable RabbitMQ MessageId (maximum 128 characters) is required.");
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await db.Inbox.AnyAsync(message => message.Id == messageId, cancellationToken))
            {
                var message = JsonSerializer.Deserialize<TMessage>(args.Body.Span, JsonOptions)
                    ?? throw new InvalidDataException("RabbitMQ message payload was empty.");
                var service = scope.ServiceProvider.GetRequiredService<ICustomersService>();
                await handler(message, service, cancellationToken);
                db.Inbox.Add(new InboxMessage { Id = messageId, ReceivedAt = DateTime.UtcNow });
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Customers message processing failed; message is dead-lettered.");
            await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken);
        }
    }

    private static Task ProcessSignedUpAsync(
        SignedUp message,
        ICustomersService service,
        CancellationToken cancellationToken)
        => service.CreateFromSignedUpAsync(message, cancellationToken);

    private static Task ProcessOrderCompletedAsync(
        OrderCompleted message,
        ICustomersService service,
        CancellationToken cancellationToken)
        => service.RecordCompletedOrderAsync(message, cancellationToken);
}
