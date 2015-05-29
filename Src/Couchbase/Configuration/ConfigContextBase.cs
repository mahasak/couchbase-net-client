using System.Threading;
using Common.Logging;
using Couchbase.Authentication.SASL;
using Couchbase.Configuration.Client;
using Couchbase.Configuration.Server.Serialization;
using Couchbase.Core;
using Couchbase.Core.Buckets;
using Couchbase.Core.Transcoders;
using Couchbase.IO;
using Couchbase.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Couchbase.Configuration
{
    /// <summary>
    /// Base class for configuration contexts. The configuration context is a class which maintains the internal
    /// state of the cluster and communicats with configuration providers to ensure that the state is up-to-date.
    /// </summary>
    internal abstract class ConfigContextBase : IConfigInfo
    {
        protected static readonly ILog Log = LogManager.GetLogger<ConfigContextBase>();
        protected IKeyMapper KeyMapper;
        private readonly DateTime _creationTime;
        private readonly ClientConfiguration _clientConfig;
        protected IDictionary<IPAddress, IServer> Servers = new Dictionary<IPAddress, IServer>();
        protected Func<IConnectionPool, IOStrategy> IOStrategyFactory;
        protected Func<PoolConfiguration, IPEndPoint, IConnectionPool> ConnectionPoolFactory;
        protected readonly Func<string, string, IOStrategy, ITypeTranscoder, ISaslMechanism> SaslFactory;
        protected IBucketConfig _bucketConfig;
        private bool _disposed;
        protected ReaderWriterLockSlim Lock = new ReaderWriterLockSlim();

        //for segregating the nodes into separate lists
        protected List<IServer> QueryNodes;
        protected List<IServer> ViewNodes;
        protected List<IServer> DataNodes;
        protected List<IServer> IndexNodes;

        public bool IsQueryCapable { get; set; }
        public bool IsViewCapable { get; set; }
        public bool IsDataCapable { get; set; }
        public bool IsIndexCapable { get; set; }

        protected ConfigContextBase(IBucketConfig bucketConfig, ClientConfiguration clientConfig,
            Func<IConnectionPool, IOStrategy> ioStrategyFactory,
            Func<PoolConfiguration, IPEndPoint, IConnectionPool> connectionPoolFactory,
            Func<string, string, IOStrategy, ITypeTranscoder, ISaslMechanism> saslFactory,
            ITypeTranscoder transcoder)
        {
            _bucketConfig = bucketConfig;
            _clientConfig = clientConfig;
            IOStrategyFactory = ioStrategyFactory;
            ConnectionPoolFactory = connectionPoolFactory;
            _creationTime = DateTime.Now;
            SaslFactory = saslFactory;
            Transcoder = transcoder;
        }

        protected ITypeTranscoder Transcoder { get; private set; }

        /// <summary>
        /// The time at which this configuration context has been created.
        /// </summary>
        public DateTime CreationTime
        {
            get { return _creationTime; }
        }

        /// <summary>
        /// The client configuration for a bucket.
        /// <remarks> See <see cref="IBucketConfig"/> for details.</remarks>
        /// </summary>
        public IBucketConfig BucketConfig
        {
            get { return _bucketConfig; }
            private set { _bucketConfig = value; }
        }

        /// <summary>
        /// The name of the Bucket that this configuration represents.
        /// </summary>
        public string BucketName
        {
            get { return BucketConfig.Name; }
        }

        /// <summary>
        /// The client configuration.
        /// </summary>
        public ClientConfiguration ClientConfig
        {
            get { return _clientConfig; }
        }

        /// <summary>
        /// The <see cref="BucketTypeEnum"/> that this configuration context is for.
        /// </summary>
        public BucketTypeEnum BucketType
        {
            get
            {
                BucketTypeEnum bucketType;
                if (!Enum.TryParse(BucketConfig.BucketType, true, out bucketType))
                {
                    throw new NullConfigException("BucketType is not defined");
                }
                return bucketType;
            }
        }

        /// <summary>
        /// The <see cref="NodeLocatorEnum"/> that this configuration is using.
        /// </summary>
        public NodeLocatorEnum NodeLocator
        {
            get
            {
                NodeLocatorEnum nodeLocator;
                if (!Enum.TryParse(BucketConfig.NodeLocator, true, out nodeLocator))
                {
                    throw new NullConfigException("NodeLocator is not defined");
                }
                return nodeLocator;
            }
        }

        /// <summary>
        /// Loads the most updated configuration creating any resources as needed.
        /// </summary>
        /// <param name="bucketConfig">The latest <see cref="IBucketConfig"/>
        /// that will drive the recreation if the configuration context.</param>
        /// <param name="force">True to force the reconfiguration.</param>
        public abstract void LoadConfig(IBucketConfig bucketConfig, bool force = false);

        /// <summary>
        /// Loads the most updated configuration creating any resources as needed. The <see cref="IBucketConfig"/>
        /// used by this method is passed into the CTOR.
        /// </summary>
        /// <remarks>This method should be called immediately after creation.</remarks>
        public abstract void LoadConfig();

        /// <summary>
        /// Gets the <see cref="IKeyMapper"/> instance associated with this <see cref="IConfigInfo"/>.
        /// </summary>
        /// <returns></returns>
        public IKeyMapper GetKeyMapper()
        {
            Log.Debug(m=>m("Getting KeyMapper for rev#{0} on thread {1}", BucketConfig.Rev, Thread.CurrentThread.ManagedThreadId));
            return KeyMapper;
        }

        /// <summary>
        /// Gets a random server instance from the underlying <see cref="IServer"/> collection.
        /// </summary>
        /// <returns></returns>
        public IServer GetServer()
        {
            const int maxAttempts = 7;
            var attempts = 0;

            try
            {
                Lock.EnterReadLock();
                if (!Servers.Any())
                {
                    throw new ServerUnavailableException();
                }

                IServer server;
                do
                {
                    server = Servers.Values.Where(x => !x.IsDead).GetRandom();

                    //cannot find a server - usually a temp state
                    if (server == null)
                    {
                        try
                        {
                            Lock.ExitReadLock();
                            var sleepTime = (int)Math.Pow(2, attempts);
                            Thread.Sleep(sleepTime);
                        }
                        finally
                        {
                            Lock.EnterReadLock();
                        }
                    }
                    else
                    {
                        break;
                    }
                } while (attempts++ < maxAttempts);
                if (server == null)
                {
                    throw new ServerUnavailableException();
                }
                return server;
            }
            finally
            {
                Lock.ExitReadLock();
            }
        }

        List<IServer> IConfigInfo.Servers
        {
            get
            {
                try
                {
                    Lock.EnterReadLock();
                    return Servers.Values.ToList();
                }
                finally
                {
                    Lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Reclaims all resources and suppresses finalization.
        /// </summary>
        public void Dispose()
        {
            Log.Debug(m => m("Disposing ConfigContext"));
            Dispose(true);
        }

        /// <summary>
        /// Reclams all resources and optionally suppresses finalization.
        /// </summary>
        /// <param name="disposing">True to suppress finalization.</param>
        private void Dispose(bool disposing)
        {
            try
            {
                Lock.EnterWriteLock();
                if (_disposed) return;
                if (disposing)
                {
                    GC.SuppressFinalize(this);
                }
                if (Servers != null)
                {
                    foreach (var server in Servers)
                    {
                        server.Value.Dispose();
                    }
                    Servers.Clear();
                }
                _disposed = true;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

#if DEBUG
        /// <summary>
        /// Reclaims all un-reclaimed resources.
        /// </summary>
        ~ConfigContextBase()
        {
            Log.Debug(m => m("Finalizing ConfigContext for Rev#{0}", BucketConfig.Rev));
            Dispose(false);
        }
#endif

        public bool SslConfigured
        {
            get { return _bucketConfig.UseSsl || _clientConfig.UseSsl; }
        }

        /// <summary>
        /// Gets a data node from the Servers collection.
        /// </summary>
        /// <returns></returns>
        public IServer GetDataNode()
        {
            return DataNodes.GetRandom();
        }

        /// <summary>
        /// Gets a query node from the Servers collection.
        /// </summary>
        /// <returns></returns>
        public IServer GetQueryNode()
        {
            return QueryNodes.GetRandom();
        }

        /// <summary>
        /// Gets a index node from the Servers collection.
        /// </summary>
        /// <returns></returns>
        public IServer GetIndexNode()
        {
            return IndexNodes.GetRandom();
        }

        /// <summary>
        /// Gets a view node from the Servers collection.
        /// </summary>
        /// <returns></returns>
        public IServer GetViewNode()
        {
            return ViewNodes.GetRandom();
        }
    }
}

#region [ License information          ]

/* ************************************************************
 *
 *    @author Couchbase <info@couchbase.com>
 *    @copyright 2014 Couchbase, Inc.
 *
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *
 *        http://www.apache.org/licenses/LICENSE-2.0
 *
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *
 * ************************************************************/

#endregion
