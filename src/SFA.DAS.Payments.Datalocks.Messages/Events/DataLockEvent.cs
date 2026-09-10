using SFA.DAS.Payments.Messages.Common.Events;
using SFA.DAS.Payments.Model.Core;
using SFA.DAS.Payments.Model.Core.Entities;
using SFA.DAS.Payments.Model.Core.Incentives;
using SFA.DAS.Payments.Model.Core.OnProgramme;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace SFA.DAS.Payments.DataLocks.Messages.Events
{
    public interface IDataLockEvent: IContractType1EarningEvent
    {
        Guid EarningEventId { get; set; }
        FundingPlatformType FundingPlatformType { get; set; } 
    }

    [KnownType("GetInheritors")]
    public abstract class DataLockEvent : PaymentsEvent, IDataLockEvent, IContractType1EarningEvent
    {
        private static Type[] inheritors;
        public Guid EarningEventId { get; set; }
        public List<PriceEpisode> PriceEpisodes { get; set; }
        public short CollectionYear { get; set; }
        public string AgreementId { get; set; }
        public List<OnProgrammeEarning> OnProgrammeEarnings { get; set; }
        public List<IncentiveEarning> IncentiveEarnings { get; set; }
        public virtual FundingPlatformType FundingPlatformType { get; set; }

        private static Type[] GetInheritors()
        {
            return inheritors ?? (inheritors = typeof(DataLockEvent).Assembly.GetTypes()
                .Where(x => x.IsSubclassOf(typeof(DataLockEvent)))
                .ToArray());
        }
    }
}