using System;
using System.Threading.Tasks;

namespace CoreFtp.Infrastructure.Caching;

public interface ICache
{
    void Add<T>(string key, T value, TimeSpan timespan) where T : class;
    T Get<T>(string key) where T : class;
    T GetOrSet<T>(string key, Func<T> expression, TimeSpan expiresIn) where T : class;
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> expression, TimeSpan expiresIn) where T : class;
    bool HasKey(string key);
    void Remove(string key);
}
