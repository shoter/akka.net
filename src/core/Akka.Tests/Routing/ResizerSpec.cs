﻿//-----------------------------------------------------------------------
// <copyright file="ResizerSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2021 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2021 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Routing;
using Akka.TestKit;
using Xunit;
using FluentAssertions;
using FluentAssertions.Extensions;

namespace Akka.Tests.Routing
{
    public class ResizerSpec : AkkaSpec
    {
        public ResizerSpec() : base(GetConfig())
        {
        }

        private static string GetConfig()
        {
            return @"
                akka.actor.serialize-messages = off
                akka.actor.deployment {
                  /router1 {
                    router = round-robin-pool
                    resizer {
                      enabled = on
                      lower-bound = 2
                      upper-bound = 3
                    }
                  }
                }";
        }

        private class ResizerTestActor : ReceiveActor
        {
            public ResizerTestActor()
            {
                Receive<TestLatch>(latch => latch.CountDown());
            }
        }

        private class PressureActor : ReceiveActor
        {
            public PressureActor()
            {
                ReceiveAsync<TimeSpan>(async d =>
                {
                    await Task.Delay(d);
                    Sender.Tell("done");
                });

                Receive<string>(s => s == "echo", s =>
                {
                    Sender.Tell("reply");
                });
            }
        }

        private class PressureAsyncActor : ReceiveActor
        {
            public PressureAsyncActor()
            {
                ReceiveAsync<TimeSpan>(async d =>
                {
                    await Task.Delay(d);
                    Sender.Tell("done");
                });

                ReceiveAsync<string>(s => s == "echo", s =>
                {
                    Sender.Tell("reply");
                    return Task.CompletedTask;
                });
            }
        }

        private class BackoffActor : ReceiveActor
        {
            private readonly Func<TimeSpan, TimeSpan> _dilated;

            public BackoffActor(Func<TimeSpan, TimeSpan> dilated)
            {
                _dilated = dilated;

                ReceiveAsync<int>(async n =>
                {
                    if (n <= 0)
                    {
                        // done
                    }
                    else
                    {
                        await Task.Delay(_dilated(TimeSpan.FromMilliseconds(n)));
                    }
                });
            }
        }

        private static async Task<int> RouteeSize(IActorRef router)
        {
            return (await router.Ask<Routees>(new GetRoutees())).Members.Count();
        }

        [Fact(Skip = "DefaultOptimalSizeExploringResizer has not implemented yet")]
        public void Resizer_fromConfig_must_load_DefaultResizer_from_config_when_resizer_is_enabled()
        {
            var config = ConfigurationFactory.ParseString(@"
                resizer {
                  enabled = on
                }
            ");

            // Resizer.FromConfig(config).GetType().ShouldBe(typeof(DefaultOptimalSizeExploringResizer));
        }

        [Fact(Skip = "DefaultOptimalSizeExploringResizer has not implemented yet")]
        public void Resizer_fromConfig_must_load_MetricsBasedResizer_fromConfig_when_optimalsizeexploringresizer_is_enabled()
        {
            var config = ConfigurationFactory.ParseString(@"
                optimal-size-exploring-resizer {
                  enabled = on
                }
            ");

            //Assert.Throws<ResizerInitializationException>(() =>
            //{
            //    Resizer.FromConfig(config);
            //});
        }

        [Fact(Skip = "DefaultOptimalSizeExploringResizer has not implemented yet")]
        public void Resizer_fromConfig_must_load_MetricsBasedResizer_fromConfig_when_both_resizer_and_optimalsizeexploringresizer_is_enabled()
        {
            var config = ConfigurationFactory.ParseString(@"
                resizer {
                  enabled = on
                }
                optimal-size-exploring-resizer {
                  enabled = on
                }
            ");

            //Assert.Throws<ResizerInitializationException>(() =>
            //{
            //    Resizer.FromConfig(config);
            //});
        }

        [Fact]
        public void Resizer_fromConfig_must_return_None_if_neither_resizer_is_enabled_which_is_default()
        {
            Resizer.FromConfig(Config.Empty).Should().BeNull();
        }

        [Fact]
        public void DefaultResizer_must_use_settings_to_evaluate_capacity()
        {
            var resizer = new DefaultResizer(lower: 2, upper: 3);
            var c1 = resizer.Capacity(Enumerable.Empty<Routee>());
            c1.Should().Be(2);

            var current = new Routee[]
            {
                new ActorRefRoutee(Sys.ActorOf<ResizerTestActor>()),
                new ActorRefRoutee(Sys.ActorOf<ResizerTestActor>())
            };

            var c2 = resizer.Capacity(current);
            c2.Should().Be(0);
        }

        [Fact]
        public void DefaultResizer_must_use_settings_to_evaluate_rampup()
        {
            var resizer = new DefaultResizer(lower: 2, upper: 10, rampupRate: 0.2);

            resizer.Rampup(pressure: 9, capacity: 10).Should().Be(0);
            resizer.Rampup(pressure: 5, capacity: 5).Should().Be(1);
            resizer.Rampup(pressure: 6, capacity: 6).Should().Be(2);
        }

        [Fact]
        public void DefaultResizer_must_use_settings_to_evaluate_backoff()
        {
            var resizer = new DefaultResizer(lower: 2, upper: 10, backoffThreshold: 0.3d, backoffRate: 0.1d);

            resizer.Backoff(pressure: 10, capacity: 10).Should().Be(0);
            resizer.Backoff(pressure: 4, capacity: 10).Should().Be(0);
            resizer.Backoff(pressure: 3, capacity: 10).Should().Be(0);
            resizer.Backoff(pressure: 2, capacity: 10).Should().Be(-1);
            resizer.Backoff(pressure: 0, capacity: 10).Should().Be(-1);
            resizer.Backoff(pressure: 1, capacity: 9).Should().Be(-1);
            resizer.Backoff(pressure: 0, capacity: 9).Should().Be(-1);
        }

        [Fact]
        public async Task DefaultResizer_must_be_possible_to_define_programmatically()
        {
            var latch = new TestLatch(3);
            var resizer = new DefaultResizer(lower: 2, upper: 3);

            var router = Sys.ActorOf(new RoundRobinPool(0, resizer).Props(Props.Create<ResizerTestActor>()));

            router.Tell(latch);
            router.Tell(latch);
            router.Tell(latch);

            latch.Ready(RemainingOrDefault);

            // MessagesPerResize is 10 so there is no risk of additional resize
            (await RouteeSize(router)).Should().Be(2);
        }

        [Fact]
        public async Task DefaultResizer_must_be_possible_to_define_in_configuration()
        {
            var latch = new TestLatch(3);
            var router = Sys.ActorOf(FromConfig.Instance.Props(Props.Create<ResizerTestActor>()), "router1");

            router.Tell(latch);
            router.Tell(latch);
            router.Tell(latch);

            latch.Ready(RemainingOrDefault);

            (await RouteeSize(router)).Should().Be(2);
        }

        [Fact(Skip = "Racy due to Resizer / Mailbox impl")]
        public async Task DefaultResizer_must_grow_as_needed_under_pressure()
        {
            var resizer = new DefaultResizer(
                lower: 3,
                upper: 5,
                rampupRate: 0.1,
                backoffRate: 0.0,
                pressureThreshold: 1,
                messagesPerResize: 1,
                backoffThreshold: 0.0);

            var router = Sys.ActorOf(new RoundRobinPool(0, resizer).Props(Props.Create<PressureActor>()));

            // first message should create the minimum number of routees
            router.Tell("echo");
            await ExpectMsgAsync("reply");

            (await RouteeSize(router)).Should().Be(resizer.LowerBound);

            Func<int, TimeSpan, Task> loop = async (loops, d) =>
            {
                for (var i = 0; i < loops; i++)
                {
                    router.Tell(d);

                    //sending too quickly will result in skipped resize due to many ResizeInProgress conflicts
                    await Task.Delay(Dilated(20.Milliseconds()));
                }

                double max = d.TotalMilliseconds * loops / resizer.LowerBound + Dilated(2.Seconds()).TotalMilliseconds;
                await WithinAsync(TimeSpan.FromMilliseconds(max), async() =>
                {
                    for (var i = 0; i < loops; i++)
                    {
                        await ExpectMsgAsync("done");
                    }
                });
            };

            // 2 more should go through without triggering more
            await loop(2, 200.Milliseconds());
            (await RouteeSize(router)).Should().Be(resizer.LowerBound);

            // a whole bunch should max it out
            await loop(20, 500.Milliseconds());
            (await RouteeSize(router)).Should().Be(resizer.UpperBound);
        }

        [Fact]
        public async Task DefaultResizer_with_ReceiveAsync_must_grow_as_needed_under_pressure()
        {
            var resizer = new DefaultResizer(
                lower: 3,
                upper: 5,
                rampupRate: 0.1,
                backoffRate: 0.0,
                pressureThreshold: 1,
                messagesPerResize: 1,
                backoffThreshold: 0.0);

            var router = Sys.ActorOf(new RoundRobinPool(0, resizer).Props(Props.Create<PressureAsyncActor>()));

            // first message should create the minimum number of routees
            router.Tell("echo");
            await ExpectMsgAsync("reply");

            (await RouteeSize(router)).Should().Be(resizer.LowerBound);

            async Task Loop(int loops, TimeSpan d)
            {
                for (var i = 0; i < loops; i++)
                {
                    router.Tell(d);

                    //sending too quickly will result in skipped resize due to many ResizeInProgress conflicts
                    await Task.Delay(Dilated(20.Milliseconds()));
                }

                var max = d.TotalMilliseconds * loops / resizer.LowerBound + Dilated(2.Seconds()).TotalMilliseconds;
                await WithinAsync(TimeSpan.FromMilliseconds(max), async () =>
                {
                    for (var i = 0; i < loops; i++)
                    {
                        await ExpectMsgAsync("done");
                    }
                });
            }

            // 2 more should go through without triggering more
            await Loop(2, 200.Milliseconds());
            (await RouteeSize(router)).Should().Be(resizer.LowerBound);

            // a whole bunch should max it out
            await Loop(20, 500.Milliseconds());
            (await RouteeSize(router)).Should().Be(resizer.UpperBound);
        }
        
        [Fact(Skip = "Racy due to Resizer / Mailbox impl")]
        public async Task DefaultResizer_must_backoff()
        {
            await WithinAsync(10.Seconds(), async () =>
            {
                var resizer = new DefaultResizer(
                    lower: 2,
                    upper: 5,
                    rampupRate: 1.0d,
                    backoffRate: 1.0d,
                    backoffThreshold: 0.40d,
                    pressureThreshold: 1,
                    messagesPerResize: 2);

                var router = Sys.ActorOf(new RoundRobinPool(nrOfInstances: 0, resizer : resizer)
                    .Props(Props.Create(() => new BackoffActor(Dilated))));

                // put some pressure on the router
                for (var i = 0; i < 15; i++)
                {
                    router.Tell(150);

                    await Task.Delay(Dilated(20.Milliseconds()));
                }

                var z = await RouteeSize(router);
                z.Should().BeGreaterThan(2);

                await Task.Delay(Dilated(300.Milliseconds()));

                // let it cool down
                await AwaitConditionAsync(async () =>
                {
                    router.Tell(0); //trigger resize
                    await Task.Delay(Dilated(20.Milliseconds()));
                    return (await RouteeSize(router)) < z;
                }, Dilated(500.Milliseconds()));
            });
        }
    }
}
