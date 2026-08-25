namespace Zeus;

/// <summary>
/// 宿主级 JSON 协议绑定目录。不同宿主可以拥有不同协议插件集合。
/// </summary>
public interface IZeusJsonBinderRegistry
{
    /// <summary>当前已登记绑定的快照。</summary>
    IReadOnlyList<IZeusJsonBinder> All { get; }

    /// <summary>登记一个绑定。相同类型只登记一次。</summary>
    void Register(IZeusJsonBinder binder);

    /// <summary>按设备类型查找绑定。</summary>
    IZeusJsonBinder? FindDevice(string normalizedType);

    /// <summary>按虚拟从站类型查找绑定。</summary>
    IZeusJsonBinder? FindResponder(string normalizedResponder);
}

/// <summary>
/// 默认 JSON 协议绑定目录。
/// </summary>
public sealed class ZeusJsonBinderRegistry : IZeusJsonBinderRegistry
{
    private readonly object _gate = new();
    private readonly List<IZeusJsonBinder> _binders = [];

    /// <summary>创建绑定目录，并导入当前静态探测到的官方绑定。</summary>
    public ZeusJsonBinderRegistry()
    {
        foreach (var binder in ZeusJsonBinders.All)
        {
            Register(binder);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IZeusJsonBinder> All
    {
        get
        {
            lock (_gate)
            {
                return _binders.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Register(IZeusJsonBinder binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        lock (_gate)
        {
            if (_binders.Any(existing => ReferenceEquals(existing, binder) || existing.GetType() == binder.GetType()))
            {
                return;
            }

            _binders.Add(binder);
        }
    }

    /// <inheritdoc />
    public IZeusJsonBinder? FindDevice(string normalizedType)
    {
        lock (_gate)
        {
            return _binders.FirstOrDefault(binder => binder.DeviceTypes.Any(type =>
                string.Equals(type, normalizedType, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <inheritdoc />
    public IZeusJsonBinder? FindResponder(string normalizedResponder)
    {
        lock (_gate)
        {
            return _binders.FirstOrDefault(binder => binder.ResponderTypes.Any(type =>
                string.Equals(type, normalizedResponder, StringComparison.OrdinalIgnoreCase)));
        }
    }
}

