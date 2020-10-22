﻿namespace NServiceBus.AcceptanceTests.Core.DelayedDelivery.TimeoutManager
{
    using System;
    using System.Threading.Tasks;
    using AcceptanceTesting;
    using EndpointTemplates;
    using Extensibility;
    using Features;
    using Microsoft.Extensions.DependencyInjection;
    using NServiceBus.Persistence;
    using NServiceBus.Pipeline;
    using NServiceBus.Timeout.Core;
    using NUnit.Framework;

    class When_dispatch_fails_on_removal_SendsAtomicWithReceive : NServiceBusAcceptanceTest
    {
        [Test]
        public async Task Should_move_control_message_to_errors_and_dispatch_original_message_to_handler()
        {
            Requires.TimeoutStorage();

            var context = await Scenario.Define<Context>()
                .WithEndpoint<Endpoint>(b => b
                    .DoNotFailOnErrorMessages()
                    .When((bus, c) =>
                    {
                        var options = new SendOptions();
                        options.DelayDeliveryWith(TimeSpan.FromMinutes(1));
                        options.RouteToThisEndpoint();
                        options.SetMessageId(c.TestRunId.ToString());

                        return bus.Send(new MyMessage
                        {
                            Id = c.TestRunId
                        }, options);
                    }))
                .Done(c => c.FailedTimeoutMovedToError && c.DelayedMessageDeliveredToHandler)
                .Run();

            Assert.IsTrue(context.DelayedMessageDeliveredToHandler);
            Assert.IsTrue(context.FailedTimeoutMovedToError);
        }

        public class Context : ScenarioContext
        {
            public bool FailedTimeoutMovedToError { get; set; }
            public bool DelayedMessageDeliveredToHandler { get; set; }
        }

        public class Endpoint : EndpointConfigurationBuilder
        {
            public Endpoint()
            {
                EndpointSetup<DefaultServer>((config, runDescriptor) =>
                {
                    config.EnableFeature<TimeoutManager>();
                    config.UsePersistence<FakeTimeoutPersistence>();
                    config.SendFailedMessagesTo(AcceptanceTesting.Customization.Conventions.EndpointNamingConvention(typeof(Endpoint)));
                    config.RegisterComponents(c =>
                    {
                        c.AddSingleton<FakeTimeoutStorage>();
                        c.AddSingleton<IPersistTimeouts>(sp => sp.GetRequiredService<FakeTimeoutStorage>());
                        c.AddSingleton<IQueryTimeouts>(sp => sp.GetRequiredService<FakeTimeoutStorage>());
                    });
                    config.Pipeline.Register<BehaviorThatLogsControlMessageDelivery.Registration>();
                    config.LimitMessageProcessingConcurrencyTo(1);
                    ////config.ConfigureTransport().Transactions(TransportTransactionMode.SendsAtomicWithReceive);
                });
            }

            class Handler : IHandleMessages<MyMessage>
            {
                public Handler(Context testContext)
                {
                    this.testContext = testContext;
                }

                public Task Handle(MyMessage message, IMessageHandlerContext context)
                {
                    if (message.Id == testContext.TestRunId)
                    {
                        testContext.DelayedMessageDeliveredToHandler = true;
                    }

                    return Task.FromResult(0);
                }

                Context testContext;
            }

            class FakeTimeoutPersistence : PersistenceDefinition
            {
                public FakeTimeoutPersistence()
                {
                    Supports<StorageType.Timeouts>(s => { });
                }
            }

            class FakeTimeoutStorage : IQueryTimeouts, IPersistTimeouts
            {
                public FakeTimeoutStorage(Context testContext)
                {
                    this.testContext = testContext;
                }

                public Task Add(TimeoutData timeout, ContextBag context)
                {
                    if (testContext.TestRunId.ToString() == timeout.Headers[Headers.MessageId])
                    {
                        timeout.Id = testContext.TestRunId.ToString();
                        timeout.Time = DateTime.UtcNow;

                        timeoutData = timeout;
                    }

                    return Task.FromResult(0);
                }

                public Task<bool> TryRemove(string timeoutId, ContextBag context)
                {
                    throw new Exception("Simulated exception on removing timeout data.");
                }

                public Task<TimeoutData> Peek(string timeoutId, ContextBag context)
                {
                    if (timeoutId == testContext.TestRunId.ToString() && timeoutData != null)
                    {
                        return Task.FromResult(timeoutData);
                    }

                    return Task.FromResult((TimeoutData)null);
                }

                public Task RemoveTimeoutBy(Guid sagaId, ContextBag context)
                {
                    throw new NotImplementedException();
                }

                public Task<TimeoutsChunk> GetNextChunk(DateTime startSlice)
                {
                    var timeouts = timeoutData != null
                        ? new[]
                        {
                            new TimeoutsChunk.Timeout(timeoutData.Id, timeoutData.Time)
                        }
                        : new TimeoutsChunk.Timeout[0];

                    return Task.FromResult(new TimeoutsChunk(timeouts, DateTime.UtcNow + TimeSpan.FromSeconds(10)));
                }

                TimeoutData timeoutData;
                Context testContext;
            }

            class BehaviorThatLogsControlMessageDelivery : IBehavior<ITransportReceiveContext, ITransportReceiveContext>
            {
                public BehaviorThatLogsControlMessageDelivery(Context testContext)
                {
                    this.testContext = testContext;
                }

                public Task Invoke(ITransportReceiveContext context, Func<ITransportReceiveContext, Task> next)
                {
                    if (context.Message.Headers.ContainsKey(Headers.ControlMessageHeader) &&
                        context.Message.Headers["Timeout.Id"] == testContext.TestRunId.ToString())
                    {
                        testContext.FailedTimeoutMovedToError = true;
                        return Task.FromResult(0);
                    }

                    return next(context);
                }

                public class Registration : RegisterStep
                {
                    public Registration() : base("BehaviorThatLogsControlMessageDelivery", typeof(BehaviorThatLogsControlMessageDelivery), "BehaviorThatLogsControlMessageDelivery")
                    {
                    }
                }

                Context testContext;
            }
        }

        public class MyMessage : IMessage
        {
            public Guid Id { get; set; }
        }
    }
}
