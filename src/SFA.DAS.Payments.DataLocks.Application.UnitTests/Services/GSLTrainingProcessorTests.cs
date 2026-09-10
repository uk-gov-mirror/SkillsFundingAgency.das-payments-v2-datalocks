using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Autofac;
using Autofac.Extras.Moq;
using AutoMapper;
using FluentAssertions;
using NUnit.Framework;
using SFA.DAS.Payments.DataLocks.Application.Mapping;
using SFA.DAS.Payments.DataLocks.Application.Services;
using SFA.DAS.Payments.DataLocks.Messages.Events;
using SFA.DAS.Payments.EarningEvents.Messages.Events;
using SFA.DAS.Payments.Model.Core;
using SFA.DAS.Payments.Model.Core.Incentives;
using SFA.DAS.Payments.Model.Core.OnProgramme;

namespace SFA.DAS.Payments.DataLocks.Application.UnitTests.Services
{
    [TestFixture]
    public class GSLTrainingProcessorTests
    {
        [OneTimeSetUp]
        public void Initialise()
        {
            var configuration = new MapperConfiguration(cfg => { cfg.AddProfile<DataLocksProfile>(); });
            configuration.AssertConfigurationIsValid();
            mapper = configuration.CreateMapper();
            aim = new LearningAim();
        }

        [SetUp]
        public void Setup()
        {
            mocker = AutoMock.GetLoose(cfg => cfg.RegisterInstance(mapper).As<IMapper>());

            earningEvent = CreateTestEarningEvent(1, 100m, aim);
            earningEvent.LearningAim = aim;
        }

        private AutoMock mocker;
        private IMapper mapper;
        private GSLApprenticeshipEarningsEvent earningEvent;
        private const long Uln = 123;
        private const int AcademicYear = 1819;
        private LearningAim aim;
        private const long Ukprn = 123;

        [Test]
        public void Returns_All_Earnings_As_Payable_If_Nothing_Is_Withheld()
        {
            var processor = mocker.Create<GSLTrainingProcessor>();
            var dataLockEvents = processor.Process(earningEvent, default).Result;
            dataLockEvents.Should().NotBeEmpty();
            dataLockEvents.Should().HaveCount(1);
            dataLockEvents.Should().AllBeOfType<PayableEarningEvent>();
        }

        private GSLApprenticeshipEarningsEvent CreateTestEarningEvent(byte periodsToCreate,
                    decimal earningPeriodAmount, LearningAim testAim)
        {
            var testEarningEvent = new GSLApprenticeshipEarningsEvent
            {
                Learner = new Learner { Uln = Uln },
                PriceEpisodes = new List<PriceEpisode>(),
                CollectionYear = AcademicYear,
                Ukprn = Ukprn
            };

            testEarningEvent.OnProgrammeEarnings = new List<OnProgrammeEarning>
            {
                new OnProgrammeEarning
                {
                    Periods = new ReadOnlyCollection<EarningPeriod>(GenerateEarningPeriod(periodsToCreate,
                        earningPeriodAmount, testEarningEvent))
                }
            };

            testEarningEvent.IncentiveEarnings = new List<IncentiveEarning>
            {
                new IncentiveEarning
                {
                    Periods = new ReadOnlyCollection<EarningPeriod>(GenerateEarningPeriod(periodsToCreate,
                        earningPeriodAmount, testEarningEvent))
                }
            };

            testEarningEvent.LearningAim = testAim;

            return testEarningEvent;
        }


        private static List<EarningPeriod> GenerateEarningPeriod(byte periodsToCreate, decimal earningPeriodAmount,
            GSLApprenticeshipEarningsEvent testEarningEvent)
        {
            var earningPeriods = new List<EarningPeriod>();

            for (byte i = 1; i <= periodsToCreate; i++)
            {
                testEarningEvent.PriceEpisodes.Add(new PriceEpisode
                {
                    EffectiveTotalNegotiatedPriceStartDate = DateTime.UtcNow.AddDays(1),
                    Identifier = $"pe-{i}"
                });

                earningPeriods.Add(new EarningPeriod
                {
                    Amount = earningPeriodAmount,
                    Period = i,
                    PriceEpisodeIdentifier = $"pe-{i}"
                });
            }

            return earningPeriods;
        }

        private static List<EarningPeriod> GenerateEarningPeriod(byte periodsToCreate, decimal earningPeriodAmount,
            Act1FunctionalSkillEarningsEvent testEarningEvent)
        {
            var earningPeriods = new List<EarningPeriod>();

            for (byte i = 1; i <= periodsToCreate; i++)
            {
                testEarningEvent.PriceEpisodes.Add(new PriceEpisode
                {
                    EffectiveTotalNegotiatedPriceStartDate = DateTime.UtcNow.AddDays(1),
                    Identifier = $"pe-{i}"
                });

                earningPeriods.Add(new EarningPeriod
                {
                    Amount = earningPeriodAmount,
                    Period = i,
                    PriceEpisodeIdentifier = $"pe-{i}"
                });
            }

            return earningPeriods;
        }
    }
}