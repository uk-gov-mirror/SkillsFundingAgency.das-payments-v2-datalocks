using AutoMapper;
using SFA.DAS.Payments.Application.Infrastructure.Logging;
using SFA.DAS.Payments.DataLocks.Messages.Events;
using SFA.DAS.Payments.EarningEvents.Messages.Events;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SFA.DAS.Payments.DataLocks.Application.Services
{
    public interface IGSLTrainingProcessor
    {
        Task<List<DataLockEvent>> Process(GSLApprenticeshipEarningsEvent earningEvent, CancellationToken cancellationToken);
    }

    public class GSLTrainingProcessor: IGSLTrainingProcessor
    {
        private readonly IMapper mapper;
        private readonly IPaymentLogger logger;

        public GSLTrainingProcessor(IMapper mapper, IPaymentLogger logger)
        {
            this.mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        //TODO: There is no base type for the GSL earning events, need to create one and refactor the code to use it. For now, this is a placeholder for the GSL earning event processing.
        public async Task<List<DataLockEvent>> Process(GSLApprenticeshipEarningsEvent earningEvent, CancellationToken cancellationToken)  
        {
            var dataLockEvents = new List<DataLockEvent>();

            var dataLockEvent = mapper.Map<PayableEarningEvent>(earningEvent);
            dataLockEvents.Add(dataLockEvent);
            return dataLockEvents;
        }
    }
}
