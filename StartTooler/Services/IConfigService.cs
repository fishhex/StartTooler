using System.Threading.Tasks;

namespace StartTooler.Services;

public interface IConfigService
{
    Task<T?> GetAsync<T>(string key) where T : class;
    Task SetAsync<T>(string key, T value) where T : class;
    Task<T> GetOrCreateAsync<T>(string key) where T : class, new();
}
