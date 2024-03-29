﻿using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Client.App.Model;
using Serilog;

namespace Client.App.Services
{
    public class MeshController
    {
        private readonly ILogger _logger;
        private readonly ObservableMeshClient _observableMeshClient;
        private readonly SchedulerProvider _schedulerProvider;

        public MeshController(ILogger logger, SchedulerProvider schedulerProvider,
            ObservableMeshClient observableMeshClient)
        {
            _logger = logger;
            _schedulerProvider = schedulerProvider;
            _observableMeshClient = observableMeshClient;
        }

        public (IObservable<Unit> request, IObservable<bool> ready, Func<(IObservable<Unit> request, IObservable<Unit> complete)> stop) Start(string applicationResourceName,
            string imageRegistryServer,
            string imageRegistryUsername, string imageRegistryPassword, string imageName, string azurePipelinesUrl,
            string azurePipelinesToken, string resourceGroupName,
            bool outputContainerLogs = true, bool outputComponentStatus = true)
        {
            var autoResetEvent = new AutoResetEvent(false);
            var disposable = new CompositeDisposable();
            var readySubject = new Subject<bool>();
            var request = Observable.DeferAsync(async token =>
            {
                _logger.Information("Starting Mesh {Mesh}", applicationResourceName);

                await _observableMeshClient.CreateOrEditMesh(applicationResourceName, imageRegistryServer,
                    imageRegistryUsername, imageRegistryPassword, imageName, azurePipelinesUrl, azurePipelinesToken,
                    resourceGroupName);

                var startPollingSubject = new Subject<Unit>();
                var startPollingObservable = startPollingSubject.AsObservable();

                var applicationFailedSubject = new Subject<Unit>();
                var applicationFailedObservable = applicationFailedSubject.AsObservable();

                var pollApplicationStatus = _observableMeshClient
                    .PollApplicationStatus(applicationResourceName, resourceGroupName)
                    .Replay();

                var pollServiceStatus = _observableMeshClient
                    .PollServiceStatus(applicationResourceName, resourceGroupName, startPollingObservable, applicationFailedObservable)
                    .Replay();

                var pollAgentStatus = _observableMeshClient
                    .PollAgentStatus(applicationResourceName, resourceGroupName, startPollingObservable, applicationFailedObservable, 0, outputContainerLogs)
                    .Replay();

                var pollReplicaStateSubscription = pollAgentStatus
                    .SubscribeOn(_schedulerProvider.TaskPool)
                    .Subscribe(agentStateEnum => { },
                        exception => { _logger.Error("Container Logs Error: {Message}", exception.Message); });

                var applicationStateObservable = pollApplicationStatus
                    .SubscribeOn(_schedulerProvider.TaskPool)
                    .Subscribe(applicationStatusEnum => { },
                        exception =>
                        {
                            _logger.Error("Application Error: {Message}", exception.Message);
                        });

                var serviceDataSubscription = pollServiceStatus
                    .SubscribeOn(_schedulerProvider.TaskPool)
                    .Subscribe(serviceStatusEnum => { },
                        exception =>
                        {
                            _logger.Error("Service Error: {Message}", exception.Message);
                        });

                var combinedSubscription = pollApplicationStatus.CombineLatest(pollServiceStatus, pollAgentStatus,
                        (applicationStatus, serviceStatus, agentStatus) =>
                            (applicationStatus, serviceStatus, agentStatus))
                    .DistinctUntilChanged()
                    .SubscribeOn(_schedulerProvider.TaskPool)
                    .Subscribe(tuple =>
                        {
                            var (applicationStatus, serviceStatus, agentStatus) = tuple;

                            if (outputComponentStatus)
                                _logger.Information(
                                    "Mesh {Mesh} Application {Application,8} Service {Service,8} Agent {Agent,8}",
                                    applicationResourceName, applicationStatus, serviceStatus, agentStatus);

                            if (applicationStatus == ApplicationStatusEnum.Ready &&
                                serviceStatus == ServiceStatusEnum.Ready && 
                                agentStatus == AgentStatusEnum.Ready)
                            {
                                readySubject.OnNext(true);
                                readySubject.OnCompleted();
                                return;
                            }

                            if (applicationStatus == ApplicationStatusEnum.Failed &&
                                serviceStatus == ServiceStatusEnum.Failed && 
                                agentStatus == AgentStatusEnum.Failed)
                            {
                                readySubject.OnNext(false);
                                readySubject.OnCompleted();
                                return;
                            }

                            if (applicationStatus == ApplicationStatusEnum.Failed)
                            {
                                applicationFailedSubject.OnNext(Unit.Default);
                                applicationFailedSubject.OnCompleted();
                                return;
                            }

                            if (applicationStatus == ApplicationStatusEnum.Creating)
                            {
                                startPollingSubject.OnNext(Unit.Default);
                                startPollingSubject.OnCompleted();
                                return;
                            }
                        },
                        () =>
                        {
                            _logger.Debug("Streams completed");
                            autoResetEvent.Set();
                        });

                pollApplicationStatus.Connect();
                pollServiceStatus.Connect();
                pollAgentStatus.Connect();

                disposable.Add(pollReplicaStateSubscription);
                disposable.Add(applicationStateObservable);
                disposable.Add(serviceDataSubscription);
                disposable.Add(combinedSubscription);
                disposable.Add(readySubject);
                disposable.Add(startPollingSubject);
                disposable.Add(applicationFailedSubject);

                return Observable.Return(Unit.Default);
            }).SubscribeOn(_schedulerProvider.TaskPool);

            Func<(IObservable<Unit> request, IObservable<Unit> complete)> stop = () =>
            {
                var completeObservable = Observable.Defer(() =>
                {
                    autoResetEvent.WaitOne();

                    _logger.Information("Stopped");

                    disposable.Dispose();
                    disposable = null;

                    return Observable.Return(Unit.Default);
                }).SubscribeOn(_schedulerProvider.TaskPool);
                ;

                var stopRequest = Observable.DeferAsync(async token =>
                {
                    _logger.Information("Stopping");

                    await _observableMeshClient.DeleteMesh(applicationResourceName, resourceGroupName);

                    return Observable.Return(Unit.Default);
                }).SubscribeOn(_schedulerProvider.TaskPool);

                return (stopRequest, completeObservable);
            };

            var ready = readySubject.AsObservable();
            return (request, ready, stop);
        }

        public IObservable<string> Upscale(string applicationResourceName,
            string imageRegistryServer,
            string imageRegistryUsername, string imageRegistryPassword, string imageName, string azurePipelinesUrl,
            string azurePipelinesToken, string resourceGroupName, int replicaCount)
        {
            return _observableMeshClient.CreateOrEditMesh(applicationResourceName, imageRegistryServer,
                imageRegistryUsername, imageRegistryPassword, imageName, azurePipelinesUrl, azurePipelinesToken,
                resourceGroupName, replicaCount);
        }
    }
}