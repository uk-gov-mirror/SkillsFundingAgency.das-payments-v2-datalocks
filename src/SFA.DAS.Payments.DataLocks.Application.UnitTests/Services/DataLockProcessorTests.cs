using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extras.Moq;
using AutoMapper;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using SFA.DAS.Payments.Application.Infrastructure.Logging;
using SFA.DAS.Payments.Application.Messaging;
using SFA.DAS.Payments.Application.Repositories;
using SFA.DAS.Payments.DataLocks.Application.Mapping;
using SFA.DAS.Payments.DataLocks.Application.Services;
using SFA.DAS.Payments.DataLocks.Domain.Models;
using SFA.DAS.Payments.DataLocks.Domain.Services.CourseValidation;
using SFA.DAS.Payments.DataLocks.Domain.Services.LearnerMatching;
using SFA.DAS.Payments.DataLocks.Messages.Events;
using SFA.DAS.Payments.EarningEvents.Messages.Events;
using SFA.DAS.Payments.Messages.Common.Events;
using SFA.DAS.Payments.Model.Core;
using SFA.DAS.Payments.Model.Core.Entities;
using SFA.DAS.Payments.Model.Core.Incentives;
using SFA.DAS.Payments.Model.Core.OnProgramme;
using SFA.DAS.Payments.ServiceFabric.Core.Messaging;

namespace SFA.DAS.Payments.DataLocks.Application.UnitTests.Services
{

    [TestFixture]
    public class DataLockProcessorTests
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

            apprenticeships = new List<ApprenticeshipModel>
            {
                new ApprenticeshipModel { Id = 1, AccountId = 456, Uln = Uln, Ukprn = Ukprn },
                new ApprenticeshipModel { Id = 2, AccountId = 456, Uln = Uln, Ukprn = Ukprn }
            };

            earningEvent = CreateTestEarningEvent(1, 100m, aim);
            earningEvent.LearningAim = aim;

            learnerMatcherMock = mocker.Mock<ILearnerMatcher>();
            onProgValidationMock = mocker.Mock<IOnProgrammeAndIncentiveEarningPeriodsValidationProcessor>();
            functionalSkillsValidationMock = mocker.Mock<IFunctionalSkillEarningPeriodsValidationProcessor>();
            mocker.Mock<IDuplicateEarningEventService>()
                .Setup(x => x.IsDuplicate(It.IsAny<IPaymentsEvent>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        }

        private AutoMock mocker;

        private IMapper mapper;
        private ApprenticeshipContractType1EarningEvent earningEvent;
        private Mock<ILearnerMatcher> learnerMatcherMock;
        private Mock<IOnProgrammeAndIncentiveEarningPeriodsValidationProcessor> onProgValidationMock;
        private Mock<IFunctionalSkillEarningPeriodsValidationProcessor> functionalSkillsValidationMock;
        private List<ApprenticeshipModel> apprenticeships;
        private const long Uln = 123;
        private const int AcademicYear = 1819;
        private LearningAim aim;
        private const long Ukprn = 123;

        [Test]
        public async Task GivenNoDataLockErrorAllEarningPeriodsShouldBePayableEvent()
        {
            learnerMatcherMock
                .Setup(x => x.MatchLearner(apprenticeships[0].Ukprn, apprenticeships[0].Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = null,
                    Apprenticeships = apprenticeships
                })
                .Verifiable();

            onProgValidationMock
                .Setup(x => x.ValidatePeriods(Ukprn, apprenticeships[0].Uln,
                    earningEvent.PriceEpisodes,
                    It.IsAny<List<EarningPeriod>>(),
                    (TransactionType)earningEvent.OnProgrammeEarnings[0].Type,
                    apprenticeships,
                    aim,
                    AcademicYear))
                .Returns(() =>
                    (new List<EarningPeriod> { earningEvent.OnProgrammeEarnings.FirstOrDefault()?.Periods.FirstOrDefault() },
                        new List<EarningPeriod>()
                    )).Verifiable();

            var dataLockProcessor = mocker.Create<DataLockProcessor>();
            var dataLockEvents = await dataLockProcessor.GetPaymentEvents(earningEvent, default);

            dataLockEvents.Should().NotBeNull();
            dataLockEvents.Should().HaveCount(1);

            var payableEarning = dataLockEvents[0] as PayableEarningEvent;
            payableEarning.Should().NotBeNull();
            payableEarning.OnProgrammeEarnings.Count.Should().Be(1);
            payableEarning.OnProgrammeEarnings.First().Periods.Count.Should().Be(1);

            payableEarning.IncentiveEarnings.Should().NotBeNull();
            payableEarning.IncentiveEarnings.Count.Should().Be(1);
            payableEarning.IncentiveEarnings.First().Periods.Count.Should().Be(1);
            learnerMatcherMock.Verify();
            onProgValidationMock.Verify();
        }

        [Test]
        public async Task LearnerDataLockForEarningWithNoIncentivesShouldReturnValidNonPayableEarningEvent()
        {
            earningEvent.IncentiveEarnings = new List<IncentiveEarning>();

            learnerMatcherMock
                .Setup(x => x.MatchLearner(apprenticeships[0].Ukprn, apprenticeships[0].Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = DataLockErrorCode.DLOCK_01,
                    Apprenticeships = new List<ApprenticeshipModel>(apprenticeships)
                }).Verifiable();

            var dataLockProcessor = mocker.Create<DataLockProcessor>();
            var dataLockEvents = await dataLockProcessor.GetPaymentEvents(earningEvent, default);
            dataLockEvents.Should().NotBeNull();
            dataLockEvents.Should().HaveCount(1);
            var nonPayableEarningEvent = dataLockEvents[0] as EarningFailedDataLockMatching;
            nonPayableEarningEvent.Should().NotBeNull();
            nonPayableEarningEvent.OnProgrammeEarnings
                .SelectMany(x => x.Periods)
                .All(p => p.DataLockFailures.All(d => d.DataLockError == DataLockErrorCode.DLOCK_01))
                .Should().BeTrue();
            learnerMatcherMock.Verify();
            onProgValidationMock.Verify();
        }

        [Test]
        public async Task LearnerDataLockForEarningWithIncentivesShouldReturnValidNonPayableEarningEvent()
        {
            learnerMatcherMock
                .Setup(x => x.MatchLearner(apprenticeships[0].Ukprn, apprenticeships[0].Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = DataLockErrorCode.DLOCK_01,
                    Apprenticeships = new List<ApprenticeshipModel>(apprenticeships)
                }).Verifiable();

            var dataLockProcessor = mocker.Create<DataLockProcessor>();
            var dataLockEvents = await dataLockProcessor.GetPaymentEvents(earningEvent, default);
            dataLockEvents.Should().NotBeNull();
            dataLockEvents.Should().HaveCount(1);
            var nonPayableEarningEvent = dataLockEvents[0] as EarningFailedDataLockMatching;
            nonPayableEarningEvent.Should().NotBeNull();
            nonPayableEarningEvent.IncentiveEarnings
                .SelectMany(x => x.Periods)
                .All(p => p.DataLockFailures.All(d => d.DataLockError == DataLockErrorCode.DLOCK_01))
                .Should().BeTrue();
            learnerMatcherMock.Verify();
            onProgValidationMock.Verify();
        }

        [Test]
        public async Task GivenCourseValidationDataLockIsReturnedMapBothValidAndInvalidEarningPeriods()
        {
            learnerMatcherMock
                .Setup(x => x.MatchLearner(apprenticeships[0].Ukprn, apprenticeships[0].Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = null,
                    Apprenticeships = new List<ApprenticeshipModel>(apprenticeships)
                }).Verifiable();

            var testEarningEvent = CreateTestEarningEvent(2, 100m, aim);
            testEarningEvent.IncentiveEarnings = new List<IncentiveEarning>();

            var periodExpected = testEarningEvent.OnProgrammeEarnings[0].Periods[1];
            periodExpected.DataLockFailures = new List<DataLockFailure>
            {
                new DataLockFailure
                {
                    DataLockError = DataLockErrorCode.DLOCK_09,
                    ApprenticeshipPriceEpisodeIds = new List<long>()
                }
            };

            onProgValidationMock
                .Setup(x => x.ValidatePeriods(Ukprn, apprenticeships[0].Uln,
                    It.IsAny<List<PriceEpisode>>(),
                    It.IsAny<List<EarningPeriod>>(),
                    It.IsAny<TransactionType>(),
                    It.IsAny<List<ApprenticeshipModel>>(),
                    aim,
                    AcademicYear))
                .Returns(() => (new List<EarningPeriod>
                {
                    testEarningEvent.OnProgrammeEarnings[0].Periods[0]
                }, new List<EarningPeriod>
                {
                    periodExpected
                }))
                .Verifiable();

            var dataLockProcessor = mocker.Create<DataLockProcessor>();
            var actual = await dataLockProcessor.GetPaymentEvents(testEarningEvent, default)
                .ConfigureAwait(false);

            var payableEarnings = actual.OfType<PayableEarningEvent>().ToList();

            payableEarnings.Should().HaveCount(1);
            var payableEarning = payableEarnings[0];
            payableEarning.OnProgrammeEarnings.Count.Should().Be(1);

            var onProgrammeEarning = payableEarning.OnProgrammeEarnings.First();
            onProgrammeEarning.Periods.Count.Should().Be(1);

            var earningPeriod = onProgrammeEarning.Periods.Single();
            earningPeriod.Period.Should().Be(1);

            var failedDatalockEarnings = actual.OfType<EarningFailedDataLockMatching>().ToList();
            failedDatalockEarnings.Should().HaveCount(1);

            var failedDatalockEarning = failedDatalockEarnings[0];
            failedDatalockEarning.OnProgrammeEarnings.Count.Should().Be(1);

            var invalidOnProgrammeEarning = failedDatalockEarning.OnProgrammeEarnings[0];
            invalidOnProgrammeEarning.Periods.Count.Should().Be(1);

            var invalidEarningPeriod = invalidOnProgrammeEarning.Periods.Single();
            invalidEarningPeriod.Period.Should().Be(2);
            invalidEarningPeriod.DataLockFailures.Should().NotBeNull();
            invalidEarningPeriod.DataLockFailures.Should().HaveCount(1);
            invalidEarningPeriod.DataLockFailures[0].DataLockError.Should().Be(DataLockErrorCode.DLOCK_09);
            learnerMatcherMock.Verify();
            onProgValidationMock.Verify();
        }

        [Test]
        public async Task
            CourseValidationDataLockForEarningWithIncentivesMapBothValidAndInvalidIncentivesAndOnprogEarningPeriods()
        {
            learnerMatcherMock
                .Setup(x => x.MatchLearner(apprenticeships[0].Ukprn, apprenticeships[0].Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = null,
                    Apprenticeships = new List<ApprenticeshipModel>(apprenticeships)
                }).Verifiable();

            var testEarningEvent = CreateTestEarningEvent(2, 100m, aim);

            var periodExpected = testEarningEvent.OnProgrammeEarnings[0].Periods[1];
            periodExpected.DataLockFailures = new List<DataLockFailure>
            {
                new DataLockFailure
                {
                    DataLockError = DataLockErrorCode.DLOCK_09,
                    ApprenticeshipPriceEpisodeIds = new List<long>()
                }
            };

            onProgValidationMock
                .Setup(x => x.ValidatePeriods(Ukprn, apprenticeships[0].Uln,
                    It.IsAny<List<PriceEpisode>>(),
                    It.IsAny<List<EarningPeriod>>(),
                    It.IsAny<TransactionType>(),
                    It.IsAny<List<ApprenticeshipModel>>(),
                    aim,
                    AcademicYear))
                .Returns(() => (new List<EarningPeriod>
                {
                    testEarningEvent.OnProgrammeEarnings[0].Periods[0]
                }, new List<EarningPeriod>
                {
                    periodExpected
                }))
                .Verifiable();

            var dataLockProcessor = mocker.Create<DataLockProcessor>();
            var actualDataLockEvents = await dataLockProcessor.GetPaymentEvents(testEarningEvent, default)
                .ConfigureAwait(false);

            var payableEarnings = actualDataLockEvents.OfType<PayableEarningEvent>().ToList();

            payableEarnings.Should().HaveCount(1);
            var payableEarning = payableEarnings[0];

            //validate OnProgrammeEarnings
            payableEarning.OnProgrammeEarnings.Count.Should().Be(1);

            var onProgrammeEarning = payableEarning.OnProgrammeEarnings.First();
            onProgrammeEarning.Periods.Count.Should().Be(1);

            var earningPeriod = onProgrammeEarning.Periods.Single();
            earningPeriod.Period.Should().Be(1);


            var failedDatalockEarnings = actualDataLockEvents.OfType<EarningFailedDataLockMatching>().ToList();
            failedDatalockEarnings.Should().HaveCount(1);

            var failedDatalockEarning = failedDatalockEarnings[0];
            failedDatalockEarning.OnProgrammeEarnings.Count.Should().Be(1);

            var invalidOnProgrammeEarning = failedDatalockEarning.OnProgrammeEarnings[0];
            invalidOnProgrammeEarning.Periods.Count.Should().Be(1);

            var invalidEarningPeriod = invalidOnProgrammeEarning.Periods.Single();
            invalidEarningPeriod.Period.Should().Be(2);
            invalidEarningPeriod.DataLockFailures.Should().NotBeNull();
            invalidEarningPeriod.DataLockFailures.Should().HaveCount(1);
            invalidEarningPeriod.DataLockFailures[0].DataLockError.Should().Be(DataLockErrorCode.DLOCK_09);

            //Validate Incentives
            payableEarning.IncentiveEarnings.Count.Should().Be(1);
            var incentiveEarnings = payableEarning.IncentiveEarnings.First();
            incentiveEarnings.Periods.Count.Should().Be(1);

            var incentiveEarningPeriod = incentiveEarnings.Periods.Single();
            incentiveEarningPeriod.Period.Should().Be(1);

            failedDatalockEarning.IncentiveEarnings.Count.Should().Be(1);

            var invalidIncentiveEarning = failedDatalockEarning.IncentiveEarnings[0];
            invalidIncentiveEarning.Periods.Count.Should().Be(1);

            var invalidIncentiveEarningPeriod = invalidIncentiveEarning.Periods.Single();
            invalidIncentiveEarningPeriod.Period.Should().Be(2);
            invalidIncentiveEarningPeriod.DataLockFailures.Should().NotBeNull();
            invalidIncentiveEarningPeriod.DataLockFailures.Should().HaveCount(1);
            invalidIncentiveEarningPeriod.DataLockFailures[0].DataLockError.Should().Be(DataLockErrorCode.DLOCK_09);
            learnerMatcherMock.Verify();
            onProgValidationMock.Verify();
        }

        [Test]
        public async Task CourseValidationForFunctionalSkillMapsValidAndInvalidPeriods()
        {
            learnerMatcherMock
                .Setup(x => x.MatchLearner(Ukprn, apprenticeships[0].Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = null,
                    Apprenticeships = new List<ApprenticeshipModel>(apprenticeships)
                }).Verifiable();

            var testEarningEvent = CreateTestFunctionalSkillEarningEvent(2, 100m, aim);

            var periodExpected = testEarningEvent.Earnings[0].Periods[1];
            periodExpected.DataLockFailures = new List<DataLockFailure>
            {
                new DataLockFailure
                {
                    DataLockError = DataLockErrorCode.DLOCK_09,
                    ApprenticeshipPriceEpisodeIds = new List<long>()
                }
            };

            functionalSkillsValidationMock
                .Setup(x => x.ValidatePeriods(Ukprn, apprenticeships[0].Uln,
                    It.IsAny<List<EarningPeriod>>(),
                    It.IsAny<TransactionType>(),
                    It.IsAny<List<ApprenticeshipModel>>(),
                    aim,
                    AcademicYear))
                .Returns(() => (new List<EarningPeriod>
                {
                    testEarningEvent.Earnings[0].Periods[0]
                }, new List<EarningPeriod>
                {
                    periodExpected
                }))
                .Verifiable();

            var dataLockProcessor = mocker.Create<DataLockProcessor>();
            var actualDataLockEvents = await dataLockProcessor
                .GetFunctionalSkillPaymentEvents(testEarningEvent, default)
                .ConfigureAwait(false);

            var payableEarnings = actualDataLockEvents.OfType<PayableFunctionalSkillEarningEvent>().ToList();
            payableEarnings.Should().HaveCount(1);

            var failedDataLockEarnings =
                actualDataLockEvents.OfType<FunctionalSkillEarningFailedDataLockMatching>().ToList();
            failedDataLockEarnings.Should().HaveCount(1);
            learnerMatcherMock.Verify();
            onProgValidationMock.Verify();
        }


        private ApprenticeshipContractType1EarningEvent CreateTestEarningEvent(byte periodsToCreate,
            decimal earningPeriodAmount, LearningAim testAim)
        {
            var testEarningEvent = new ApprenticeshipContractType1EarningEvent
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

        private Act1FunctionalSkillEarningsEvent CreateTestFunctionalSkillEarningEvent(byte periodsToCreate,
            decimal earningPeriodAmount, LearningAim testAim)
        {
            var testEarningEvent = new Act1FunctionalSkillEarningsEvent
            {
                Ukprn = Ukprn,
                Learner = new Learner { Uln = Uln },
                PriceEpisodes = new List<PriceEpisode>(),
                CollectionYear = AcademicYear
            };

            var functionalSkillEarnings = new List<FunctionalSkillEarning>
            {
                new FunctionalSkillEarning
                {
                    Periods = new ReadOnlyCollection<EarningPeriod>(GenerateEarningPeriod(periodsToCreate,
                        earningPeriodAmount, testEarningEvent))
                }
            };

            testEarningEvent.Earnings = functionalSkillEarnings.AsReadOnly();

            testEarningEvent.LearningAim = testAim;

            return testEarningEvent;
        }

        private static List<EarningPeriod> GenerateEarningPeriod(byte periodsToCreate, decimal earningPeriodAmount,
            ApprenticeshipContractType1EarningEvent testEarningEvent)
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

        [Test]
        public async Task DoesNotProcessDuplicateEarningEvents()
        {
            learnerMatcherMock
                .Setup(x => x.MatchLearner(apprenticeships[0].Ukprn, apprenticeships[0].Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = null,
                    Apprenticeships = apprenticeships
                })
                .Verifiable();

            onProgValidationMock
                .Setup(x => x.ValidatePeriods(Ukprn, apprenticeships[0].Uln,
                    earningEvent.PriceEpisodes,
                    It.IsAny<List<EarningPeriod>>(),
                    (TransactionType)earningEvent.OnProgrammeEarnings[0].Type,
                    apprenticeships,
                    aim,
                    AcademicYear))
                .Returns(() =>
                    (new List<EarningPeriod> { earningEvent.OnProgrammeEarnings.FirstOrDefault()?.Periods.FirstOrDefault() },
                        new List<EarningPeriod>()
                    )).Verifiable();

            mocker.Mock<IDuplicateEarningEventService>()
                .Setup(x => x.IsDuplicate(It.IsAny<IPaymentsEvent>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var dataLockProcessor = mocker.Create<DataLockProcessor>();
            var dataLockEvents = await dataLockProcessor.GetPaymentEvents(earningEvent, default);

            dataLockEvents.Should().NotBeNull();
            dataLockEvents.Should().HaveCount(0);
        }


        [Test]
        public async Task DoesProcessGSOEarningEventsWithDuplicateValuesButDifferentEarningIds()
        {
            var seenKeys = new HashSet<string>();
            var cacheMock = new Mock<IActorDataCache<EarningEventKey>>();
            cacheMock
                .Setup(x => x.Contains(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => seenKeys.Contains(key));
            cacheMock
                .Setup(x => x.Add(It.IsAny<string>(), It.IsAny<EarningEventKey>(), It.IsAny<CancellationToken>()))
                .Returns((string key, EarningEventKey _, CancellationToken __) =>
                {
                    seenKeys.Add(key);
                    return Task.CompletedTask;
                });

            var duplicateService = new DuplicateEarningEventService(new Mock<IPaymentLogger>().Object, cacheMock.Object);

            var learnerMatcherTestMock = new Mock<ILearnerMatcher>();
            var onProgValidationTestMock = new Mock<IOnProgrammeAndIncentiveEarningPeriodsValidationProcessor>();
            var functionalSkillsValidationTestMock = new Mock<IFunctionalSkillEarningPeriodsValidationProcessor>();

            learnerMatcherTestMock
                .Setup(x => x.MatchLearner(Ukprn, Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = null,
                    Apprenticeships = apprenticeships
                })
                .Verifiable();

            var firstEarningEvent = CreateGSOEarningEvent(Guid.NewGuid());
            var secondEarningEvent = CreateGSOEarningEvent(Guid.NewGuid());

            onProgValidationTestMock
                .Setup(x => x.ValidatePeriods(Ukprn, Uln,
                    It.IsAny<List<PriceEpisode>>(),
                    It.IsAny<List<EarningPeriod>>(),
                    It.IsAny<TransactionType>(),
                    apprenticeships,
                    aim,
                    AcademicYear))
                .Returns(() =>
                    (new List<EarningPeriod>
                    {
                        new EarningPeriod
                        {
                            Amount = 100m,
                            Period = 1,
                            PriceEpisodeIdentifier = "pe-1"
                        }
                    }, new List<EarningPeriod>()))
                .Verifiable();

            var dataLockProcessor = new DataLockProcessor(
                mapper,
                learnerMatcherTestMock.Object,
                onProgValidationTestMock.Object,
                functionalSkillsValidationTestMock.Object,
                duplicateService);

            
            var firstDataLockEvents = await dataLockProcessor.GetPaymentEvents(firstEarningEvent, default);
            var secondDataLockEvents = await dataLockProcessor.GetPaymentEvents(secondEarningEvent, default);

            firstDataLockEvents.Should().HaveCount(1);
            firstDataLockEvents.OfType<PayableEarningEvent>().Should().HaveCount(1);

            secondDataLockEvents.Should().HaveCount(1);
            secondDataLockEvents.OfType<PayableEarningEvent>().Should().HaveCount(1);

            var expectedKeys = new List<string>
            {
                new EarningEventKey(firstEarningEvent).Key,
                new EarningEventKey(secondEarningEvent).Key
            };

            seenKeys.Should().BeEquivalentTo(expectedKeys);

            cacheMock.Verify(x => x.Contains(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
            cacheMock.Verify(x => x.Add(It.IsAny<string>(), It.IsAny<EarningEventKey>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
            learnerMatcherTestMock.Verify(x => x.MatchLearner(Ukprn, Uln), Times.Exactly(2));
            onProgValidationTestMock.Verify(x => x.ValidatePeriods(
                Ukprn,
                Uln,
                It.IsAny<List<PriceEpisode>>(),
                It.IsAny<List<EarningPeriod>>(),
                It.IsAny<TransactionType>(),
                apprenticeships,
                aim,
                AcademicYear), Times.Exactly(4));
        }

        [Test]
        public async Task DoesNotProcessGSOEarningEventsWithDuplicateValuesButSameEarningIds()
        {
            // Arrange
            var seenKeys = new HashSet<string>();
            var cacheMock = new Mock<IActorDataCache<EarningEventKey>>();
            cacheMock
                .Setup(x => x.Contains(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => seenKeys.Contains(key));
            cacheMock
                .Setup(x => x.Add(It.IsAny<string>(), It.IsAny<EarningEventKey>(), It.IsAny<CancellationToken>()))
                .Returns((string key, EarningEventKey _, CancellationToken __) =>
                {
                    seenKeys.Add(key);
                    return Task.CompletedTask;
                });

            var duplicateService = new DuplicateEarningEventService(new Mock<IPaymentLogger>().Object, cacheMock.Object);

            var learnerMatcherTestMock = new Mock<ILearnerMatcher>();
            var onProgValidationTestMock = new Mock<IOnProgrammeAndIncentiveEarningPeriodsValidationProcessor>();
            var functionalSkillsValidationTestMock = new Mock<IFunctionalSkillEarningPeriodsValidationProcessor>();

            learnerMatcherTestMock
                .Setup(x => x.MatchLearner(Ukprn, Uln))
                .ReturnsAsync(() => new LearnerMatchResult
                {
                    DataLockErrorCode = null,
                    Apprenticeships = apprenticeships
                })
                .Verifiable();

            var eventId = Guid.NewGuid();
            var firstEarningEvent = CreateGSOEarningEvent(eventId);
            var secondEarningEvent = CreateGSOEarningEvent(eventId);

            onProgValidationTestMock
                .Setup(x => x.ValidatePeriods(Ukprn, Uln,
                    It.IsAny<List<PriceEpisode>>(),
                    It.IsAny<List<EarningPeriod>>(),
                    It.IsAny<TransactionType>(),
                    apprenticeships,
                    aim,
                    AcademicYear))
                .Returns(() =>
                    (new List<EarningPeriod>
                    {
                        new EarningPeriod
                        {
                            Amount = 100m,
                            Period = 1,
                            PriceEpisodeIdentifier = "pe-1"
                        }
                    }, new List<EarningPeriod>()))
                .Verifiable();

            var dataLockProcessor = new DataLockProcessor(
                mapper,
                learnerMatcherTestMock.Object,
                onProgValidationTestMock.Object,
                functionalSkillsValidationTestMock.Object,
                duplicateService);

            // Act
            var firstDataLockEvents = await dataLockProcessor.GetPaymentEvents(firstEarningEvent, default);
            var secondDataLockEvents = await dataLockProcessor.GetPaymentEvents(secondEarningEvent, default);

            // Assert
            firstDataLockEvents.Should().HaveCount(1);
            firstDataLockEvents.OfType<PayableEarningEvent>().Should().HaveCount(1);

            secondDataLockEvents.Should().HaveCount(0);

            var expectedKey = new EarningEventKey(firstEarningEvent).Key;
            new EarningEventKey(secondEarningEvent).Key.Should().Be(expectedKey);
            seenKeys.Should().BeEquivalentTo(new[] { expectedKey });

            cacheMock.Verify(x => x.Contains(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
            cacheMock.Verify(x => x.Add(It.IsAny<string>(), It.IsAny<EarningEventKey>(), It.IsAny<CancellationToken>()), Times.Once);
            learnerMatcherTestMock.Verify(x => x.MatchLearner(Ukprn, Uln), Times.Once);
            onProgValidationTestMock.Verify(x => x.ValidatePeriods(
                Ukprn,
                Uln,
                It.IsAny<List<PriceEpisode>>(),
                It.IsAny<List<EarningPeriod>>(),
                It.IsAny<TransactionType>(),
                apprenticeships,
                aim,
                AcademicYear), Times.Exactly(2));
        }

        private ApprenticeshipContractType1EarningEvent CreateGSOEarningEvent(Guid eventId) => new ApprenticeshipContractType1EarningEvent
        {
            EventId = eventId,
            Learner = new Learner { Uln = Uln },
            PriceEpisodes = new List<PriceEpisode>
            {
                new PriceEpisode
                {
                    EffectiveTotalNegotiatedPriceStartDate = DateTime.UtcNow.AddDays(1),
                    Identifier = "pe-1"
                }
            },
            CollectionYear = AcademicYear,
            JobId = 0,
            CollectionPeriod = new CollectionPeriod { Period = 1, AcademicYear = AcademicYear },
            Ukprn = Ukprn,
            LearningAim = aim,
            OnProgrammeEarnings = new List<OnProgrammeEarning>
            {
                new OnProgrammeEarning
                {
                    Periods = new ReadOnlyCollection<EarningPeriod>(new List<EarningPeriod>
                    {
                        new EarningPeriod
                        {
                            Amount = 100m,
                            Period = 1,
                            PriceEpisodeIdentifier = "pe-1"
                        }
                    })
                }
            },
            IncentiveEarnings = new List<IncentiveEarning>
            {
                new IncentiveEarning
                {
                    Periods = new ReadOnlyCollection<EarningPeriod>(new List<EarningPeriod>
                    {
                        new EarningPeriod
                        {
                            Amount = 100m,
                            Period = 1,
                            PriceEpisodeIdentifier = "pe-1"
                        }
                    })
                }
            }
        };

    }
}