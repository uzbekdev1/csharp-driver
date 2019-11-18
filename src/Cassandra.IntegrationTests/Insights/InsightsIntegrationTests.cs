//
//      Copyright (C) DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cassandra.Insights.Schema;
using Cassandra.Insights.Schema.StartupMessage;
using Cassandra.Insights.Schema.StatusMessage;
using Cassandra.SessionManagement;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.IntegrationTests.TestClusterManagement.Simulacron;
using Cassandra.Tests;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Cassandra.IntegrationTests.Insights
{
    [TestFixture, Category("short")]
    public class InsightsIntegrationTests
    {
        private static object InsightsRpcPrime() => new
        {
            when = new
            {
                query = "CALL InsightsRpc.reportInsight(?)"
            },
            then = new
            {
                result = "void",
                delay_in_ms = 0
            }
        };

        private static readonly Guid clusterId = Guid.NewGuid();
        private static readonly string applicationName = "app 1";
        private static readonly string applicationVersion = "v1.2";

        private static Cluster BuildCluster(SimulacronCluster simulacronCluster, int statusEventDelay)
        {
            return Cluster.Builder()
                          .AddContactPoint(simulacronCluster.InitialContactPoint)
                          .WithApplicationName(InsightsIntegrationTests.applicationName)
                          .WithApplicationVersion(InsightsIntegrationTests.applicationVersion)
                          .WithClusterId(clusterId)
                          .WithSocketOptions(
                              new SocketOptions()
                                  .SetReadTimeoutMillis(5000)
                                  .SetConnectTimeoutMillis(10000))
                          .WithMonitorReporting(new MonitorReportingOptions().SetStatusEventDelayMilliseconds(statusEventDelay))
                          .Build();
        }

        [Test]
        [TestInsightsVersion]
        public void Should_InvokeInsightsRpcCall_When_SessionIsCreated()
        {
            using (var simulacronCluster = SimulacronCluster.CreateNew(new SimulacronOptions { IsDse = true, Nodes = "3" }))
            {
                simulacronCluster.Prime(InsightsIntegrationTests.InsightsRpcPrime());
                using (var cluster = InsightsIntegrationTests.BuildCluster(simulacronCluster, 500))
                {
                    Assert.AreEqual(0, simulacronCluster.GetQueries("CALL InsightsRpc.reportInsight(?)").Count);
                    var session = (IInternalSession)cluster.Connect();
                    dynamic query = null;
                    TestHelper.RetryAssert(
                        () =>
                        {
                            query = simulacronCluster.GetQueries("CALL InsightsRpc.reportInsight(?)").FirstOrDefault();
                            Assert.IsNotNull(query);
                        },
                        5,
                        1000);
                    string json = string.Empty;
                    Insight<InsightsStartupData> message = null;
                    try
                    {
                        json = Encoding.UTF8.GetString(
                            Convert.FromBase64String(
                                (string) query.frame.message.options.positional_values[0].Value));
                        message = JsonConvert.DeserializeObject<Insight<InsightsStartupData>>(json);
                    }
                    catch (JsonReaderException ex)
                    {
                        Assert.Fail("failed to deserialize json: " + ex.Message + Environment.NewLine + json);
                    }

                    Assert.IsNotNull(message);
                    Assert.AreEqual(InsightType.Event, message.Metadata.InsightType);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message.Metadata.InsightMappingId));
                    Assert.AreEqual("driver.startup", message.Metadata.Name);
                    Assert.AreEqual(InsightsIntegrationTests.applicationName, message.Data.ApplicationName);
                    Assert.AreEqual(false, message.Data.ApplicationNameWasGenerated);
                    Assert.AreEqual(InsightsIntegrationTests.applicationVersion, message.Data.ApplicationVersion);
                    Assert.AreEqual(InsightsIntegrationTests.clusterId.ToString(), message.Data.ClientId);
                    Assert.AreEqual(session.InternalSessionId.ToString(), message.Data.SessionId);
                    Assert.Greater(message.Data.PlatformInfo.CentralProcessingUnits.Length, 0);
#if NETCORE
                    if (TestHelper.IsWin)
                    {
                        Assert.IsNull(message.Data.PlatformInfo.CentralProcessingUnits.Model);
                    }
                    else
                    {
                        Assert.IsFalse(string.IsNullOrWhiteSpace(message.Data.PlatformInfo.CentralProcessingUnits.Model));
                    }
#else
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message.Data.PlatformInfo.CentralProcessingUnits.Model));
#endif
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message.Data.PlatformInfo.OperatingSystem.Version));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message.Data.PlatformInfo.OperatingSystem.Arch));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message.Data.PlatformInfo.OperatingSystem.Name));
                    Assert.IsFalse(message.Data.PlatformInfo.Runtime.Dependencies.Any(s => string.IsNullOrWhiteSpace(s.Value.FullName)));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message.Data.PlatformInfo.Runtime.RuntimeFramework));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message.Data.PlatformInfo.Runtime.TargetFramework));
                }
            }
        }
        
        [Test]
        [TestInsightsVersion]
        public void Should_InvokeInsightsRpcCallPeriodically_When_SessionIsCreatedAndEventDelayPasses()
        {
            using (var simulacronCluster = SimulacronCluster.CreateNew(new SimulacronOptions { IsDse = true, Nodes = "3" }))
            {
                simulacronCluster.Prime(InsightsIntegrationTests.InsightsRpcPrime());
                using (var cluster = InsightsIntegrationTests.BuildCluster(simulacronCluster, 50))
                {
                    Assert.AreEqual(0, simulacronCluster.GetQueries("CALL InsightsRpc.reportInsight(?)").Count);
                    var session = (IInternalSession) cluster.Connect();
                    IList<dynamic> queries = null;
                    TestHelper.RetryAssert(
                        () =>
                        {
                            queries = simulacronCluster.GetQueries("CALL InsightsRpc.reportInsight(?)");
                            var queryCount = queries.Count;
                            Assert.GreaterOrEqual(queryCount, 5);
                        },
                        250,
                        40);
                    
                    
                    string json = string.Empty;
                    Insight<InsightsStatusData> message = null;
                    try
                    {
                        json = Encoding.UTF8.GetString(
                            Convert.FromBase64String(
                                (string) queries[1].frame.message.options.positional_values[0].Value));
                        message = JsonConvert.DeserializeObject<Insight<InsightsStatusData>>(json);
                    }
                    catch (JsonReaderException ex)
                    {
                        // simulacron issue multiple queries of the same type but different data causes data corruption
                        Assert.Inconclusive("failed to deserialize json (probably due to simulacron bug) : " + ex.Message + Environment.NewLine + json);
                    }
                    Assert.IsNotNull(message);
                    Assert.AreEqual(InsightType.Event, message.Metadata.InsightType);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message.Metadata.InsightMappingId));
                    Assert.AreEqual("driver.status", message.Metadata.Name);
                    Assert.AreEqual(InsightsIntegrationTests.clusterId.ToString(), message.Data.ClientId);
                    Assert.AreEqual(session.InternalSessionId.ToString(), message.Data.SessionId);
                }
            }
        }
    }
}