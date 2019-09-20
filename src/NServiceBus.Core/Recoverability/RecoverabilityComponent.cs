﻿namespace NServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DelayedDelivery;
    using DeliveryConstraints;
    using Faults;
    using Hosting;
    using Logging;
    using NServiceBus.ObjectBuilder;
    using Settings;
    using Support;
    using Transport;

    class RecoverabilityComponent
    {
        public RecoverabilityComponent(SettingsHolder settings)
        {
            this.settings = settings;

            settings.SetDefault(NumberOfDelayedRetries, DefaultNumberOfRetries);
            settings.SetDefault(DelayedRetriesTimeIncrease, DefaultTimeIncrease);
            settings.SetDefault(NumberOfImmediateRetries, 5);
            settings.SetDefault(FaultHeaderCustomization, new Action<Dictionary<string, string>>(headers => { }));
            settings.AddUnrecoverableException(typeof(MessageDeserializationException));
        }

        public RecoverabilityExecutorFactory GetRecoverabilityExecutorFactory()
        {
            return recoverabilityExecutorFactory.Value;
        }

        public void Initialize(ReceiveConfiguration receiveConfiguration, ContainerComponent containerComponent)
        {
            if (settings.GetOrDefault<bool>("Endpoint.SendOnly"))
            {
                //Message recoverability is only relevant for endpoints receiving messages.
                return;
            }

            transactionMode = receiveConfiguration.TransactionMode;

            var errorQueue = settings.ErrorQueueAddress();
            settings.Get<QueueBindings>().BindSending(errorQueue);

            var transactionsOn = receiveConfiguration.TransactionMode != TransportTransactionMode.None;

            var delayedRetryConfig = GetDelayedRetryConfig(settings, transactionsOn);


            var immediateRetryConfig = GetImmediateRetryConfig(settings, transactionsOn);

            var failedConfig = new FailedConfig(errorQueue, settings.UnrecoverableExceptions());

            recoverabilityConfig = new RecoverabilityConfig(immediateRetryConfig, delayedRetryConfig, failedConfig);

            settings.AddStartupDiagnosticsSection("Recoverability", new
            {
                ImmediateRetries = recoverabilityConfig.Immediate.MaxNumberOfRetries,
                DelayedRetries = recoverabilityConfig.Delayed.MaxNumberOfRetries,
                DelayedRetriesTimeIncrease = recoverabilityConfig.Delayed.TimeIncrease.ToString("g"),
                recoverabilityConfig.Failed.ErrorQueue,
                UnrecoverableExceptions = recoverabilityConfig.Failed.UnrecoverableExceptionTypes.Select(t => t.FullName).ToArray()
            });

            WireUpLegacyNotifications();

            recoverabilityExecutorFactory = new Lazy<RecoverabilityExecutorFactory>(() => CreateRecoverabilityExecutorFactory(containerComponent.Builder));

            //for backwards compatibility we register the factory in the container
            containerComponent.ContainerConfiguration.ConfigureComponent(_ => GetRecoverabilityExecutorFactory(), DependencyLifecycle.SingleInstance);
        }

        RecoverabilityExecutorFactory CreateRecoverabilityExecutorFactory(IBuilder builder)
        {
            var transactionsOn = transactionMode != TransportTransactionMode.None;

            var delayedRetriesAvailable = transactionsOn
                                         && (settings.DoesTransportSupportConstraint<DelayedDeliveryConstraint>() || settings.Get<TimeoutManagerAddressConfiguration>().TransportAddress != null);

            var immediateRetriesAvailable = transactionsOn;

            Func<string, MoveToErrorsExecutor> moveToErrorsExecutorFactory = localAddress =>
            {
                var hostInfo = builder.Build<HostInformation>();
                var staticFaultMetadata = new Dictionary<string, string>
                    {
                        {FaultsHeaderKeys.FailedQ, localAddress},
                        {Headers.ProcessingMachine, RuntimeEnvironment.MachineName},
                        {Headers.ProcessingEndpoint, settings.EndpointName()},
                        {Headers.HostId, hostInfo.HostId.ToString("N")},
                        {Headers.HostDisplayName, hostInfo.DisplayName}
                    };

                var headerCustomizations = settings.Get<Action<Dictionary<string, string>>>(FaultHeaderCustomization);

                return new MoveToErrorsExecutor(builder.Build<IDispatchMessages>(), staticFaultMetadata, headerCustomizations);
            };

            Func<string, DelayedRetryExecutor> delayedRetryExecutorFactory = localAddress =>
            {
                if (delayedRetriesAvailable)
                {
                    return new DelayedRetryExecutor(
                        localAddress,
                        builder.Build<IDispatchMessages>(),
                        settings.DoesTransportSupportConstraint<DelayedDeliveryConstraint>()
                            ? null
                            : settings.Get<TimeoutManagerAddressConfiguration>().TransportAddress);
                }

                return null;
            };

            if (!settings.TryGet(PolicyOverride, out Func<RecoverabilityConfig, ErrorContext, RecoverabilityAction> policy))
            {
                policy = DefaultRecoverabilityPolicy.Invoke;
            }

            return new RecoverabilityExecutorFactory(
                policy,
                recoverabilityConfig,
                delayedRetryExecutorFactory,
                moveToErrorsExecutorFactory,
                immediateRetriesAvailable,
                delayedRetriesAvailable);

        }

        static ImmediateConfig GetImmediateRetryConfig(ReadOnlySettings settings, bool transactionsOn)
        {
            if (!transactionsOn)
            {
                Logger.Warn("Immediate Retries will be disabled. Immediate Retries are not supported when running with TransportTransactionMode.None. Failed messages will be moved to the error queue instead.");
                //Transactions must be enabled since Immediate Retries requires the transport to be able to rollback
                return new ImmediateConfig(0);
            }

            var maxImmediateRetries = settings.Get<int>(NumberOfImmediateRetries);

            return new ImmediateConfig(maxImmediateRetries);
        }

        static DelayedConfig GetDelayedRetryConfig(ReadOnlySettings settings, bool transactionsOn)
        {
            if (!transactionsOn)
            {
                Logger.Warn("Delayed Retries will be disabled. Delayed retries are not supported when running with TransportTransactionMode.None. Failed messages will be moved to the error queue instead.");
                //Transactions must be enabled since Delayed Retries requires the transport to be able to rollback
                return new DelayedConfig(0, TimeSpan.Zero);
            }

            var numberOfRetries = settings.Get<int>(NumberOfDelayedRetries);
            var timeIncrease = settings.Get<TimeSpan>(DelayedRetriesTimeIncrease);

            return new DelayedConfig(numberOfRetries, timeIncrease);
        }

        //note: will soon be removed since we're deprecating Notifications in favor of the new notifications
        void WireUpLegacyNotifications()
        {
            var legacyNotifications = settings.Get<Notifications>();
            var notifications = settings.Get<NotificationSubscriptions>();

            notifications.Subscribe<MessageToBeRetried>(e =>
            {
                if (e.IsImmediateRetry)
                {
                    legacyNotifications.Errors.InvokeMessageHasFailedAnImmediateRetryAttempt(e.Attempt, e.Message, e.Exception);
                }
                else
                {
                    legacyNotifications.Errors.InvokeMessageHasBeenSentToDelayedRetries(e.Attempt, e.Message, e.Exception);
                }

                return TaskEx.CompletedTask;
            });

            notifications.Subscribe<MessageFaulted>(e =>
            {
                legacyNotifications.Errors.InvokeMessageHasBeenSentToErrorQueue(e.Message, e.Exception, e.ErrorQueue);
                return TaskEx.CompletedTask;
            });
        }

        public const string NumberOfDelayedRetries = "Recoverability.Delayed.DefaultPolicy.Retries";
        public const string DelayedRetriesTimeIncrease = "Recoverability.Delayed.DefaultPolicy.Timespan";
        public const string NumberOfImmediateRetries = "Recoverability.Immediate.Retries";
        public const string FaultHeaderCustomization = "Recoverability.Failed.FaultHeaderCustomization";
        public const string PolicyOverride = "Recoverability.CustomPolicy";
        public const string UnrecoverableExceptions = "Recoverability.UnrecoverableExceptions";

        internal static int DefaultNumberOfRetries = 3;
        internal static TimeSpan DefaultTimeIncrease = TimeSpan.FromSeconds(10);

        SettingsHolder settings;
        TransportTransactionMode transactionMode;
        RecoverabilityConfig recoverabilityConfig;
        Lazy<RecoverabilityExecutorFactory> recoverabilityExecutorFactory;

        static ILog Logger = LogManager.GetLogger<RecoverabilityComponent>();
    }
}