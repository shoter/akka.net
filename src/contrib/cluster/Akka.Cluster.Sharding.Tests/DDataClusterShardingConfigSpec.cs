﻿//-----------------------------------------------------------------------
// <copyright file="DDataClusterShardingConfigSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2021 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2021 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using Akka.Configuration;
using Akka.DistributedData;
using Akka.DistributedData.Internal;
using Akka.DistributedData.Serialization;
using Akka.TestKit;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Cluster.Sharding.Tests
{
    /// <summary>
    /// Used to validate that https://github.com/akkadotnet/akka.net/issues/3529 works as expected
    /// </summary>
    public class DDataClusterShardingConfigSpec : AkkaSpec
    {
        private static Config SpecConfig =>
            ConfigurationFactory.ParseString(@"akka.actor.provider = ""Akka.Cluster.ClusterActorRefProvider, Akka.Cluster""
                akka.cluster.sharding.state-store-mode = ddata
                akka.remote.dot-netty.tcp.port = 0
            ");

        public DDataClusterShardingConfigSpec(ITestOutputHelper helper) : base(SpecConfig, output: helper)
        {
        }

        [Fact]
        public void Should_load_DData_serializers_when_enabled()
        {
            ClusterSharding.Get(Sys);

            var rmSerializer = Sys.Serialization.FindSerializerFor(WriteAck.Instance);
            rmSerializer.Should().BeOfType<ReplicatorMessageSerializer>();

            var rDSerializer = Sys.Serialization.FindSerializerFor(ORDictionary<string, GSet<string>>.Empty);
            rDSerializer.Should().BeOfType<ReplicatedDataSerializer>();
        }
    }
}
