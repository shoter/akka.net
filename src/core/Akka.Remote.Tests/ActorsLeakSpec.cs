﻿//-----------------------------------------------------------------------
// <copyright file="ActorsLeakSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2021 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2021 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Actor.Internal;
using Akka.Configuration;
using Akka.Remote.Transport;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using Akka.TestKit.TestActors;
using Xunit;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit.Abstractions;

namespace Akka.Remote.Tests
{
    public class ActorsLeakSpec : AkkaSpec
    {
        private static readonly Config Config = ConfigurationFactory.ParseString(@"
            akka.actor.provider = remote
            akka.loglevel = INFO
            akka.remote.dot-netty.tcp.applied-adapters = [trttl]
            akka.remote.dot-netty.tcp.hostname = 127.0.0.1
            akka.remote.log-lifecycle-events = on
            akka.remote.transport-failure-detector.heartbeat-interval = 1 s
            akka.remote.transport-failure-detector.acceptable-heartbeat-pause = 3 s
            akka.remote.quarantine-after-silence = 3 s
            akka.test.filter-leeway = 12 s
        ");

        public ActorsLeakSpec(ITestOutputHelper output) : base(Config, output)
        {
        }

        private static ImmutableList<IActorRef> Recurse(IActorRef @ref)
        {
            var empty = new List<IActorRef>();
            var list = empty;
            if (@ref is ActorRefWithCell wc)
            {
                var cell = wc.Underlying;
                switch (cell.ChildrenContainer)
                {
                    case TerminatingChildrenContainer _:
                    case TerminatedChildrenContainer _:
                    case EmptyChildrenContainer _:
                        list = empty;
                        break;
                    case NormalChildrenContainer n:
                        list = n.Children.Cast<IActorRef>().ToList();
                        break;
                }
            }

            return ImmutableList<IActorRef>.Empty.Add(@ref).AddRange(list.SelectMany(Recurse));
        }


        private static ImmutableList<IActorRef> CollectLiveActors(IActorRef root)
        {
            return Recurse(root);
        }

        private class StoppableActor : ReceiveActor
        {
            public StoppableActor()
            {
                Receive<string>(str => str.Equals("stop"), s =>
                {
                    Context.Stop(Self);
                });
            }
        }

        private void AssertActors(ImmutableHashSet<IActorRef> expected, ImmutableHashSet<IActorRef> actual)
        {
            expected.Should().BeEquivalentTo(actual);
        }

        [Fact]
        public async Task Remoting_must_not_leak_actors()
        {
            var actorRef = Sys.ActorOf(EchoActor.Props(this, true), "echo");
            var echoPath = new RootActorPath(RARP.For(Sys).Provider.DefaultAddress)/"user"/"echo";

            var targets = await Task.WhenAll(new[] { "/system/endpointManager", "/system/transports" }.Select(
                async x =>
                {
                    Sys.ActorSelection(x).Tell(new Identify(0));
                    return (await ExpectMsgAsync<ActorIdentity>()).Subject;
                }));

            var initialActors = targets.SelectMany(CollectLiveActors).ToImmutableHashSet();

            // Clean shutdown case
            for (var i = 1; i <= 3; i++)
            {
                var remoteSystem = ActorSystem.Create("remote",
                    ConfigurationFactory.ParseString("akka.remote.dot-netty.tcp.port = 0")
                        .WithFallback(Sys.Settings.Config));

                try
                {
                    var probe = CreateTestProbe(remoteSystem);
                    remoteSystem.ActorSelection(echoPath).Tell(new Identify(1), probe.Ref);
                    (await probe.ExpectMsgAsync<ActorIdentity>()).Subject.ShouldNotBe(null);
                }
                finally
                {
                    await ShutdownAsync(remoteSystem);
                }

                Assert.True(await remoteSystem.WhenTerminated.AwaitWithTimeout(TimeSpan.FromSeconds(10)));
            }

            // Quarantine an old incarnation case
            for (var i = 1; i <= 3; i++)
            {
                // always use the same address
                var remoteSystem = ActorSystem.Create("remote",
                    ConfigurationFactory.ParseString("akka.remote.dot-netty.tcp.port = 2553")
                        .WithFallback(Sys.Settings.Config));

                try
                {
                    var remoteAddress = RARP.For(remoteSystem).Provider.DefaultAddress;
                    remoteSystem.ActorOf(Props.Create(() => new StoppableActor()), "stoppable");

                    // the message from remote to local will cause inbound connection established
                    var probe = CreateTestProbe(remoteSystem);
                    remoteSystem.ActorSelection(echoPath).Tell(new Identify(1), probe.Ref);
                    (await probe.ExpectMsgAsync<ActorIdentity>()).Subject.ShouldNotBe(null);

                    var beforeQuarantineActors = targets.SelectMany(CollectLiveActors).ToImmutableHashSet();

                    // it must not quarantine the current connection
                    RARP.For(Sys)
                        .Provider.Transport.Quarantine(remoteAddress, AddressUidExtension.Uid(remoteSystem) + 1);

                    // the message from local to remote should reuse passive inbound connection
                    Sys.ActorSelection(new RootActorPath(remoteAddress) / "user" / "stoppable").Tell(new Identify(1));
                    (await ExpectMsgAsync<ActorIdentity>()).Subject.ShouldNotBe(null);

                    await AwaitAssertAsync(() =>
                    {
                        var afterQuarantineActors = targets.SelectMany(CollectLiveActors).ToImmutableHashSet();
                        AssertActors(beforeQuarantineActors, afterQuarantineActors);
                    }, TimeSpan.FromSeconds(10));
                }
                finally
                {
                    await ShutdownAsync(remoteSystem);
                }
                Assert.True(await remoteSystem.WhenTerminated.AwaitWithTimeout(TimeSpan.FromSeconds(10)));
            }

            // Missing SHUTDOWN case
            for (var i = 1; i <= 3; i++)
            {
                var remoteSystem = ActorSystem.Create("remote",
                    ConfigurationFactory.ParseString("akka.remote.dot-netty.tcp.port = 0")
                        .WithFallback(Sys.Settings.Config));
                var remoteAddress = RARP.For(remoteSystem).Provider.DefaultAddress;

                try
                {
                    var probe = CreateTestProbe(remoteSystem);
                    remoteSystem.ActorSelection(echoPath).Tell(new Identify(1), probe.Ref);
                    (await probe.ExpectMsgAsync<ActorIdentity>()).Subject.ShouldNotBe(null);

                    // This will make sure that no SHUTDOWN message gets through
                    Assert.True(await RARP.For(Sys).Provider.Transport.ManagementCommand(new ForceDisassociate(remoteAddress))
                            .AwaitWithTimeout(TimeSpan.FromSeconds(3)));
                }
                finally
                {
                    await ShutdownAsync(remoteSystem);
                }

                await EventFilter.Warning(contains: "Association with remote system").ExpectOneAsync(async () =>
                {
                    Assert.True(await remoteSystem.WhenTerminated.AwaitWithTimeout(TimeSpan.FromSeconds(10)));
                });
            }

            // Remote idle for too long case
            var idleRemoteSystem = ActorSystem.Create("remote",
                    ConfigurationFactory.ParseString("akka.remote.dot-netty.tcp.port = 0")
                        .WithFallback(Sys.Settings.Config));
            var idleRemoteAddress = RARP.For(idleRemoteSystem).Provider.DefaultAddress;

            idleRemoteSystem.ActorOf(Props.Create<StoppableActor>(), "stoppable");

            try
            {
                var probe = CreateTestProbe(idleRemoteSystem);

                idleRemoteSystem.ActorSelection(echoPath).Tell(new Identify(1), probe.Ref);
                (await probe.ExpectMsgAsync<ActorIdentity>()).Subject.ShouldNotBe(null);

                // Watch a remote actor - this results in system message traffic
                Sys.ActorSelection(new RootActorPath(idleRemoteAddress) / "user" / "stoppable").Tell(new Identify(1));
                var remoteActor = (await ExpectMsgAsync<ActorIdentity>()).Subject;
                Watch(remoteActor);
                remoteActor.Tell("stop");
                await ExpectTerminatedAsync(remoteActor);
                // All system messages have been acked now on this side

                // This will make sure that no SHUTDOWN message gets through
                Assert.True(await RARP.For(Sys).Provider.Transport.ManagementCommand(new ForceDisassociate(idleRemoteAddress))
                        .AwaitWithTimeout(TimeSpan.FromSeconds(3)));
            }
            finally
            {
                await ShutdownAsync(idleRemoteSystem);
            }

            await EventFilter.Warning(contains: "Association with remote system").ExpectOneAsync(async () =>
            {
                Assert.True(await idleRemoteSystem.WhenTerminated.AwaitWithTimeout(TimeSpan.FromSeconds(10)));
            });

            /*
             * Wait for the ReliableDeliverySupervisor to receive its "TooLongIdle" message,
             * which will throw a HopelessAssociation wrapped around a TimeoutException.
             */
            await EventFilter.Exception<TimeoutException>().ExpectOneAsync(() => { });

            await AwaitAssertAsync(() =>
            {
                AssertActors(initialActors, targets.SelectMany(CollectLiveActors).ToImmutableHashSet());
            }, 10.Seconds());
        }
    }
}

