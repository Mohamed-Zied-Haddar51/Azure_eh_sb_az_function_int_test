using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Producer;
using Azure.Messaging.ServiceBus;
using System.Text;
using NUnit.Framework; // Import NUnit namespace
using Testcontainers.EventHubs;
using Testcontainers.ServiceBus;
using NUnit.Framework.Legacy;

[TestFixture] // Use TestFixture for setting up a test class
public class EventHubToServiceBusIntegrationTest
{
    private readonly EventHubsContainer _ehContainer;
    private readonly ServiceBusContainer _sbContainer;

    private const string EventHubName = "myeventhub";
    private const string ServiceBusQueue = "myqueue";

    public EventHubToServiceBusIntegrationTest()
    {
        // Configure Event Hubs
        var ehConfig = EventHubsServiceConfiguration
            .Create()
            .WithEntity(
                EventHubName,
                partitionCount: 1);

        _ehContainer = new EventHubsBuilder()
            .WithImage("mcr.microsoft.com/azure-messaging/eventhubs-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .WithConfigurationBuilder(ehConfig)
            .Build();

        // Configure Service Bus
        _sbContainer = new ServiceBusBuilder()
            .WithImage("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .WithConfig("Config.json")
            .Build();
    }

    [OneTimeSetUp] // Use OneTimeSetUp for initializing resources once before any test runs
    public async Task InitializeAsync()
    {
        await _ehContainer.StartAsync().ConfigureAwait(false);
        await _sbContainer.StartAsync().ConfigureAwait(false);
    }

    [OneTimeTearDown] // Use OneTimeTearDown for cleaning up resources once after all tests have run
    public async Task DisposeAsync()
    {
        await _ehContainer.DisposeAsync();
        await _sbContainer.DisposeAsync();
    }

    [Test] // Use Test for defining a test method
    public async Task MessageSentToEventHub_IsReceivedAndForwardedToServiceBus()
    {
        // Arrange
        var messageContent = $"Test-{Guid.NewGuid()}";

        // 1. Send message to Event Hub
        await using (var producer = new EventHubProducerClient(
            _ehContainer.GetConnectionString(),
            EventHubName))
        {
            using var eventBatch = await producer.CreateBatchAsync(CancellationToken.None);
            eventBatch.TryAdd(new EventData(Encoding.UTF8.GetBytes(messageContent)));
            await producer.SendAsync(eventBatch, CancellationToken.None);
        }

        // 2. Receive from Event Hub
        string? receivedFromEventHub = null;
        await using (var consumer = new EventHubConsumerClient(
            EventHubConsumerClient.DefaultConsumerGroupName,
            _ehContainer.GetConnectionString(),
            EventHubName))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (PartitionEvent partitionEvent in consumer.ReadEventsAsync(cts.Token))
            {
                receivedFromEventHub = Encoding.UTF8.GetString(partitionEvent.Data.Body.ToArray());
                break;
            }
        }

        ClassicAssert.AreEqual(messageContent, receivedFromEventHub);

        // 3. Send to Service Bus
        await using (var sbClient = new ServiceBusClient(_sbContainer.GetConnectionString()))
        {
            var sender = sbClient.CreateSender(ServiceBusQueue);
            await sender.SendMessageAsync(new ServiceBusMessage(receivedFromEventHub), CancellationToken.None);
        }

        // 4. Receive from Service Bus
        await using (var sbReceiverClient = new ServiceBusClient(_sbContainer.GetConnectionString()))
        {
            var receiver = sbReceiverClient.CreateReceiver(ServiceBusQueue);
            var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

            ClassicAssert.NotNull(message);
            ClassicAssert.AreEqual(messageContent, message.Body.ToString());

            await receiver.CompleteMessageAsync(message, CancellationToken.None);
        }
    }
}