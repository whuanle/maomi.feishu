using System.Collections;
using System.Text;

namespace FeishuWss.Frames;

/// <summary>
/// Frame 帧的 Headers 字段。键值对集合，按插入顺序遍历。
/// 飞书长连接里很多头（如 biz_rt）需要在收到帧上直接追加，所以是可变的。
/// </summary>
public sealed class FrameHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _items = new();

    public int Count => _items.Count;

    public string GetString(string key)
    {
        foreach (var kv in _items)
        {
            if (kv.Key == key) return kv.Value;
        }
        return string.Empty;
    }

    public int GetInt(string key)
    {
        var s = GetString(key);
        return int.TryParse(s, out var v) ? v : 0;
    }

    public long GetLong(string key)
    {
        var s = GetString(key);
        return long.TryParse(s, out var v) ? 0L : v;
    }

    public void Add(string key, string value)
    {
        _items.Add(new KeyValuePair<string, string>(key, value ?? string.Empty));
    }

    public void Set(string key, string value)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Key == key)
            {
                _items[i] = new KeyValuePair<string, string>(key, value ?? string.Empty);
                return;
            }
        }
        Add(key, value);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString()
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in _items)
        {
            if (!first) sb.Append(", ");
            sb.Append(kv.Key).Append('=').Append(kv.Value);
            first = false;
        }
        return sb.Append('}').ToString();
    }
}
