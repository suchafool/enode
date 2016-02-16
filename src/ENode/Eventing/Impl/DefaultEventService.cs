﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ECommon.IO;
using ECommon.Logging;
using ECommon.Scheduling;
using ECommon.Serializing;
using ENode.Commanding;
using ENode.Configurations;
using ENode.Domain;
using ENode.Infrastructure;

namespace ENode.Eventing.Impl
{
    public class DefaultEventService : IEventService
    {
        #region Private Variables

        private IProcessingMessageHandler<ProcessingCommand, ICommand, CommandResult> _processingCommandHandler;
        private readonly IList<PersistEventWorker> _persistEventWorkerList;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IScheduleService _scheduleService;
        private readonly ITypeNameProvider _typeNameProvider;
        private readonly IMemoryCache _memoryCache;
        private readonly IAggregateRootFactory _aggregateRootFactory;
        private readonly IAggregateStorage _aggregateStorage;
        private readonly IEventStore _eventStore;
        private readonly IMessagePublisher<DomainEventStreamMessage> _domainEventPublisher;
        private readonly IOHelper _ioHelper;
        private readonly ILogger _logger;

        #endregion

        #region Constructors

        public DefaultEventService(
            IJsonSerializer jsonSerializer,
            IScheduleService scheduleService,
            ITypeNameProvider typeNameProvider,
            IMemoryCache memoryCache,
            IAggregateRootFactory aggregateRootFactory,
            IAggregateStorage aggregateStorage,
            IEventStore eventStore,
            IMessagePublisher<DomainEventStreamMessage> domainEventPublisher,
            IOHelper ioHelper,
            ILoggerFactory loggerFactory)
        {
            _persistEventWorkerList = new List<PersistEventWorker>();
            _ioHelper = ioHelper;
            _jsonSerializer = jsonSerializer;
            _scheduleService = scheduleService;
            _typeNameProvider = typeNameProvider;
            _memoryCache = memoryCache;
            _aggregateRootFactory = aggregateRootFactory;
            _aggregateStorage = aggregateStorage;
            _eventStore = eventStore;
            _domainEventPublisher = domainEventPublisher;
            _logger = loggerFactory.Create(GetType().FullName);

            for (var i = 0; i < ENodeConfiguration.Instance.Setting.EventPersistQueueCount; i++)
            {
                var worker = new PersistEventWorker(new ConcurrentQueue<EventCommittingContext>(), context => CommitEventAsync(context, 0));
                _persistEventWorkerList.Add(worker);
            }
        }

        #endregion

        #region Public Methods

        public void SetProcessingCommandHandler(IProcessingMessageHandler<ProcessingCommand, ICommand, CommandResult> processingCommandHandler)
        {
            _processingCommandHandler = processingCommandHandler;
        }
        public void CommitDomainEventAsync(EventCommittingContext context)
        {
            int queueIndex = GetPersistQueueIndex(context.AggregateRoot.UniqueId);
            var worker = _persistEventWorkerList[queueIndex];
            worker.EnqueueMessage(context);
            worker.TryCommitNextEvent();
        }
        public void PublishDomainEventAsync(ProcessingCommand processingCommand, DomainEventStream eventStream, bool tryCommitNextEvent = true)
        {
            if (eventStream.Items == null || eventStream.Items.Count == 0)
            {
                eventStream.Items = processingCommand.Items;
            }
            var eventStreamMessage = new DomainEventStreamMessage(processingCommand.Message.Id, eventStream.AggregateRootId, eventStream.Version, eventStream.AggregateRootTypeName, eventStream.Events, eventStream.Items);
            PublishDomainEventAsync(processingCommand, eventStreamMessage, 0, tryCommitNextEvent);
        }

        #endregion

        #region Private Methods

        private int GetPersistQueueIndex(string aggregateRootId)
        {
            int hash = 23;
            foreach (char c in aggregateRootId)
            {
                hash = (hash << 5) - hash + c;
            }
            if (hash < 0)
            {
                hash = Math.Abs(hash);
            }
            return hash % _persistEventWorkerList.Count;
        }
        private void CommitEventAsync(EventCommittingContext context, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult<EventAppendResult>>("PersistEventAsync",
            () => _eventStore.AppendAsync(context.EventStream),
            currentRetryTimes => CommitEventAsync(context, currentRetryTimes),
            result =>
            {
                if (result.Data == EventAppendResult.Success)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.DebugFormat("Persist events success, {0}", context.EventStream);
                    }
                    RefreshAggregateMemoryCache(context);
                    PublishDomainEventAsync(context.ProcessingCommand, context.EventStream);
                }
                else if (result.Data == EventAppendResult.DuplicateEvent)
                {
                    HandleDuplicateEventResult(context);
                }
                else if (result.Data == EventAppendResult.DuplicateCommand)
                {
                    HandleDuplicateCommandResult(context, 0);
                }
            },
            () => string.Format("[eventStream:{0}]", context.EventStream),
            errorMessage => NotifyCommandExecuted(context.ProcessingCommand, new CommandResult(CommandStatus.Failed, context.ProcessingCommand.Message.Id, context.EventStream.AggregateRootId, errorMessage ?? "Persist event async failed.", typeof(string).FullName)),
            retryTimes);
        }
        private void HandleDuplicateCommandResult(EventCommittingContext context, int retryTimes)
        {
            var command = context.ProcessingCommand.Message;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult<DomainEventStream>>("FindEventStreamByCommandIdAsync",
            () => _eventStore.FindAsync(command.AggregateRootId, command.Id),
            currentRetryTimes => HandleDuplicateCommandResult(context, currentRetryTimes),
            result =>
            {
                var existingEventStream = result.Data;
                if (existingEventStream != null)
                {
                    //这里，我们需要再重新做一遍更新内存缓存以及发布事件这两个操作；
                    //之所以要这样做是因为虽然该command产生的事件已经持久化成功，但并不表示已经内存也更新了或者事件已经发布出去了；
                    //因为有可能事件持久化成功了，但那时正好机器断电了，则更新内存和发布事件都没有做；
                    RefreshAggregateMemoryCache(existingEventStream);
                    PublishDomainEventAsync(context.ProcessingCommand, existingEventStream);
                }
                else
                {
                    //到这里，说明当前command想添加到eventStore中时，提示command重复，但是尝试从eventStore中取出该command时却找不到该command。
                    //出现这种情况，我们就无法再做后续处理了，这种错误理论上不会出现，除非eventStore的Add接口和Get接口出现读写不一致的情况；
                    //我们记录错误日志，然后认为当前command已被处理为失败。
                    var errorMessage = string.Format("Command exist in the event store, but we cannot get it from the event store. commandType:{0}, commandId:{1}, aggregateRootId:{2}",
                        command.GetType().Name,
                        command.Id,
                        command.AggregateRootId);
                    _logger.Error(errorMessage);
                    NotifyCommandExecuted(context.ProcessingCommand, new CommandResult(CommandStatus.Failed, command.Id, command.AggregateRootId, "Duplicate command execution.", typeof(string).FullName));
                }
            },
            () => string.Format("[aggregateRootId:{0}, commandId:{1}]", command.AggregateRootId, command.Id),
            errorMessage => NotifyCommandExecuted(context.ProcessingCommand, new CommandResult(CommandStatus.Failed, command.Id, command.AggregateRootId, "Find event stream by commandId failed.", typeof(string).FullName)),
            retryTimes);
        }
        private void HandleDuplicateEventResult(EventCommittingContext context)
        {
            //如果是当前事件的版本号为1，则认为是在创建重复的聚合根
            if (context.EventStream.Version == 1)
            {
                HandleFirstEventDuplicationAsync(context, 0);
            }
            //如果事件的版本大于1，则认为是更新聚合根时遇到并发冲突了；
            //那么我们需要先将聚合根的最新状态更新到内存，然后重试command；
            else
            {
                UpdateAggregateToLatestVersion(context.EventStream.AggregateRootTypeName, context.EventStream.AggregateRootId);
                RetryConcurrentCommand(context);
            }
        }
        private void HandleFirstEventDuplicationAsync(EventCommittingContext context, int retryTimes)
        {
            var eventStream = context.EventStream;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult<DomainEventStream>>("FindFirstEventByVersion",
            () => _eventStore.FindAsync(eventStream.AggregateRootId, 1),
            currentRetryTimes => HandleFirstEventDuplicationAsync(context, currentRetryTimes),
            result =>
            {
                var firstEventStream = result.Data;
                if (firstEventStream != null)
                {
                    //判断是否是同一个command，如果是，则再重新做一遍更新内存缓存以及发布事件这两个操作；
                    //之所以要这样做，是因为虽然该command产生的事件已经持久化成功，但并不表示已经内存也更新了或者事件已经发布出去了；
                    //有可能事件持久化成功了，但那时正好机器断电了，则更新内存和发布事件都没有做；
                    if (context.ProcessingCommand.Message.Id == firstEventStream.CommandId)
                    {
                        RefreshAggregateMemoryCache(firstEventStream);
                        PublishDomainEventAsync(context.ProcessingCommand, firstEventStream);
                    }
                    else
                    {
                        //如果不是同一个command，则认为是两个不同的command重复创建ID相同的聚合根，我们需要记录错误日志，然后通知当前command的处理完成；
                        var errorMessage = string.Format("Duplicate aggregate creation. current commandId:{0}, existing commandId:{1}, aggregateRootId:{2}, aggregateRootTypeName:{3}",
                            context.ProcessingCommand.Message.Id,
                            eventStream.CommandId,
                            eventStream.AggregateRootId,
                            eventStream.AggregateRootTypeName);
                        _logger.Error(errorMessage);
                        NotifyCommandExecuted(context.ProcessingCommand, new CommandResult(CommandStatus.Failed, context.ProcessingCommand.Message.Id, eventStream.AggregateRootId, "Duplicate aggregate creation.", typeof(string).FullName));
                    }
                }
                else
                {
                    var errorMessage = string.Format("Duplicate aggregate creation, but we cannot find the existing eventstream from eventstore. commandId:{0}, aggregateRootId:{1}, aggregateRootTypeName:{2}",
                        eventStream.CommandId,
                        eventStream.AggregateRootId,
                        eventStream.AggregateRootTypeName);
                    _logger.Error(errorMessage);
                    NotifyCommandExecuted(context.ProcessingCommand, new CommandResult(CommandStatus.Failed, context.ProcessingCommand.Message.Id, eventStream.AggregateRootId, "Duplicate aggregate creation, but we cannot find the existing eventstream from eventstore.", typeof(string).FullName));
                }
            },
            () => string.Format("[eventStream:{0}]", eventStream),
            errorMessage => NotifyCommandExecuted(context.ProcessingCommand, new CommandResult(CommandStatus.Failed, context.ProcessingCommand.Message.Id, eventStream.AggregateRootId, errorMessage ?? "Persist the first version of event duplicated, but try to get the first version of domain event async failed.", typeof(string).FullName)),
            retryTimes);
        }
        private void RefreshAggregateMemoryCache(DomainEventStream aggregateFirstEventStream)
        {
            try
            {
                var aggregateRootType = _typeNameProvider.GetType(aggregateFirstEventStream.AggregateRootTypeName);
                var aggregateRoot = _memoryCache.Get(aggregateFirstEventStream.AggregateRootId, aggregateRootType);
                if (aggregateRoot == null)
                {
                    aggregateRoot = _aggregateRootFactory.CreateAggregateRoot(aggregateRootType);
                    aggregateRoot.ReplayEvents(new DomainEventStream[] { aggregateFirstEventStream });
                    _memoryCache.Set(aggregateRoot);
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.DebugFormat("Aggregate added into memory, commandId:{0}, aggregateRootType:{1}, aggregateRootId:{2}, aggregateRootVersion:{3}", aggregateFirstEventStream.CommandId, aggregateRootType.Name, aggregateRoot.UniqueId, aggregateRoot.Version);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Refresh memory cache by aggregate first event stream failed, {0}", aggregateFirstEventStream), ex);
            }
        }
        private void RefreshAggregateMemoryCache(EventCommittingContext context)
        {
            try
            {
                context.AggregateRoot.AcceptChanges(context.EventStream.Version);
                _memoryCache.Set(context.AggregateRoot);
                if (_logger.IsDebugEnabled)
                {
                    _logger.DebugFormat("Refreshed aggregate memory cache, commandId:{0}, aggregateRootType:{1}, aggregateRootId:{2}, aggregateRootVersion:{3}", context.EventStream.CommandId, context.AggregateRoot.GetType().Name, context.AggregateRoot.UniqueId, context.AggregateRoot.Version);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Refresh memory cache failed by event stream:{0}", context.EventStream), ex);
            }
        }
        private void UpdateAggregateToLatestVersion(string aggregateRootTypeName, string aggregateRootId)
        {
            _memoryCache.RefreshAggregateFromEventStore(aggregateRootTypeName, aggregateRootId);
        }
        private void RetryConcurrentCommand(EventCommittingContext context)
        {
            var processingCommand = context.ProcessingCommand;
            var command = processingCommand.Message;
            processingCommand.IncreaseConcurrentRetriedCount();
            processingCommand.CommandExecuteContext.Clear();
            _logger.InfoFormat("Begin to retry command as it meets the concurrent conflict. commandType:{0}, commandId:{1}, aggregateRootId:{2}, retried count:{3}.", command.GetType().Name, command.Id, processingCommand.Message.AggregateRootId, processingCommand.ConcurrentRetriedCount);
            _processingCommandHandler.HandleAsync(processingCommand);
        }
        private void PublishDomainEventAsync(ProcessingCommand processingCommand, DomainEventStreamMessage eventStream, int retryTimes, bool tryCommitNextEvent = true)
        {
            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult>("PublishDomainEventAsync",
            () => _domainEventPublisher.PublishAsync(eventStream),
            currentRetryTimes => PublishDomainEventAsync(processingCommand, eventStream, currentRetryTimes, tryCommitNextEvent),
            result =>
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.DebugFormat("Publish domain events success, {0}", eventStream);
                }
                var commandHandleResult = processingCommand.CommandExecuteContext.GetResult();
                NotifyCommandExecuted(processingCommand, new CommandResult(CommandStatus.Success, processingCommand.Message.Id, eventStream.AggregateRootId, commandHandleResult, typeof(string).FullName), tryCommitNextEvent);
            },
            () => string.Format("[eventStream:{0}]", eventStream),
            errorMessage => NotifyCommandExecuted(processingCommand, new CommandResult(CommandStatus.Failed, processingCommand.Message.Id, eventStream.AggregateRootId, errorMessage ?? "Publish domain event async failed.", typeof(string).FullName), tryCommitNextEvent),
            retryTimes);
        }
        private void NotifyCommandExecuted(ProcessingCommand processingCommand, CommandResult commandResult, bool tryCommitNextEvent = true)
        {
            processingCommand.Complete(commandResult);
            if (tryCommitNextEvent)
            {
                int queueIndex = GetPersistQueueIndex(processingCommand.Message.AggregateRootId);
                var worker = _persistEventWorkerList[queueIndex];
                worker.ExitHandlingMessage();
                worker.TryCommitNextEvent();
            }
        }

        class PersistEventWorker
        {
            private ConcurrentQueue<EventCommittingContext> _queue;
            private Action<EventCommittingContext> _commitAction;
            private int _isHandlingMessage;

            public PersistEventWorker(ConcurrentQueue<EventCommittingContext> queue, Action<EventCommittingContext> commitAction)
            {
                _queue = queue;
                _commitAction = commitAction;
            }

            public void EnqueueMessage(EventCommittingContext message)
            {
                _queue.Enqueue(message);
            }
            public void TryCommitNextEvent()
            {
                if (EnterHandlingMessage())
                {
                    EventCommittingContext context = null;
                    try
                    {
                        if (_queue.TryDequeue(out context))
                        {
                            _commitAction(context);
                        }
                    }
                    finally
                    {
                        if (context == null)
                        {
                            ExitHandlingMessage();
                            if (!_queue.IsEmpty)
                            {
                                TryCommitNextEvent();
                            }
                        }
                    }
                }
            }
            public bool EnterHandlingMessage()
            {
                return Interlocked.CompareExchange(ref _isHandlingMessage, 1, 0) == 0;
            }
            public void ExitHandlingMessage()
            {
                Interlocked.Exchange(ref _isHandlingMessage, 0);
            }
        }

        #endregion
    }
}
