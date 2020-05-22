using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace jiangyi1985
{
    /// <summary>
    /// A Memory key/value cache that:
    /// 1. load data on first access (thread block)
    /// 2. automatically refresh data when cache entry expired (in async mono thread)
    /// Usage:
    /// 1. one class calling TryRegisterNewCacheEntry first to register ValueFactory and then other classes calling Get to get value
    /// 2. every class calling GetOrRegisterNew to get value or register ValueFactory
    ///    (only the first guy that register will successfully register the ValueFactory, other registration action will be ignored)
    /// Note:
    /// 1.first load thread-block
    /// 2.refreshing triggered only when cache hit/accessed
    /// 3.cache entry need to be added first by calling register method
    /// 4.expected_minimum_cache_updating_period (when under constant requesting) = load_data_time + cache_entry_life_span
    /// </summary>
    public class AutoRefreshCache
    {
        private AutoRefreshCache() { }
        private static readonly Lazy<AutoRefreshCache> Lazy = new Lazy<AutoRefreshCache>(() => new AutoRefreshCache());

        public static AutoRefreshCache Instance => Lazy.Value;

        private readonly Dictionary<string, CacheItem> _dic = new Dictionary<string, CacheItem>();
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        public int Count => _dic.Count;

        public string Status
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var pair in _dic)
                {
                    sb.Append(
                        pair.Key + " " + pair.Value.Status + " " + pair.Value.ExpireAt + " " + pair.Value.LifeSpan);
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Register a new cache entry with a value factory
        /// (this method holds a lock on the entire cache!!)
        /// (Throws when key already exists)
        /// </summary>
        private void RegisterNewCacheEntry(string key, Func<string, object> valueFactory, TimeSpan lifeSpan)
        {
            _lock.EnterWriteLock();
            try
            {
                _dic.Add(key, new CacheItem()
                {
                    LifeSpan = lifeSpan,
                    ValueFactory = valueFactory,

                    ExpireAt = DateTime.MinValue,
                    Status = InProcCacheItemStatus.Empty,
                    Value = null,
                });
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Try registering a new cache entry with a value factory that will be run when
        /// 1. the cache entry is empty
        /// 2. when the cache entry expires
        /// </summary>
        /// <param name="key">cache entry key</param>
        /// <param name="valueFactory">value factory that produce the cache entry value</param>
        /// <param name="lifeSpan">life span of the cache entry</param>
        /// <returns>true if entry added, false if key already exists</returns>
        public bool TryRegisterNewCacheEntry(string key, Func<string, object> valueFactory, TimeSpan lifeSpan)
        {
            _lock.EnterReadLock();

            try
            {
                if (_dic.ContainsKey(key))
                    return false;

                _lock.ExitReadLock();
                try
                {
                    RegisterNewCacheEntry(key, valueFactory, lifeSpan);
                    return true;
                }
                catch (ArgumentException e)
                {
                    return false;
                }
            }
            finally { if (_lock.IsReadLockHeld) _lock.ExitReadLock(); }
        }

        public T Get<T>(string key)
        {
            return (T)Get(key);
        }

        public object Get(string key)
        {
            _lock.EnterReadLock();
            try
            {
                CacheItem cacheItem = _dic[key];

                switch (cacheItem.Status)
                {
                    case InProcCacheItemStatus.Empty:
                        _lock.ExitReadLock();
                        _lock.EnterWriteLock();

                        //load data (thread blocking)
                        try
                        {
                            if (cacheItem.Status == InProcCacheItemStatus.Empty)
                            {
                                cacheItem.Value = cacheItem.ValueFactory(key);
                                cacheItem.ExpireAt = DateTime.UtcNow.Add(cacheItem.LifeSpan);
                                cacheItem.Status = InProcCacheItemStatus.Healthy;
                            }
                        }
                        finally { _lock.ExitWriteLock(); }
                        break;

                    case InProcCacheItemStatus.Healthy:
                        if (DateTime.UtcNow >= cacheItem.ExpireAt) //expired
                        {
                            _lock.ExitReadLock();
                            _lock.EnterWriteLock();

                            //start mono-thread async task to load data 
                            try
                            {
                                if (cacheItem.Status != InProcCacheItemStatus.Refreshing)
                                {
                                    cacheItem.Status = InProcCacheItemStatus.Refreshing;

                                    //queue task
                                    Task.Factory.StartNew(() =>
                                    {
                                        //get data
                                        object newValue = null;
                                        bool hasException = false;
                                        try
                                        {
                                            newValue = cacheItem.ValueFactory(key);
                                        }
                                        catch (Exception e)
                                        {
                                            hasException = true;

                                            //write log here
                                        }

                                        //update cache
                                        _lock.EnterWriteLock();
                                        try
                                        {
                                            if (!hasException)
                                            {
                                                cacheItem.Value = newValue;
                                                cacheItem.ExpireAt = DateTime.UtcNow.Add(cacheItem.LifeSpan);
                                            }

                                            //no need to change cache status here because we are doing it in the finally clause
                                        }
                                        finally
                                        {
                                            //change the status from refreshing to healthy, even when there's an exception
                                            //because we want another thread to be able to trigger the refreshing process later
                                            cacheItem.Status = InProcCacheItemStatus.Healthy;

                                            _lock.ExitWriteLock();
                                        }
                                    });
                                }
                            }
                            finally { _lock.ExitWriteLock(); }
                        }
                        break;

                    case InProcCacheItemStatus.Refreshing:
                        //do nothing
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                return cacheItem.Value;
            }
            finally { if (_lock.IsReadLockHeld) _lock.ExitReadLock(); }
        }

        public T GetOrRegisterNew<T>(string key, Func<string, object> valueFactory, TimeSpan lifeSpan)
        {
            return (T)GetOrRegisterNew(key, valueFactory, lifeSpan);
        }

        public object GetOrRegisterNew(string key, Func<string, object> valueFactory, TimeSpan lifeSpan)
        {
            try
            {
                return Get(key);
            }
            catch (KeyNotFoundException)
            {
                TryRegisterNewCacheEntry(key,valueFactory,lifeSpan);

                return Get(key);
            }
        }

        //public string GetOrAdd(string key, Func<string, string> valueFactory, int periodicUpdateSeconds = 0)
        //{
        //    _lock.EnterReadLock();
        //    try
        //    {
        //        return _dic[key];
        //    }
        //    catch (KeyNotFoundException)
        //    {
        //        _lock.ExitReadLock();
        //        _lock.EnterWriteLock();
        //        try
        //        {
        //            if (!_dic.ContainsKey(key))
        //            {
        //                var value = valueFactory.Invoke(key);
        //                _dic.Add(key, value);
        //                return value;
        //            }
        //            else
        //            {
        //                return _dic[key];
        //            }
        //        }
        //        finally
        //        {
        //            _lock.ExitWriteLock();
        //        }
        //    }
        //    finally
        //    {
        //        if (_lock.IsReadLockHeld)
        //            _lock.ExitReadLock();
        //    }
        //}

        //public void Add(string key, string value)
        //{
        //    _lock.EnterWriteLock();
        //    try
        //    {
        //        _dic.Add(key, value);
        //    }
        //    finally
        //    {
        //        _lock.ExitWriteLock();
        //    }
        //}

        //public bool AddWithTimeout(string key, string value, int timeout)
        //{
        //    if (_lock.TryEnterWriteLock(timeout))
        //    {
        //        try
        //        {
        //            _dic.Add(key, value);
        //        }
        //        finally
        //        {
        //            _lock.ExitWriteLock();
        //        }
        //        return true;
        //    }
        //    else
        //    {
        //        return false;
        //    }
        //}

        //public AddOrUpdateStatus AddOrUpdate(string key, string value)
        //{
        //    _lock.EnterUpgradeableReadLock();
        //    try
        //    {
        //        string result = null;
        //        if (_dic.TryGetValue(key, out result))
        //        {
        //            if (result == value)
        //            {
        //                return AddOrUpdateStatus.Unchanged;
        //            }
        //            else
        //            {
        //                _lock.EnterWriteLock();
        //                try
        //                {
        //                    _dic[key] = value;
        //                }
        //                finally
        //                {
        //                    _lock.ExitWriteLock();
        //                }
        //                return AddOrUpdateStatus.Updated;
        //            }
        //        }
        //        else
        //        {
        //            _lock.EnterWriteLock();
        //            try
        //            {
        //                _dic.Add(key, value);
        //            }
        //            finally
        //            {
        //                _lock.ExitWriteLock();
        //            }
        //            return AddOrUpdateStatus.Added;
        //        }
        //    }
        //    finally
        //    {
        //        _lock.ExitUpgradeableReadLock();
        //    }
        //}

        //public void Delete(string key)
        //{
        //    _lock.EnterWriteLock();
        //    try
        //    {
        //        _dic.Remove(key);
        //    }
        //    finally
        //    {
        //        _lock.ExitWriteLock();
        //    }
        //}

        ~AutoRefreshCache()
        {
            _lock?.Dispose();
        }
    }

    enum InProcCacheItemStatus
    {
        /// <summary>
        /// cache entry is created but cache value is never populated
        /// </summary>
        Empty,

        /// <summary>
        /// cache value ready and not expired
        /// </summary>
        Healthy,

        /// <summary>
        /// cache value ready but expired, new value is being fetched
        /// </summary>
        Refreshing,
    }

    class CacheItem
    {
        public object Value { get; set; }
        public InProcCacheItemStatus Status { get; set; }
        public DateTime ExpireAt { get; set; }
        public TimeSpan LifeSpan { get; set; }
        public Func<string, object> ValueFactory { get; set; }
    }
}
