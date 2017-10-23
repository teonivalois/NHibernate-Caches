#region License

//
//  RtMemoryCache - A cache provider for NHibernate using System.Runtime.Caching.MemoryCache.
//
//  This library is free software; you can redistribute it and/or
//  modify it under the terms of the GNU Lesser General Public
//  License as published by the Free Software Foundation; either
//  version 2.1 of the License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
//

#endregion

using System;
using System.Collections;
using System.Runtime.Caching;
using NHibernate.Cache;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NHibernate.Util;

namespace NHibernate.Caches.RtMemoryCache
{
	/// <summary>
	/// Pluggable cache implementation using the System.Runtime.Caching classes
	/// </summary>
	public class RtMemoryCache : ICache
	{
		private static readonly IInternalLogger log = LoggerProvider.LoggerFor(typeof(RtMemoryCache));
		private readonly string region;
		private string regionPrefix;
		private readonly ObjectCache cache;
		private TimeSpan expiration;
		private bool _useSlidingExpiration;
		private CacheItemPriority priority;
		// The name of the cache key used to clear the cache. All cached items depend on this key.
		private readonly string rootCacheKey;
		private bool rootCacheKeyStored;
		private static readonly TimeSpan DefaultExpiration = TimeSpan.FromSeconds(300);
		private const bool DefaultUseSlidingExpiration = false;
		private static readonly string DefaultRegionPrefix = string.Empty;
		private const string CacheKeyPrefix = "NHibernate-Cache:";

		/// <summary>
		/// default constructor
		/// </summary>
		public RtMemoryCache()
			: this("nhibernate", null)
		{
		}

		/// <summary>
		/// constructor with no properties
		/// </summary>
		/// <param name="region"></param>
		public RtMemoryCache(string region)
			: this(region, null)
		{
		}

		/// <summary>
		/// full constructor
		/// </summary>
		/// <param name="region"></param>
		/// <param name="properties">cache configuration properties</param>
		/// <remarks>
		/// There are two (2) configurable parameters:
		/// <ul>
		///		<li>expiration = number of seconds to wait before expiring each item</li>
		///		<li>priority = a numeric cost of expiring each item, where 1 is a low cost, 5 is the highest, and 3 is normal. Only values 1 through 5 are valid.</li>
		/// </ul>
		/// All parameters are optional. The defaults are an expiration of 300 seconds and the default priority of 3.
		/// </remarks>
		/// <exception cref="IndexOutOfRangeException">The "priority" property is not between 1 and 5</exception>
		/// <exception cref="ArgumentException">The "expiration" property could not be parsed.</exception>
		public RtMemoryCache(string region, IDictionary<string, string> properties)
		{
			this.region = region;
			cache = MemoryCache.Default;
			Configure(properties);

			rootCacheKey = GenerateRootCacheKey();
			StoreRootCacheKey();
		}

		public string Region
		{
			get { return region; }
		}

		public TimeSpan Expiration
		{
			get { return expiration; }
		}

		public CacheItemPriority Priority
		{
			get { return priority; }
		}

		private void Configure(IDictionary<string, string> props)
		{
			if (props == null)
			{
				if (log.IsWarnEnabled)
				{
					log.Warn("configuring cache with default values");
				}
				expiration = DefaultExpiration;
				_useSlidingExpiration = DefaultUseSlidingExpiration;
				priority = CacheItemPriority.Default;
				regionPrefix = DefaultRegionPrefix;
			}
			else
			{
				priority = GetPriority(props);
				expiration= GetExpiration(props);
				_useSlidingExpiration = GetUseSlidingExpiration(props);
				regionPrefix= GetRegionPrefix(props);
			}
		}

		private static string GetRegionPrefix(IDictionary<string, string> props)
		{
			string result;
			if (props.TryGetValue("regionPrefix", out result))
			{
				log.DebugFormat("new regionPrefix: {0}", result);
			}
			else
			{
				result = DefaultRegionPrefix;
				log.Debug("no regionPrefix value given, using defaults");
			}
			return result;
		}

		private static TimeSpan GetExpiration(IDictionary<string, string> props)
		{
			TimeSpan result = DefaultExpiration;
			string expirationString;
			if (!props.TryGetValue("expiration", out expirationString))
			{
				props.TryGetValue(Cfg.Environment.CacheDefaultExpiration, out expirationString);
			}

			if (expirationString != null)
			{
				try
				{
					int seconds = Convert.ToInt32(expirationString);
					result = TimeSpan.FromSeconds(seconds);
					log.DebugFormat("new expiration value: {0}", seconds);
				}
				catch (Exception ex)
				{
					log.ErrorFormat("error parsing expiration value '{0}'", expirationString);
					throw new ArgumentException($"could not parse expiration '{expirationString}' as a number of seconds", ex);
				}
			}
			else
			{
				if (log.IsDebugEnabled)
				{
					log.Debug("no expiration value given, using defaults");
				}
			}
			return result;
		}

		private static bool GetUseSlidingExpiration(IDictionary<string, string> props)
		{
			var sliding = PropertiesHelper.GetBoolean("cache.use_sliding_expiration", props, DefaultUseSlidingExpiration);
			log.DebugFormat("Use sliding expiration value: {0}", sliding);
			return sliding;
		}

		private static CacheItemPriority GetPriority(IDictionary<string, string> props)
		{
			CacheItemPriority result = CacheItemPriority.Default;
			string priorityString;
			if (props.TryGetValue("priority", out priorityString))
			{
				result = ConvertCacheItemPriorityFromXmlString(priorityString);
				log.DebugFormat("new priority: {0}", result);
			}
			return result;
		}


		private static CacheItemPriority ConvertCacheItemPriorityFromXmlString(string priorityString)
		{
			if(string.IsNullOrEmpty(priorityString))
			{
				return CacheItemPriority.Default;
			}
			var ps = priorityString.Trim().ToLowerInvariant();
			if(ps.Length == 1 && char.IsDigit(priorityString,0))
			{
				// the priority is specified as a number
				int priorityAsInt = int.Parse(ps);
				if(priorityAsInt >= 1 && priorityAsInt <=6)
				{
					return (CacheItemPriority) priorityAsInt;
				}
			}
			else
			{
				switch (ps)
				{
					case "default":
						return CacheItemPriority.Default;
					case "notremovable":
						return CacheItemPriority.NotRemovable;
				}
			}
			log.ErrorFormat("priority value out of range: {0}", priorityString);
			throw new IndexOutOfRangeException("Priority must be a valid System.Runtime.Caching.CacheItemPriority; was: " + priorityString);
		}

		private string GetCacheKey(object key)
		{
			return String.Concat(CacheKeyPrefix, regionPrefix, region, ":", key.ToString(), "@", key.GetHashCode());
		}

		public object Get(object key)
		{
			if (key == null)
			{
				return null;
			}
			string cacheKey = GetCacheKey(key);
			log.DebugFormat("Fetching object '{0}' from the cache.", cacheKey);

			object obj = cache.Get(cacheKey);
			if (obj == null)
			{
				return null;
			}

			var de = (DictionaryEntry) obj;
			if (key.Equals(de.Key))
			{
				return de.Value;
			}
			else
			{
				return null;
			}
		}

		public void Put(object key, object value)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key), "null key not allowed");
			}
			if (value == null)
			{
				throw new ArgumentNullException(nameof(value), "null value not allowed");
			}
			string cacheKey = GetCacheKey(key);
			if (cache[cacheKey] != null)
			{
				log.DebugFormat("updating value of key '{0}' to '{1}'.", cacheKey, value);

				// Remove the key to re-add it again below
				cache.Remove(cacheKey);
			}
			else
			{
				log.DebugFormat("adding new data: key={0}&value={1}", cacheKey, value);
			}

			if (!rootCacheKeyStored)
			{
				StoreRootCacheKey();
			}

			cache.Add(cacheKey, new DictionaryEntry(key, value),
			          new CacheItemPolicy
			          {
			              AbsoluteExpiration = _useSlidingExpiration ? ObjectCache.InfiniteAbsoluteExpiration : DateTimeOffset.UtcNow.Add(expiration),
			              Priority = priority,
			              SlidingExpiration = _useSlidingExpiration ? expiration : ObjectCache.NoSlidingExpiration,
			              ChangeMonitors = {cache.CreateCacheEntryChangeMonitor(new[] {rootCacheKey})}
			          });
		}

		public void Remove(object key)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key));
			}
			string cacheKey = GetCacheKey(key);
			log.DebugFormat("removing item with key: {0}", cacheKey);
			cache.Remove(cacheKey);
		}

		public void Clear()
		{
			RemoveRootCacheKey();
			StoreRootCacheKey();
		}

		/// <summary>
		/// Generate a unique root key for all cache items to be dependant upon
		/// </summary>
		private string GenerateRootCacheKey()
		{
			return GetCacheKey(Guid.NewGuid());
		}

		private void RootCacheItemRemoved(CacheEntryRemovedArguments arguments)
		{
			rootCacheKeyStored = false;
		}

		private void StoreRootCacheKey()
		{
			rootCacheKeyStored = true;
			cache.Add(
				rootCacheKey,
				rootCacheKey,
				new CacheItemPolicy
				{
					AbsoluteExpiration = ObjectCache.InfiniteAbsoluteExpiration,
					SlidingExpiration = ObjectCache.NoSlidingExpiration,
					Priority = CacheItemPriority.Default,
					RemovedCallback = RootCacheItemRemoved
				});
		}

		private void RemoveRootCacheKey()
		{
			cache.Remove(rootCacheKey);
		}

		public void Destroy()
		{
			Clear();
		}

		public void Lock(object key)
		{
			// Do nothing
		}

		public void Unlock(object key)
		{
			// Do nothing
		}

		public long NextTimestamp()
		{
			return Timestamper.Next();
		}

		public int Timeout
		{
			get { return Timestamper.OneMs * 60000; } // 60 seconds
		}

		public string RegionName
		{
			get { return region; }
		}

		#region ICache async methods delegated to sync implementation

		public Task<object> GetAsync(object key, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return Task.FromCanceled<object>(cancellationToken);
			}
			return Task.FromResult(Get(key));
		}

		public Task PutAsync(object key, object value, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return Task.FromCanceled(cancellationToken);
			}
			Put(key, value);
			return Task.CompletedTask;
		}

		public Task RemoveAsync(object key, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return Task.FromCanceled(cancellationToken);
			}
			Remove(key);
			return Task.CompletedTask;
		}

		public Task ClearAsync(CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return Task.FromCanceled(cancellationToken);
			}
			Clear();
			return Task.CompletedTask;
		}

		public Task LockAsync(object key, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return Task.FromCanceled(cancellationToken);
			}
			Lock(key);
			return Task.CompletedTask;
		}

		public Task UnlockAsync(object key, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return Task.FromCanceled(cancellationToken);
			}
			Unlock(key);
			return Task.CompletedTask;
		}

		#endregion
	}
}