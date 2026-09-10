using System;
using SFA.DAS.Payments.Messages.Common;
using SFA.DAS.Payments.Model.Core.Entities;

namespace SFA.DAS.Payments.DataLocks.Messages.Events
{
    public interface IPayableEarningEvent: IDataLockEvent
    {
        DateTime StartDate { get; set; }
        int? AgeAtStartOfLearning { get; set; }
    }

    public class PayableEarningEvent : DataLockEvent, IPayableEarningEvent, IMonitoredMessage
    {
        public DateTime StartDate { get; set; }
        public int? AgeAtStartOfLearning { get; set; }
        public PayableEarningEvent() => FundingPlatformType = FundingPlatformType.SubmitLearnerData;
    }
}