﻿using System;

namespace ENode.Domain.Impl
{
    public class SnapshotOnlyAggregateStorage : IAggregateStorage
    {
        private readonly IAggregateSnapshotter _aggregateSnapshotter;

        public SnapshotOnlyAggregateStorage(IAggregateSnapshotter aggregateSnapshotter)
        {
            _aggregateSnapshotter = aggregateSnapshotter;
        }

        public IAggregateRoot Get(Type aggregateRootType, string aggregateRootId)
        {
            if (aggregateRootType == null) throw new ArgumentNullException("aggregateRootType");
            if (aggregateRootId == null) throw new ArgumentNullException("aggregateRootId");

            var aggregateRoot = _aggregateSnapshotter.RestoreFromSnapshot(aggregateRootType, aggregateRootId);

            if (aggregateRoot != null && (aggregateRoot.GetType() != aggregateRootType || aggregateRoot.UniqueId != aggregateRootId))
            {
                throw new Exception(string.Format("AggregateRoot recovery from snapshot is invalid as the aggregateRootType or aggregateRootId is not matched. Snapshot: [aggregateRootType:{0},aggregateRootId:{1}], expected: [aggregateRootType:{2},aggregateRootId:{3}]",
                    aggregateRoot.GetType(),
                    aggregateRoot.UniqueId,
                    aggregateRootType,
                    aggregateRootId));
            }

            return aggregateRoot;
        }
    }
}
