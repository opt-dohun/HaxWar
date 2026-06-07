namespace HexWar.Application.Sessions;

using System.Collections;

/// <summary>
/// 고정 크기의 원형 버퍼입니다.
/// 버퍼가 가득 차면 가장 오래된 항목을 덮어씁니다.
/// Thread-safe합니다.
/// </summary>
// IEnumerable 인터페이스 구현하여 컬렉션처럼 사용 가능하도록 함.
// 큐와 유사하지만 고정된 용량을 가지며, 용량을 초과하면 오래된 데이터는 덮어쓰기되어 삭제됨 
public class CircularBuffer<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");

        _buffer = new T[capacity];
        _head = 0;
        _count = 0;
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            // index 번호가 _buffer를 넘지 않도록 mod 연산 진행
            _head = (_head + 1) % _buffer.Length;
            // _buffer 길이 cap을 초과하면 덮어써짐으로 _count를 증가시키지 않음
            if (_count < _buffer.Length) _count++;
        }
    }

    // Func<T, bool> 형태는 T 타입의 데이터를 매개변수로 받아서 bool을 반환하는 델리게이트임. 즉, T 타입의 데이터를 받아서 bool을 반환하는 메서드
    public List<T> Where(Func<T, bool> predicate)
    {
        lock (_lock)
        {

            var list = new List<T>();
            // 버퍼가 다 안 차있으면 시작 인덱스는 0, 다 차있으면 현재 헤드 인덱스가 시작 인덱스
            int start = _count < _buffer.Length ? 0 : _head;

            for (int i = 0; i < _count; i++)
            {
                int index = (start + i) % _buffer.Length;
                if (predicate(_buffer[index]))
                    list.Add(_buffer[index]);
            }
            return list;
        }
    }

    // GetEnumerator() 메서드는 IEnumerable<T> 인터페이스를 구현하기 위해 필요함.
    // 이 메서드는 버퍼의 모든 요소를 순회하는 이터레이터를 반환함.
    public IEnumerator<T> GetEnumerator()
    {
        lock (_lock)
        {
            int start = _count < _buffer.Length ? 0 : _head;
            for (int i = 0; i < _count; i++)
            {
                yield return _buffer[(start + i) % _buffer.Length];
            }
        }
    }

    // IEnumerable<T>가 아닌 IEnumerable<T>를 상속받지 않은 타입인 IEnumerable 인터페이스를 구현하기 위해 필요함.
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class BufferedEvent
{
    public long SequenceNumber { get; init; }
    public object Event { get; init; } = null!;
    public DateTime Timestamp { get; init; }
}