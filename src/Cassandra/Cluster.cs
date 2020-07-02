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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.Collections;
using Cassandra.Connections;
using Cassandra.Connections.Control;
using Cassandra.Helpers;
using Cassandra.Requests;
using Cassandra.SessionManagement;
using Cassandra.Tasks;

namespace Cassandra
{
    /// <inheritdoc cref="ICluster" />
    public class Cluster : IInternalCluster
    {
        private const int Disposed = 10;
        private const int Initialized = 5;
        private const int Initializing = 1;

        private static readonly IPEndPoint DefaultContactPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042);

        internal static readonly Logger Logger = new Logger(typeof(Cluster));
        private readonly CopyOnWriteList<IInternalSession> _connectedSessions = new CopyOnWriteList<IInternalSession>();
        private readonly bool _implicitContactPoint = false;
        private volatile Exception _initException;
        private volatile bool _initialized = false;
        private readonly SemaphoreSlim _sessionCreateLock = new SemaphoreSlim(1, 1);
        private long _sessionCounter = -1;

        private readonly IInternalMetadata _internalMetadata;
        private readonly Metadata _lazyMetadata;
        private IReadOnlyList<ILoadBalancingPolicy> _loadBalancingPolicies;

        private readonly Task<IInternalMetadata> _initTask;

        private long _state = Cluster.Initializing;

        internal IInternalCluster InternalRef => this;

        IInternalMetadata IInternalCluster.InternalMetadata => _internalMetadata;

        private bool IsDisposed => Interlocked.Read(ref _state) == Cluster.Disposed;

        Exception IInternalCluster.InitException => _initException;

        /// <inheritdoc />
        ConcurrentDictionary<byte[], PreparedStatement> IInternalCluster.PreparedQueries { get; }
            = new ConcurrentDictionary<byte[], PreparedStatement>(new ByteArrayComparer());

        /// <summary>
        ///  Build a new cluster based on the provided initializer. <p> Note that for
        ///  building a cluster programmatically, Cluster.NewBuilder provides a slightly less
        ///  verbose shortcut with <link>NewBuilder#Build</link>. </p><p> Also note that that all
        ///  the contact points provided by <c>initializer</c> must share the same
        ///  port.</p>
        /// </summary>
        /// <param name="initializer">the Cluster.Initializer to use</param>
        /// <returns>the newly created Cluster instance </returns>
        public static Cluster BuildFrom(IInitializer initializer)
        {
            return BuildFrom(initializer, null, null);
        }

        internal static Cluster BuildFrom(IInitializer initializer, IReadOnlyList<object> nonIpEndPointContactPoints)
        {
            return BuildFrom(initializer, nonIpEndPointContactPoints, null);
        }

        internal static Cluster BuildFrom(IInitializer initializer, IReadOnlyList<object> nonIpEndPointContactPoints, Configuration config)
        {
            nonIpEndPointContactPoints = nonIpEndPointContactPoints ?? new object[0];
            return new Cluster(
                initializer.ContactPoints.Concat(nonIpEndPointContactPoints),
                config ?? initializer.GetConfiguration());
        }

        /// <summary>
        ///  Creates a new <link>Cluster.NewBuilder</link> instance. <p> This is a shortcut
        ///  for <c>new Cluster.NewBuilder()</c></p>.
        /// </summary>
        /// <returns>the new cluster builder.</returns>
        public static Builder Builder()
        {
            return new Builder();
        }

        /// <inheritdoc />
        public Configuration Configuration { get; }

        /// <inheritdoc />
        // ReSharper disable once ConvertToAutoProperty, reviewed
        bool IInternalCluster.ImplicitContactPoint => _implicitContactPoint;

        /// <inheritdoc />
        public IMetadata Metadata => _lazyMetadata;

        private Cluster(IEnumerable<object> contactPoints, Configuration configuration)
        {
            Configuration = configuration;

            var contactPointsList = contactPoints.ToList();
            if (contactPointsList.Count == 0)
            {
                Cluster.Logger.Info("No contact points provided, defaulting to {0}", Cluster.DefaultContactPoint);
                contactPointsList.Add(Cluster.DefaultContactPoint);
                _implicitContactPoint = true;
            }

            var parsedContactPoints = configuration.ContactPointParser.ParseContactPoints(contactPointsList);
            
            _internalMetadata = new InternalMetadata(this, configuration, parsedContactPoints);
            _lazyMetadata = new Metadata(_internalMetadata);

            _initTask = Task.Run(InitAsync);
        }

        /// <inheritdoc />
        Task<IInternalMetadata> IInternalCluster.TryInitAndGetMetadataAsync()
        {
            if (_initialized)
            {
                //It was already initialized
                return _initTask;
            }
            
            var currentState = Interlocked.Read(ref _state);
            if (currentState == Cluster.Disposed)
            {
                throw new ObjectDisposedException("This cluster object has been disposed.");
            }

            if (_initException != null)
            {
                //There was an exception that is not possible to recover from
                throw _initException;
            }

            return _initTask;
        }

        private async Task<IInternalMetadata> InitAsync()
        {
            Cluster.Logger.Info("Connecting to cluster using {0}", Cluster.GetAssemblyInfo());
            try
            {
                // Collect all policies in collections
                var loadBalancingPolicies = new HashSet<ILoadBalancingPolicy>(new ReferenceEqualityComparer<ILoadBalancingPolicy>());
                var speculativeExecutionPolicies = new HashSet<ISpeculativeExecutionPolicy>(new ReferenceEqualityComparer<ISpeculativeExecutionPolicy>());
                foreach (var options in Configuration.RequestOptions.Values)
                {
                    loadBalancingPolicies.Add(options.LoadBalancingPolicy);
                    speculativeExecutionPolicies.Add(options.SpeculativeExecutionPolicy);
                }

                _loadBalancingPolicies = loadBalancingPolicies.ToList();

                // Only abort the async operations when at least twice the time for ConnectTimeout per host passed
                var initialAbortTimeout = Configuration.SocketOptions.ConnectTimeoutMillis * 2 * _internalMetadata.Hosts.Count;
                initialAbortTimeout = Math.Max(initialAbortTimeout, Configuration.SocketOptions.MetadataAbortTimeout);
                var initTask = _lazyMetadata.TryInitializeAsync();
                try
                {
                    await initTask.WaitToCompleteAsync(initialAbortTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    var newEx = new TimeoutException(
                        "Cluster initialization was aborted after timing out. This mechanism is put in place to" +
                        " avoid blocking the calling thread forever. This usually caused by a networking issue" +
                        " between the client driver instance and the cluster. You can increase this timeout via " +
                        "the SocketOptions.ConnectTimeoutMillis config setting. This can also be related to deadlocks " +
                        "caused by mixing synchronous and asynchronous code.", ex);
                    _initException = new InitFatalErrorException(newEx);
                    initTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            _initException = new InitFatalErrorException(t.Exception.InnerException);
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously).Forget();
                    throw newEx;
                }
                
                // initialize the local datacenter provider
                Configuration.LocalDatacenterProvider.Initialize(this, _internalMetadata);

                // Initialize policies
                foreach (var lbp in loadBalancingPolicies)
                {
                    await lbp.InitializeAsync(Metadata).ConfigureAwait(false);
                }

                foreach (var sep in speculativeExecutionPolicies)
                {
                    await sep.InitializeAsync(Metadata).ConfigureAwait(false);
                }

                InitializeHostDistances();

                // Set metadata dependent options
                SetMetadataDependentOptions();

                _internalMetadata.Hosts.Added += _internalMetadata.OnHostAdded;
                _internalMetadata.Hosts.Removed += _internalMetadata.OnHostRemoved;
                _internalMetadata.Hosts.Up += OnHostUp;
                
                var previousState = Interlocked.CompareExchange(ref _state, Cluster.Initialized, Cluster.Initializing);
                if (previousState == Cluster.Disposed)
                {
                    await ShutdownInternalAsync().ConfigureAwait(false);
                    throw new ObjectDisposedException("Cluster instance was disposed before initialization finished.");
                }

                _initialized = true;
                
                Cluster.Logger.Info("Cluster Connected using binary protocol version: ["
                                    + Configuration.SerializerManager.CurrentProtocolVersion
                                    + "]");
                Cluster.Logger.Info("Cluster [" + _internalMetadata.ClusterName + "] has been initialized.");

                return _internalMetadata;
            }
            catch (NoHostAvailableException)
            {
                //No host available now, maybe later it can recover from
                throw;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                //There was an error that the driver is not able to recover from
                //Store the exception for the following times
                _initException = new InitFatalErrorException(ex);
                //Throw the actual exception for the first time
                throw;
            }
        }

        private void InitializeHostDistances()
        {
            foreach (var host in _internalMetadata.AllHosts())
            {
                InternalRef.RetrieveAndSetDistance(host);
            }
        }

        private static string GetAssemblyInfo()
        {
            var assembly = typeof(ISession).GetTypeInfo().Assembly;
            var info = FileVersionInfo.GetVersionInfo(assembly.Location);
            return $"{info.ProductName} v{info.FileVersion}";
        }

        IReadOnlyDictionary<IContactPoint, IEnumerable<IConnectionEndPoint>> IInternalCluster.GetResolvedEndpoints()
        {
            return _internalMetadata.ResolvedContactPoints;
        }

        /// <summary>
        /// Creates a new session on this cluster.
        /// </summary>
        public ISession Connect()
        {
            return Connect(Configuration.ClientOptions.DefaultKeyspace);
        }

        /// <summary>
        /// Creates a new session on this cluster.
        /// </summary>
        public Task<ISession> ConnectAsync()
        {
            return ConnectAsync(Configuration.ClientOptions.DefaultKeyspace);
        }

        /// <summary>
        /// Creates a new session on this cluster and using a keyspace an existing keyspace.
        /// </summary>
        /// <param name="keyspace">Case-sensitive keyspace name to use</param>
        public ISession Connect(string keyspace)
        {
            return TaskHelper.WaitToComplete(ConnectAsync(keyspace));
        }

        /// <summary>
        /// Creates a new session on this cluster and using a keyspace an existing keyspace.
        /// </summary>
        /// <param name="keyspace">Case-sensitive keyspace name to use</param>
        public async Task<ISession> ConnectAsync(string keyspace)
        {
            await _sessionCreateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsDisposed)
                {
                    throw new ObjectDisposedException("This cluster instance is disposed.");
                }

                var newSessionName = GetNewSessionName();
                var session =
                    Configuration.SessionFactory.CreateSession(this, keyspace, newSessionName);
                _connectedSessions.Add(session);
                Cluster.Logger.Info("Session connected ({0})", session.GetHashCode());
                return session;
            }
            finally
            {
                _sessionCreateLock.Release();
            }
        }

        private string GetNewSessionName()
        {
            var sessionCounter = GetAndIncrementSessionCounter();
            if (sessionCounter == 0 && Configuration.SessionName != null)
            {
                return Configuration.SessionName;
            }

            var prefix = Configuration.SessionName ?? Configuration.DefaultSessionName;
            return prefix + sessionCounter;
        }

        private long GetAndIncrementSessionCounter()
        {
            var newCounter = Interlocked.Increment(ref _sessionCounter);

            // Math.Abs just to avoid negative counters if it overflows
            return newCounter < 0 ? Math.Abs(newCounter) : newCounter;
        }

        private void SetMetadataDependentOptions()
        {
            if (_internalMetadata.IsDbaas)
            {
                Configuration.SetDefaultConsistencyLevel(ConsistencyLevel.LocalQuorum);
            }
        }

        /// <summary>
        /// Creates new session on this cluster, and sets it to default keyspace.
        /// If default keyspace does not exist then it will be created and session will be set to it.
        /// Name of default keyspace can be specified during creation of cluster object with <c>Cluster.Builder().WithDefaultKeyspace("keyspace_name")</c> method.
        /// </summary>
        /// <param name="replication">Replication property for this keyspace. To set it, refer to the <see cref="ReplicationStrategies"/> class methods.
        /// It is a dictionary of replication property sub-options where key is a sub-option name and value is a value for that sub-option.
        /// <p>Default value is <c>SimpleStrategy</c> with <c>'replication_factor' = 2</c></p></param>
        /// <param name="durableWrites">Whether to use the commit log for updates on this keyspace. Default is set to <c>true</c>.</param>
        /// <returns>a new session on this cluster set to default keyspace.</returns>
        public ISession ConnectAndCreateDefaultKeyspaceIfNotExists(Dictionary<string, string> replication = null, bool durableWrites = true)
        {
            var session = Connect(null);
            session.CreateKeyspaceIfNotExists(Configuration.ClientOptions.DefaultKeyspace, replication, durableWrites);
            session.ChangeKeyspace(Configuration.ClientOptions.DefaultKeyspace);
            return session;
        }

        bool IInternalCluster.AnyOpenConnections(Host host)
        {
            return _connectedSessions.Any(session => session.HasConnections(host));
        }

        public void Dispose()
        {
            Shutdown();
        }

        private async void OnHostUp(Host h)
        {
            try
            {
                if (!Configuration.QueryOptions.IsReprepareOnUp())
                {
                    return;
                }

                // We should prepare all current queries on the host
                await ReprepareAllQueries(h).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Cluster.Logger.Error(
                    "An exception was thrown when preparing all queries on a host ({0}) " +
                    "that came UP:" + Environment.NewLine + "{1}", h?.Address?.ToString(), ex.ToString());
            }
        }

        /// <inheritdoc />
        public void Shutdown(int timeoutMs = Timeout.Infinite)
        {
            ShutdownAsync(timeoutMs).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task ShutdownAsync(int timeoutMs = Timeout.Infinite)
        {
            var previousState = Interlocked.Exchange(ref _state, Cluster.Disposed);
            _initialized = false;

            IEnumerable<ISession> sessions;
            await _sessionCreateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                sessions = _connectedSessions.ClearAndGet();
            }
            finally
            {
                _sessionCreateLock.Release();
            }

            try
            {
                var tasks = new List<Task>();
                foreach (var s in sessions)
                {
                    tasks.Add(s.ShutdownAsync());
                }

                await Task.WhenAll(tasks).WaitToCompleteAsync(timeoutMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Cluster.Logger.Warning("Exception occured while disposing session instances: {0}", ex.ToString());
            }

            if (previousState != Cluster.Initialized)
            {
                return;
            }

            await ShutdownInternalAsync().ConfigureAwait(false);

            Cluster.Logger.Info("Cluster [" + _internalMetadata.ClusterName + "] has been shut down.");
        }

        private async Task ShutdownInternalAsync()
        {
            await _lazyMetadata.ShutdownAsync().ConfigureAwait(false);
            Configuration.Timer.Dispose();

            // Dispose policies
            var speculativeExecutionPolicies = new HashSet<ISpeculativeExecutionPolicy>(new ReferenceEqualityComparer<ISpeculativeExecutionPolicy>());
            foreach (var options in Configuration.RequestOptions.Values)
            {
                speculativeExecutionPolicies.Add(options.SpeculativeExecutionPolicy);
            }

            foreach (var sep in speculativeExecutionPolicies)
            {
                await sep.ShutdownAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        HostDistance IInternalCluster.RetrieveAndSetDistance(Host host)
        {
            var distance = _loadBalancingPolicies[0].Distance(Metadata, host);

            for (var i = 1; i < _loadBalancingPolicies.Count; i++)
            {
                var lbp = _loadBalancingPolicies[i];
                var lbpDistance = lbp.Distance(Metadata, host);
                if (lbpDistance < distance)
                {
                    distance = lbpDistance;
                }
            }

            host.SetDistance(distance);
            return distance;
        }

        /// <inheritdoc />
        async Task<PreparedStatement> IInternalCluster.PrepareAsync(
            IInternalSession session, string cqlQuery, string keyspace, IDictionary<string, byte[]> customPayload)
        {
            await InternalRef.TryInitAndGetMetadataAsync().ConfigureAwait(false);

            var serializer = _internalMetadata.SerializerManager.GetCurrentSerializer();
            var currentVersion = serializer.ProtocolVersion;
            if (!currentVersion.SupportsKeyspaceInRequest() && keyspace != null)
            {
                // Validate protocol version here and not at PrepareRequest level, as PrepareRequest can be issued
                // in the background (prepare and retry, prepare on up, ...)
                throw new NotSupportedException($"Protocol version {currentVersion} does not support" +
                                                " setting the keyspace as part of the PREPARE request");
            }
            var request = new PrepareRequest(serializer, cqlQuery, keyspace, customPayload);

            return await PrepareAsync(session, request).ConfigureAwait(false);
        }

        private async Task<PreparedStatement> PrepareAsync(IInternalSession session, PrepareRequest request)
        {
            var lbp = session.Cluster.Configuration.DefaultRequestOptions.LoadBalancingPolicy;
            var handler = InternalRef.Configuration.PrepareHandlerFactory.CreatePrepareHandler(Configuration.SerializerManager, this);
            var ps = await handler.PrepareAsync(
                request,
                session,
                lbp.NewQueryPlan(Metadata, session.Keyspace, null).GetEnumerator()).ConfigureAwait(false);
            var psAdded = InternalRef.PreparedQueries.GetOrAdd(ps.Id, ps);
            if (ps != psAdded)
            {
                PrepareHandler.Logger.Warning("Re-preparing already prepared query is generally an anti-pattern and will likely " +
                                              "affect performance. Consider preparing the statement only once. Query='{0}'", ps.Cql);
                ps = psAdded;
            }

            return ps;
        }

        private async Task ReprepareAllQueries(Host host)
        {
            ICollection<PreparedStatement> preparedQueries = InternalRef.PreparedQueries.Values;
            IEnumerable<IInternalSession> sessions = _connectedSessions;

            if (preparedQueries.Count == 0)
            {
                return;
            }

            // Get the first pool for that host that has open connections
            var pool = sessions.Select(s => s.GetExistingPool(host.Address)).Where(p => p != null).FirstOrDefault(p => p.HasConnections);
            if (pool == null)
            {
                PrepareHandler.Logger.Info($"Not re-preparing queries on {host.Address} as there wasn't an open connection to the node.");
                return;
            }

            PrepareHandler.Logger.Info($"Re-preparing {preparedQueries.Count} queries on {host.Address}");
            var tasks = new List<Task>(preparedQueries.Count);
            var handler = InternalRef.Configuration.PrepareHandlerFactory.CreateReprepareHandler();
            var serializer = Configuration.SerializerManager.GetCurrentSerializer();
            using (var semaphore = new SemaphoreSlim(64, 64))
            {
                foreach (var ps in preparedQueries)
                {
                    var request = new PrepareRequest(serializer, ps.Cql, ps.Keyspace, null);
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    tasks.Add(Task.Run(() => handler.ReprepareOnSingleNodeAsync(
                        new KeyValuePair<Host, IHostConnectionPool>(host, pool),
                        ps,
                        request,
                        semaphore,
                        true)));
                }

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PrepareHandler.Logger.Info(
                        "There was an error when re-preparing queries on {0}. " +
                        "The driver will re-prepare the queries individually the next time they are sent to this node. " +
                        "Exception: {1}",
                        host.Address,
                        ex);
                }
            }
        }
    }
}