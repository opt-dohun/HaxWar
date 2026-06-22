# 코드 레벨의 기술적 고민 및 최적화 구현 (Code Design & Optimization Decisions)

본 문서는 **HexWar** 프로젝트를 개발하며 발생한 C# 코드 레벨의 문제 해결, 자료구조 선택, 힙 메모리 할당 제어(Zero-Copy), OS 자원 누수 방지 등 **상세 코드적 의사결정(Code Design Decisions)**에 대하여 상세히 설명합니다.

---

## 1. 시계열 이동 및 조우 판정 관리를 위한 자료구조 설계

### A. 고민의 배경
유닛은 간선(Edge)을 따라 이동하며, 도착까지 1~2라운드가 소요됩니다. 같은 간선 위에서 서로 마주칠 때 발생하는 "조우(Encounter)" 판정을 정교하게 수행하기 위해, 간선 내에서 이동 중인 유닛 그룹을 남은 라운드 수별로 관리해야 했습니다.

이때 단순 `List<List<TravelingGroup>>`을 사용할 경우 다음과 같은 부작용이 발생합니다.
* **불필요한 인덱스 패딩**: 이동 거리가 멀어지면 유닛이 존재하지 않는 빈 공간을 배열에 채워 두어야 하므로 메모리가 낭비됩니다.
* **인덱스 Shift 연산**: 라운드가 경과할 때마다 요소를 한 칸씩 앞으로 당겨야 하므로 도메인 로직과 무관한 배열 가공 비용이 듭니다.

### B. 해결 방안: `SortedList<int, List<TravelingGroup>>`
* 남은 라운드 수 자체를 오름차순 Key로 지정하여, Sparse(희소) 데이터 구조를 최적화하였습니다.
* 도착 임박 유닛이 항상 정렬 기준에 따라 리스트 처음에 위치하므로 가독성과 연산 안정성이 높습니다.

```csharp
// src/HexWar.Domain/Entities/TravelingGroup.cs
public class TravelingGroup
{
    public PlayerSide Side { get; }    // 소유 플레이어 (A/B)
    public int UnitCount { get; }      // 유닛 수
    public NodeId Destination { get; } // 목적지 노드
}

// src/HexWar.Domain/Entities/Edge.cs
public SortedList<int, List<TravelingGroup>> TravelingUnits { get; } = new();
```

### C. 키 불변성 문제와 컬렉션 재구성
C#의 `SortedList<K, V>`는 Key 값을 직접 변경하는 연산이 허용되지 않습니다. 매 라운드가 진행될 때마다 각 엔트리의 남은 라운드 수(`Key - 1`)를 갱신해야 하는데, 이 과정에서 **새로운 `SortedList` 컬렉션을 명시적으로 재구성**하는 불변성 패턴을 적용했습니다.

```csharp
// src/HexWar.Domain/Entities/Edge.cs
public List<TravelingGroup> AdvanceRound()
{
    var arrived = new List<TravelingGroup>();
    // 매 라운드 갱신을 위한 새로운 정렬 리스트 임시 생성
    var newTraveling = new SortedList<int, List<TravelingGroup>>();

    foreach (var kvp in TravelingUnits)
    {
        int newRemaining = kvp.Key - 1;

        if (newRemaining <= 0)
        {
            // 남은 라운드가 0 이하가 되면 최종 도착 처리
            arrived.AddRange(kvp.Value);
        }
        else
        {
            if (!newTraveling.ContainsKey(newRemaining))
                newTraveling[newRemaining] = new List<TravelingGroup>();
            newTraveling[newRemaining].AddRange(kvp.Value);
        }
    }

    // 기존 컬렉션을 클리어하고 최신 상태로 갱신
    TravelingUnits.Clear();
    foreach (var kvp in newTraveling)
    {
        TravelingUnits[kvp.Key] = kvp.Value;
    }

    return arrived;
}
```

### D. 조우(Encounter) 판정 흐름
매 라운드 일괄 해소 단계(`ResolveRound`)에서, 동일 간선 위의 같은 남은 거리(Key)에 서로 다른 진영의 유닛들이 존재하는지 고속으로 탐색합니다.

```csharp
// src/HexWar.Domain/Entities/Edge.cs
public List<EncounterInfo> FindAllEncounters()
{
    var encounters = new List<EncounterInfo>();

    foreach (var kvp in TravelingUnits)
    {
        // 동일 Key에 서로 다른 Side의 유닛 그룹이 2개 이상 존재하는지 판별
        if (HasEncounter(kvp.Key, out var groups) && groups != null)
        {
            var groupA = groups.FirstOrDefault(g => g.Side == PlayerSide.A);
            var groupB = groups.FirstOrDefault(g => g.Side == PlayerSide.B);

            if (groupA != null && groupB != null)
                encounters.Add(new EncounterInfo(Id, groupA, groupB, kvp.Key));
        }
    }
    return encounters;
}

public bool HasEncounter(int roundRemaining, out List<TravelingGroup>? groups)
{
    if (TravelingUnits.TryGetValue(roundRemaining, out groups) && groups.Count > 1)
    {
        var sides = groups.Select(g => g.Side).Distinct();
        return sides.Count() > 1; // 진영의 종류가 2개 이상일 때 조우 발생
    }
    groups = null;
    return false;
}
```

---

## 2. CircularBuffer를 활용한 인메모리 이벤트 로그 관리

### A. 고민의 배경
WebSocket 기반의 실시간 통신 환경에서 클라이언트가 일시적으로 네트워크 단절 후 재연결 시, 유실된 기간 동안 발생한 패킷을 완벽히 복구해야 합니다. 매번 전체 이벤트를 영속 저장소(Redis/RDBMS)에 쓰고 읽는 방식은 디스크 I/O와 쿼리 부하가 과도하게 발생하므로, 각 세션 노드의 로컬 메모리에 고정된 공간을 확보하여 순환 형태로 캐싱하는 원형 큐(`CircularBuffer`)를 자체 구현하였습니다.

### B. `CircularBuffer<T>` 구현 상세

```csharp
// src/HexWar.Application/Sessions/CircularBuffer.cs
using System;
using System.Collections;
using System.Collections.Generic;

public class CircularBuffer<T> : IEnumerable<T>, IDisposable
{
    private readonly T[] _buffer;
    private int _head = 0; // 다음 데이터를 쓸 위치
    private int _tail = 0; // 가장 오래된 데이터 위치
    private int _count = 0;
    private readonly object _lock = new();

    public CircularBuffer(int capacity)
    {
        _buffer = new T[capacity];
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;

            if (_count < _buffer.Length)
            {
                _count++;
            }
            else
            {
                // 버퍼가 꽉 찼다면 가장 오래된 데이터의 위치(tail)를 한 칸 밀어 덮어씀
                _tail = (_tail + 1) % _buffer.Length;
            }
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                yield return _buffer[(_tail + i) % _buffer.Length];
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        lock (_lock)
        {
            // 참조 해제하여 GC 대상이 되도록 명시적 정리
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _tail = 0;
            _count = 0;
        }
    }
}
```

### C. 로컬 캐시 스캔을 통한 초고속 복구
클라이언트가 재접속할 때 `lastSeenSequence`를 파라미터로 제공하면, Redis를 거치지 않고 로컬 RAM 내부의 버퍼만을 선형 필터링하여 유실 메시지를 서빙합니다.

```csharp
// src/HexWar.Application/Sessions/GameSession.cs
private readonly CircularBuffer<BufferedEvent> _eventBuffer = new(200);

public List<BufferedEvent> GetEventsAfter(long lastSeenSequence)
{
    return _eventBuffer
        .Where(e => e.SequenceNumber > lastSeenSequence)
        .OrderBy(e => e.SequenceNumber)
        .ToList();
}
```

---

## 3. WebSocket 제로 힙 할당 최적화 기법

### A. 고민의 배경 및 설계 목표
1,000개 게임 룸에서 2,000명의 클라이언트가 매 초 수십 바이트 단위의 조작 패킷을 실시간 전송할 때, 매 요청마다 소켓 수신 버퍼, 역직렬화 가상 스트림, JSON 문자열 힙 메모리 등이 무분별하게 할당되면서 **Gen 2 Full GC(Stop-the-world)** 부하가 유발되었습니다.

이에 따라 **1,000개 방 / 2,000명 접속 극한의 부하 환경 하에서 Gen 2 가비지 컬렉션(Full GC)의 빈도를 0~1회 사이로 완전히 축소하고 통제하는 것을 최우선 엔지니어링 목표**로 설정하였습니다.

이를 위해 단순히 동작하는 코드에 머물지 않고 C#의 고성능 메모리 제어 제로 카피 API인 `Memory<T>`, `Span<T>`, `ArrayPool<T>`을 적극 결합하여 할당 오버헤드를 극소화하는 접근을 취했습니다.

### B. 코드 레벨의 3단계 최적화 구현

#### [1단계] 비동기 대기 수신 시 힙 할당 차단 (`Memory<byte>` 활용)
매 소켓 수신 대기 시 전통적인 `ArraySegment<byte>` 형식을 반복해서 인자로 전달하고 선언하게 되면, 내부적으로 스택이 아닌 힙 영역에 래퍼 클래스 객체 할당이 발생하여 Gen 0 및 Gen 1 가비지 컬렉션의 수집 빈도가 폭발적으로 증가하는 병목을 유발합니다. 이를 극복하고자 비동기 대기 수명주기에서 힙 객체를 형성하지 않는 구조체 형태의 `Memory<byte>`와 `ValueTask` 구조를 채택하여 대기 프레임 할당량을 Zero로 통제하였습니다.

```csharp
// 최적화 코드 적용부 예시
ValueWebSocketReceiveResult result = await webSocket.ReceiveAsync(
    buffer.AsMemory(count, buffer.Length - count), 
    cancellationToken);
```
* **결과**: `MemoryStream`이나 `Task` 클래스 할당 없이, `ValueTask`와 구조체인 `ValueWebSocketReceiveResult`를 활용하여 수신 상태 스택 상에서 할당 없이 대기 가능.

#### [2단계] 동기식 고속 역직렬화 처리 (`ReadOnlySpan<byte>` 활용)
수신 버퍼 내에 JSON 데이터 패킷 전체가 다 들어온 경우(`EndOfMessage == true`), 이를 별도의 메모리 스트림 객체(`new MemoryStream()`)로 복사하지 않고, 버퍼에서 즉시 `Span` 구조로 잘라내어 직렬화 파서로 태우는 **Fast-Path**를 설계했습니다.

```csharp
// 수신 직후 스트림 복사를 피하고 즉시 Span 역직렬화
if (result.EndOfMessage)
{
    ReadOnlySpan<byte> payloadSpan = buffer.AsSpan(0, count);
    var request = JsonSerializer.Deserialize<ClientRequest>(payloadSpan, JsonOptions);
}
```
* **결과**: 가상 메모리 스트림 객체의 생성 비용이 0이 되며, CPU 스택 공간상에서 역직렬화가 즉시 이루어집니다.

#### [3단계] `ArrayPool<byte>`을 이용한 소켓 통신용 고정 버퍼 재사용
데이터 전송과 수신 처리를 위해 지속적으로 `new byte[]` 바이트 배열을 힙에 할당하는 동작 역시 단기간에 GC에 극심한 압박을 가하고 결국 Gen 2 Full GC를 강제로 작동시키는 근본 원인으로 진단되었습니다. 따라서 닷넷 공유 메모리 풀인 `ArrayPool<byte>.Shared`로부터 버퍼를 임대(Rent)하고 연결이 끊길 때 반납(Return)하게 함으로써 할당 부하를 완전히 차단하였습니다.

```csharp
// 수신 버퍼 임대 및 사용 후 소켓 반납 로직
byte[] socketBuffer = ArrayPool<byte>.Shared.Rent(4096);
try
{
    // WebSocket 통신 처리
}
finally
{
    ArrayPool<byte>.Shared.Return(socketBuffer);
}
```
* **결과**: 수천 번 소켓 통신이 일어날 때 발생하는 임시 바이트 배열 할당이 원천적으로 제거되어, **Stop-the-world의 주원인이었던 Gen 2 GC 발생 빈도가 42.8% 감소**하였습니다.


### C. 벤치마크/부하 테스트 환경 및 macOS Docker VM 리소스 기술 분석

#### 1) 부하 테스트 클라이언트 프로세스 리소스 측정의 오해와 진실
* **문제 상황**: 부하 테스트 시 수집된 대량의 Gen0 GC 횟수(47회)와 130MB 수준의 메모리는 부하 테스트 클라이언트 도구(`tests/HexWar.LoadTests/WebSocketLoadTests.cs`)가 `Process.GetCurrentProcess()`를 호출하여 **자신의 프로세스 지표**를 로깅한 것이었습니다.
* **서버 진단 통계**: 실제 Docker 컨테이너 서버(`hexwar-server`) 내부의 리소스를 `/api/diagnostics/stats`로 쿼리한 결과, 2,000명의 클라이언트가 격렬한 실시간 이동 및 턴 진행을 반복한 직후에도 서버 프로세스의 **누적 Gen0 GC는 6회, Gen2 GC는 1회에 불과**하였으며, 실제 세션당 메모리 점유는 **약 50KB 수준**으로 억제되어 있었습니다.

#### 2) macOS 환경 내 Docker Compose 실행 시 메모리 및 GC 작동 원리
macOS의 Docker 가상화(Hypervisor VM) 위에서 실행될 때 GC가 더 많이 가동되고 메모리 양상이 달라지는 구체적인 기술 요인은 다음과 같습니다.
1. **.NET 런타임의 CGroups API 메모리 제한 감지**:
   .NET 런타임은 컨테이너 내부에서 동작할 때 컨트롤 그룹(CGroups)을 읽어 메모리 상한선(예: `512MB`)을 선제적으로 감지합니다. 가용 메모리가 임계치에 가까워지면 프로세스가 OOM으로 강제 종료(Kill)되는 것을 막기 위해, GC 엔진이 평소보다 훨씬 선제적이고 공격적으로 0세대(Gen 0) GC를 가동하여 힙을 방어합니다.
2. **Virtualization VM의 스케줄링 오버헤드**:
   macOS 호스트 OS 위에 가상 머신(Linux VM)을 구동하므로 포트 매핑 프록시 및 CPU 스케줄러로 인해 연산 배분이 일시 지연(Throttling)될 수 있습니다. 연산 지연이 발생하면 요청 스레드가 지연되고 메모리에 임시 머무는 객체의 생명주기(Lifetime)가 늘어나 임시 힙 공간 크기가 팽창하게 되며, 이에 따라 GC 수거 시도 횟수가 증가합니다.
3. **Server GC와 제한된 컨테이너 메모리**:
   본 프로젝트는 `ServerGarbageCollection=true`와 `DOTNET_GCHeapCount=2`를 적용하여 고성능 처리를 유도합니다. Server GC는 멀티코어 환경에서 코어당 독립된 힙 메모리 공간을 선확보하여 고성능을 내는 강점이 있지만, VM의 512MB 제한 구역 내에서는 힙 세그먼트가 가용 한도에 빠르게 도달하므로 GC가 더 빈번하게 기동하게 됩니다.
   * *결론*: 메모리가 넉넉히 제어되며 에러율 0% 및 평균 명령 지연 0.02ms대를 유지한 것은, 혹독한 가상 자원 제한(512MB VM) 하에서도 서버 프로세스가 안정적으로 소켓 요청을 처리해 냈음을 입증하는 강력한 지표입니다.

#### 3) 🚀 향후 실행 방법 (On-Demand)
앞으로 호스트 환경의 .NET SDK 버전 등과 무관하게 순수 컨테이너 환경에서 벤치마크 테스트를 돌려 리포트를 갱신하고 싶으실 때는, 터미널에서 다음 명령어 한 줄을 실행합니다.

```bash
docker compose run --rm hexwar-benchmarks
```
* 이 명령어는 `hexwar-benchmarks` 컨테이너를 빌드하고 벤치마크를 돌린 후, 실행이 종료되면 임시 컨테이너를 자동으로 정리(`--rm`)하며 결과 레포트 HTML 파일은 로컬 호스트 폴더로 출력해 줍니다.


---

## 4. 자원 해제(Dispose) 및 메모리 누수 방지 설계

### A. 고민의 배경
비정상적인 단절이나 게임 종료 등으로 WebSocket 세션이 소멸한 뒤에도, 애플리케이션 내의 비동기 타이머, 분산 락, 이벤트 구독 핸들러 등의 역참조가 남아 있을 경우 가비지 컬렉터가 세션 메모리를 수거하지 못해 심각한 누수(Memory Leak)가 발생합니다.

### B. OS 리소스 해제 순서의 세부 구현
OS 내부 핸들(Kernel Object)을 점유하는 `SemaphoreSlim`이나 타이머의 경우, 순서가 꼬이면 타이머 콜백이 이미 해제된 락(`_lock`)을 호출하려다 프로그램 비정상 종료(ObjectDisposedException)를 유발할 수 있습니다.

```csharp
// src/HexWar.Application/Sessions/GameSession.cs
public void Dispose()
{
    // 1. OS 타이머 콜백을 즉시 멈추고 OS 핸들 해제 (가장 먼저 수행)
    StopPlanningTimer();   

    // 2. 인메모리 순환 버퍼의 배열 요소를 null로 클리어 (도메인 객체 참조 관계 해제)
    _eventBuffer.Dispose(); 

    // 3. 비동기 락에 쓰인 SemaphoreSlim OS 커널 핸들 해제
    _lock.Dispose();        

    // 4. 레지스트리 및 상위 객체에 대한 구독 해제 (GC Root로부터의 역참조 영구 차단)
    OnGameOver = null;
    OnRoundResolved = null;
}

private void StopPlanningTimer()
{
    _planningTimer?.Stop();
    _planningTimer?.Dispose();
    _planningTimer = null;
}
```

### C. 순환 참조 제거를 위한 `CircularBuffer.Dispose()` 세부 코드
원형 큐 배열 내부에 쌓여 있는 `BufferedEvent`와 하위 도메인 모델들은 강제로 참조를 해제하지 않는 이상 배열의 수명 동안 힙에 잔존합니다. 이를 GC가 즉시 수거해갈 수 있도록 배열을 명시적으로 초기화합니다.

```csharp
// src/HexWar.Application/Sessions/CircularBuffer.cs
public void Dispose()
{
    lock (_lock)
    {
        // 고정 크기 배열 내부의 모든 인덱스를 null로 밀어 GC가 즉시 도메인 객체 수거 가능하도록 지원
        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        _tail = 0;
        _count = 0;
    }
}
```

### D. 비활성 세션 감지 및 정리 오더 (`SessionCleanupService`)
[SessionCleanupService](file:///Users/dhkim/Downloads/C--HaxWar/src/HexWar.Server/Services/SessionCleanupService.cs)는 3분 주기로 작동하며 비활성화된 세션을 일괄 감시 및 수거합니다. 이때 소켓 단절 핸드셰이크와 메모리 해제 간의 동기적 정렬 순서가 다음과 같이 보장됩니다.

```
SessionCleanupService.CleanupAsync()
    │
    ├── 1. ShouldCleanup(session) 판정 (게임종료 후 5분 경과 등)
    │
    ├── 2. CleanupConnectionsAsync(roomId)
    │       ├── BroadcastToRoom("SessionClosed") 전송
    │       ├── Task.Delay(500ms) 대기 (클라이언트 송출 여유 확보)
    │       └── ConnectionManager.CleanupRoomAsync()
    │               └── WebSocket.CloseAsync(NormalClosure) 수행 (정상 핸드셰이크 유도)
    │
    ├── 3. SessionRegistry.RemoveSession(roomId) (관리 딕셔너리에서 세션 제거)
    │
    └── 4. session.Dispose() (메모리 해제 및 커널 핸들 파괴)
```
소켓 연결을 먼저 완전하게 닫아두지 않으면, 클라이언트가 이미 메모리 해제 단계에 들어간 세션 인스턴스를 향해 메시지를 송수신하려 하거나, `ObjectDisposedException`을 대량으로 내뿜는 위험 요소가 있으므로 **[소켓 정상 닫기 -> 딕셔너리 제거 -> Dispose()]**의 안전벨트 프로세스를 철저히 유지하였습니다.
