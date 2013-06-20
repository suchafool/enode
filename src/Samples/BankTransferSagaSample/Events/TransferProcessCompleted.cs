﻿using System;
using BankTransferSagaSample.Domain;

namespace BankTransferSagaSample.Events
{
    [Serializable]
    public class TransferProcessCompleted : AbstractTransferEvent
    {
        public TransferProcessCompleted(Guid processId, TransferInfo transferInfo)
            : base(processId, transferInfo)
        {
        }
    }
}