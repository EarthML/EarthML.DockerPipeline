using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Client;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.ServiceFabric.Services.Remoting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EarthML.Pipelines.ServiceProvider.Actors
{
    public static class TaskHelper
    {
        public static void FireAndForget(this Task task)
        {
            Task.Run(async () => await task).ConfigureAwait(false);
        }
    }

    public interface IDocumentActor : IActor
    {
        Task InitializeAsync();
        Task DocumentUpdatedAsync();
    }

    [DataContract]
    [KnownType(typeof(JToken))]
    [KnownType(typeof(JArray))]
    [KnownType(typeof(JObject))]
    public class DocumentWrapper
    {
        [DataMember]
        public Object Document { get; set; }
    }

    public interface IActorBaseService : IService, IActorService
    {
        Task<bool> ActorExists(ActorId actorId, CancellationToken cancellationToken);
        Task ActorUpdatedAsync(ActorId actorid, DateTimeOffset time, bool initialzieOnMissing, CancellationToken cancellationToken);
        Task DeactivateAsync(ActorId id);
    }
    public interface IDocumentActorBaseService : IActorBaseService, IService, IActorService
    {
        Task SaveDocumentAsync(ActorId actorId, DocumentWrapper document, CancellationToken cancellationToken);
        Task<object> GetDocumentAsync(ActorId actorId, CancellationToken requestAborted);
    }
    public static class Constants
    {
        public const string ActivatedStateName = "Activated";
        public const string LastUpdatedStateName = "LastUpdated";
    }
    public class ActorBaseService<TDocument> : ActorService, IDocumentActorBaseService
    {

        public ActorBaseService(
            StatefulServiceContext context,
            ActorTypeInformation actorTypeInfo,
            Func<ActorService, ActorId, ActorBase> actorFactory,
            Func<ActorBase, IActorStateProvider, IActorStateManager> stateManagerFactory = null,
            IActorStateProvider stateProvider = null,
            ActorServiceSettings settings = null) : base(context, actorTypeInfo, actorFactory, stateManagerFactory, stateProvider, settings)
        {

        }
        public async Task<bool> ActorExists(ActorId actorId, CancellationToken cancellationToken)
        {
            try
            {
                return await StateProvider.ContainsStateAsync(actorId, Constants.ActivatedStateName, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        public async Task DeactivateAsync(ActorId actorId)
        {
            try
            {
                await StateProvider.SaveStateAsync(actorId, new[] { new ActorStateChange(Constants.ActivatedStateName, typeof(bool), false, StateChangeKind.Update) });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }




        public async Task ActorUpdatedAsync(ActorId actorid, DateTimeOffset time, bool initializeOnMissing, CancellationToken cancellationToken)
        {
            try
            {
                if (await StateProvider.ContainsStateAsync(actorid, Constants.ActivatedStateName, cancellationToken))
                {
                    if (await StateProvider.ContainsStateAsync(actorid, Constants.LastUpdatedStateName, cancellationToken))
                    {
                        var old = await StateProvider.LoadStateAsync<DateTimeOffset>(actorid, Constants.LastUpdatedStateName, cancellationToken);

                        if (time > old)
                        {
                            await StateProvider.SaveStateAsync(actorid,
                                 new ActorStateChange[] {
                            new ActorStateChange(Constants.LastUpdatedStateName, typeof(DateTimeOffset), time, StateChangeKind.Update)
                             }, cancellationToken);
                        }
                        else
                        {
                            return;
                        }
                    }
                    else
                    {

                        await StateProvider.SaveStateAsync(actorid,
                             new ActorStateChange[] {
                         new ActorStateChange(Constants.LastUpdatedStateName, typeof(DateTimeOffset), time, StateChangeKind.Add)
                         }, cancellationToken);


                    }

                    if (!await StateProvider.LoadStateAsync<bool>(actorid, Constants.ActivatedStateName, cancellationToken))
                    {
                        ActorProxy.Create<IDocumentActor>(actorid, this.Context.ServiceName).DocumentUpdatedAsync().FireAndForget();
                    }
                }
                else if (initializeOnMissing)
                {
                    ActorProxy.Create<IDocumentActor>(actorid, this.Context.ServiceName).InitializeAsync().FireAndForget();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        public async Task SaveDocumentAsync(ActorId actorId, DocumentWrapper document, CancellationToken cancellationToken)
        {


            await StateProvider.SaveStateAsync(actorId,
                    new ActorStateChange[] {
                         new ActorStateChange("Document", typeof(TDocument), document.Document,
                         await StateProvider.ContainsStateAsync(actorId, "Document", cancellationToken) ? StateChangeKind.Update: StateChangeKind.Add)
                }, cancellationToken);

            await ActorUpdatedAsync(actorId, DateTimeOffset.UtcNow, true, cancellationToken);

        }

        public async Task<object> GetDocumentAsync(ActorId actorId, CancellationToken requestAborted)
        {
            if (await StateProvider.ContainsStateAsync(actorId, "Document", requestAborted))
                return await StateProvider.LoadStateAsync<TDocument>(actorId, "Document", requestAborted);

            return null;
        }
    }

    [Serializable]
    [DataContract()]
    public class PipelineRunnerActorDocument
    {
        [DataMember]
        public Guid Id { get; set; }

        [DataMember]
        public byte[] Data { get; set; }

    }

    public interface IPipelineRunnerActorService : IDocumentActorBaseService, IService, IActorService
    {
        Task<PipelineRunnerActorDocument> CreateNewSiteRegistrationAsync(ActorId id, byte[] model, CancellationToken token);
    }

    public class PipelineRunnerActorService : ActorBaseService<PipelineRunnerActorDocument>, IPipelineRunnerActorService
    {
        public PipelineRunnerActorService(
            StatefulServiceContext context, ActorTypeInformation actorTypeInfo,
            Func<ActorService, ActorId, ActorBase> actorFactory, Func<ActorBase, IActorStateProvider,
            IActorStateManager> stateManagerFactory = null,
            IActorStateProvider stateProvider = null)
            : base(context, actorTypeInfo, actorFactory, stateManagerFactory, stateProvider, new ActorServiceSettings
            {
                ActorGarbageCollectionSettings = new ActorGarbageCollectionSettings(300, 60)
                {
                }
            })
        {

        }

        public async Task<PipelineRunnerActorDocument> CreateNewSiteRegistrationAsync(ActorId id, byte[] model, CancellationToken token)
        {


            var doc = new PipelineRunnerActorDocument { Id = id.GetGuidId(), Data = model };
            await SaveDocumentAsync(id, new DocumentWrapper { Document = doc }, token);
            return doc;
        }
    }
    public abstract class DocumentActor<T> : Actor
    {
        public const string DocumentStateKey = "Document";
        protected readonly ILogger _logger;

        protected DocumentActor(ActorService actorService, ActorId actorId, ILogger logger) : base(actorService, actorId)
        {
            this._logger = logger;
        }
        public Task<T> DocumentAsync => StateManager.GetStateAsync<T>(DocumentStateKey);
        public Task<bool> HasDocumentAsync => StateManager.ContainsStateAsync(DocumentStateKey);
        public Task SetDocumentAsync(T document) => StateManager.SetStateAsync(DocumentStateKey, document).ContinueWith(c => LastUpdated = DateTimeOffset.UtcNow);

        public virtual Task DocumentUpdatedAsync()
        {
            return Task.CompletedTask;
        }

        public virtual Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        private IActorTimer _updateTimer;
        protected DateTimeOffset? LastUpdated { get; set; }


        protected DateTimeOffset _lastChecked = DateTimeOffset.MinValue;
        protected Task _longRunningOnUpdated = null;
        protected int attempts = 0;


        protected override async Task OnActivateAsync()
        {
            await StateManager.SetStateAsync(Constants.ActivatedStateName, true);
            await StateManager.SetStateAsync(Constants.LastUpdatedStateName, DateTimeOffset.UtcNow);


            _updateTimer = RegisterTimer(
             OnUpdateCheckAsync,                     // Callback method
             null,                           // Parameter to pass to the callback method
             TimeSpan.FromMinutes(0),  // Amount of time to delay before the callback is invoked
             TimeSpan.FromSeconds(10)); // Time interval between invocations of the callback method
        }

        private async Task OnUpdateCheckAsync(object arg)
        {

            var updatedAt = await StateManager.GetStateAsync<DateTimeOffset>(Constants.LastUpdatedStateName);

            if (_longRunningOnUpdated == null)
            {

                if (updatedAt > _lastChecked)
                {

                    if (attempts++ > 0)
                    {
                        _logger.LogInformation("Running OnUpdated for {actorId} {attempt} for {updatedAt}", Id.ToString(), attempts, updatedAt);
                    }

                    _longRunningOnUpdated = Task.Run(async () => { await OnUpdatedAsync(); _lastChecked = updatedAt; attempts = 0; });


                }
            }
            else if (_longRunningOnUpdated.Status == TaskStatus.RanToCompletion)
            {
                _logger.LogDebug("OnUpdated for {actorId} ran to completion for {attempt} in {time}", Id.ToString(), attempts, DateTimeOffset.UtcNow.Subtract(updatedAt));

                _longRunningOnUpdated = null;
            }
            else if (_longRunningOnUpdated.Status == TaskStatus.Faulted)
            {
                _logger.LogInformation(_longRunningOnUpdated.Exception, "OnUpdated for {actorId} faulted in {time} and will reset", Id.ToString(), DateTimeOffset.UtcNow.Subtract(updatedAt));
                _longRunningOnUpdated = null;
                if (attempts > 1)
                {
                    _lastChecked = updatedAt;
                }
            }
            else if (_longRunningOnUpdated.Status == TaskStatus.Canceled)
            {
                _logger.LogInformation("OnUpdated for {actorId} was canceled in {time}", Id.ToString(), DateTimeOffset.UtcNow.Subtract(updatedAt));
                _longRunningOnUpdated = null;
            }
            else
            {
                await ActorProxy.Create<IDocumentActor>(this.Id, this.ServiceUri).DocumentUpdatedAsync();
            }

        }
        protected virtual Task OnUpdatedAsync()
        {
            return Task.CompletedTask;
        }
        protected override async Task OnDeactivateAsync()
        {
            if (_updateTimer != null)
            {
                UnregisterTimer(_updateTimer);
            }

            await ActorServiceProxy.Create<IActorBaseService>(ServiceUri, Id).DeactivateAsync(Id);

            await base.OnDeactivateAsync();
        }
    }

    public interface IPipelineRunnerActor : IDocumentActor, IActor
    {

    }
    /// <remarks>
    /// This class represents an actor.
    /// Every ActorID maps to an instance of this class.
    /// The StatePersistence attribute determines persistence and replication of actor state:
    ///  - Persisted: State is written to disk and replicated.
    ///  - Volatile: State is kept in memory only and replicated.
    ///  - None: State is kept in memory only and not replicated.
    /// </remarks>
    [DataContract()]
    [StatePersistence(StatePersistence.Persisted)]
    [ActorService(Name = "PipelineRunnerActorService")]
    public class PipelineRunnerActor : DocumentActor<PipelineRunnerActorDocument>, IPipelineRunnerActor
    {
        public PipelineRunnerActor(ActorService actorService, ActorId actorId, ILogger logger) : base(actorService, actorId,logger)
        {
        }


        protected override async Task OnUpdatedAsync()
        {
            


        }


    }

}
