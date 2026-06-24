# 🎮 게임 로직 상세 가이드

## 1. 핵심 게임 개념

### 1.1 동시 턴제 (Simultaneous Turn)

**문제:**
- 턴제 게임에서 한 플레이어의 행동이 노출되면 상대가 최적 대응 가능
- 정보 불균형 발생

**해결책:**

```
Planning Phase (30초):
├─ Player A: 이동 명령 제출 (보이지 않음)
├─ Player B: 이동 명령 제출 (보이지 않음)
└─ 타이머 만료 또는 양쪽 모두 제출

Resolution Phase (1초):
├─ 모든 명령을 동시에 해석
├─ 결과 계산 (이동, 조우, 점령)
└─ 클라이언트에 모두에게 같은 시간에 결과 전송
```

**구현:**

```csharp
// Planning 단계: 명령 예약만
public void MoveUnits(NodeId from, NodeId to, int count)
{
    _pendingMoves.Add(new Move { From = from, To = to, Count = count });
    // 즉시 실행하지 않음!
}

// Resolution 단계: 일괄 실행
public void ResolveRound()
{
    foreach (var move in _pendingMoves)
    {
        ExecuteMove(move);  // 여기서 비로소 실행
    }
}
```

---

### 1.2 간선 조우(Encounter)

**개념:**
- 유닛이 이동 중 같은 간선에서 만남 → 진격 vs 복귀 결정

**구현:**

```
┌─────────────────────────────────────┐
│         간선 (1번 노드 → 2번 노드)   │
├─────────────────────────────────────┤
│                                     │
│ Round 남음 | 진영A 유닛 | 진영B 유닛│
│───────────┼──────────┼──────────│
│    1      | 3명      | -        │  ← 도착 임박
│    2      | -        | 2명      │  ← 조금 더 진행
│                                     │
│ 문제: 같은 Round에 다른 진영?       │
│      → 조우 발생!                   │
│                                     │
└─────────────────────────────────────┘
```

**조우 처리:**

```
조우 감지:
├─ Edge.HasEncounter() 확인
├─ 같은 간선의 같은 Round에 A/B 유닛 있나?
└─ 있으면 조우 발생

조우 해결:
├─ 게임 일시정지 (PendingEncounters 큐)
├─ 양쪽에게 결정 요청 (Advance/Retreat)
│  ├─ A: Advance, B: Advance → 전투 (손실)
│  ├─ A: Advance, B: Retreat → A 전진, B 후진
│  ├─ A: Retreat, B: Advance → A 후진, B 전진
│  └─ A: Retreat, B: Retreat → 둘 다 후진 (1칸)
└─ 결과 업데이트
```

---

### 1.3 보급로(Supply Line)

**개념:**
- 특정 노드(전략적 거점)를 점령하면 자동으로 수비대 배치
- 거점 방어 용이

**구현:**

```csharp
if (node.Type == NodeType.SupplyLine)
{
    // 소유권 변경 시 자동 수비대 생성
    if (node.Ownership != newOwner)
    {
        node.Garrison = new UnitGroup
        {
            Count = 1,
            IsMobile = false  // 이동 불가
        };
    }
}

// 소유권을 잃으면 자동 소멸
if (node.Ownership == PlayerSide.Neutral)
{
    node.Garrison = null;
}
```

---

### 1.4 본부(Headquarters) 시야

**개념:**
- 본부(중앙 노드)에 유닛을 보낸 플레이어는 맵 전체 시야 획득
- 정보 우위 제공

**구현:**

```csharp
public bool HasHeadquartersVision(PlayerSide side)
{
    var hqNode = Nodes.FirstOrDefault(n => n.Type == NodeType.Headquarters);
    return hqNode?.Units.Any(u => u.Side == side && u.Count > 0) ?? false;
}

// 상태 조회 시 분기
public GameRoomPublic GetPublicState(PlayerSide side)
{
    var hasVision = HasHeadquartersVision(side);
    
    foreach (var edge in Edges)
    {
        if (hasVision)
        {
            // 전체 정보 노출
            return edge.ToPublicInfo();  // 목적지 포함
        }
        else
        {
            // 제한된 정보만
            return edge.ToRestrictedInfo();  // 목적지 제외
        }
    }
}
```

---

## 2. 데이터 구조

### 2.1 게임방 상태 (GameRoom)

```csharp
public class GameRoom
{
    // 기본 정보
    public RoomId Id { get; }
    public GamePhase CurrentPhase { get; }
    public int CurrentRound { get; }
    
    // 플레이어
    public Dictionary<PlayerId, Player> Players { get; }
    
    // 맵
    public List<Node> Nodes { get; }       // 격자 노드
    public List<Edge> Edges { get; }       // 간선 + 이동 중 유닛
    
    // 게임 상태
    public Queue<EncounterInfo> PendingEncounters { get; }
    public GameResult? GameResult { get; }
}
```

### 2.2 간선의 이동 중 유닛 (SortedList)

**문제:**
```csharp
// 나쁜 설계
List<List<TravelingGroup>> travelingUnits;  // 인덱스 0~20
// 문제: 
// - 0~20 배열 할당 (미사용 공간 낭비)
// - 매 라운드마다 모든 요소를 한 칸씩 앞으로 당김 (비용)
// - 인덱스 범위 초과 위험
```

**해결책:**
```csharp
// 좋은 설계
SortedList<int, List<TravelingGroup>> travelingUnits;
// Key: 남은 라운드 수
// Value: 그 라운드에 도착할 유닛 그룹

// 예: 
// Key=1 → [UnitGroup A (3명)],  ← 다음 라운드 도착
// Key=2 → [UnitGroup B (2명)]   ← 그 다음 라운드 도착

// 매 라운드:
// 1. Key=1 그룹은 도착 처리
// 2. 나머지 Key를 -1 (새 컬렉션 생성)
// → O(n) 하지만 n=남은 방향 수만 (보통 4-6개)
```

### 2.3 원형 큐 (CircularBuffer)

```csharp
public class CircularBuffer<T>
{
    private T[] _buffer;      // 고정 크기
    private int _head = 0;    // 다음 쓰기 위치
    private int _tail = 0;    // 가장 오래된 위치
    private int _count = 0;   // 현재 항목 수
    
    public void Add(T item)
    {
        _buffer[_head] = item;
        _head = (_head + 1) % _buffer.Length;
        
        if (_count < _buffer.Length)
        {
            _count++;
        }
        else
        {
            _tail = (_tail + 1) % _buffer.Length;  // 덮어쓰기
        }
    }
}

// 사용 예:
// Add(event1) → [event1, _, _, ...]
// Add(event2) → [event1, event2, _, ...]
// ...
// Add(event200) → [event1, event2, ..., event200]
// Add(event201) → [event201, event2, ..., event200]  ← event1 덮어씀
```

---

## 3. 게임 진행 흐름

### 3.1 라운드 해소 (ResolveRound)

```
1. 모든 PendingMoves 처리
   ├─ 유효성 검증 (이중 사용 방지)
   ├─ 간선에 이동 유닛 추가
   └─ 에러 시 로프백

2. 조우 검사 (Encounter)
   ├─ 각 간선에서 HasEncounter() 확인
   ├─ 조우 발생 시 PendingEncounters에 추가
   └─ 게임 일시정지

3. 조우 해결 (Encounter Resolution)
   ├─ 양쪽 결정 대기 (타임아웃 30초)
   ├─ 결정 수신 시 최종 위치 계산
   └─ 손실 계산

4. 점령 업데이트 (Node Ownership)
   ├─ 각 노드에 도착한 유닛 확인
   ├─ 소유권 변경 시 Garrison 생성/삭제
   └─ 게임 승리 조건 확인

5. 이벤트 발행
   ├─ RoundResolved 이벤트 생성
   ├─ 모든 변경사항 포함
   └─ Redis Pub/Sub 발행

6. 다음 라운드 준비
   ├─ CurrentRound++
   ├─ PendingMoves.Clear()
   └─ 새로운 Planning Phase 시작
```

---

## 4. 게임 상태 머신

```
┌─────────────────────────────────────────────────────┐
│                  게임 상태 흐름                      │
├─────────────────────────────────────────────────────┤

WaitingForPlayers
    │ (양쪽 모두 연결)
    ▼
GameStarted
    │ (초기화 완료)
    ▼
Planning ◄──────────┐
    │              │
    │ (30초 또는 양쪽 제출)
    ▼              │
Resolution         │
    │              │
    ├─ Encounter ──┘ (조우 해결 대기)
    │
    ├─ ResultCalculation
    │
    ▼
RoundComplete
    │ (라운드 누적 또는 승리 조건)
    ▼
GameOver
    │ (5분 유휴 후 정리)
    ▼
SessionClosed
```

---

## 5. 네트워크 프로토콜

### 5.1 클라이언트 → 서버

```json
// MoveUnits
{
  "type": "move_units",
  "payload": {
    "from": 1,
    "to": 2,
    "count": 3
  }
}

// EncounterDecision
{
  "type": "encounter_decision",
  "payload": {
    "from_node": 1,
    "to_node": 2,
    "decision": "Advance"  // or "Retreat"
  }
}

// GetState
{
  "type": "get_state"
}

// RetryGetEvents
{
  "type": "retry_get_events",
  "payload": {
    "lastSeenSequence": 1000
  }
}
```

### 5.2 서버 → 클라이언트

```json
// GameEvent
{
  "type": "game_event",
  "eventType": "RoundResolved",
  "sequenceNumber": 1001,
  "timestamp": "2026-06-24T10:15:30Z",
  "payload": {
    "round": 5,
    "events": [
      {
        "type": "UnitsMoved",
        "from": 1,
        "to": 2,
        "count": 3
      },
      {
        "type": "Encounter",
        "fromNode": 1,
        "toNode": 2,
        "playerA": 3,
        "playerB": 2
      }
    ]
  }
}

// GameOver
{
  "type": "game_event",
  "eventType": "GameOver",
  "payload": {
    "winner": "A",
    "statistics": {
      "roundsPlayed": 20,
      "totalUnitsDeployed": 100,
      "unitsLost": 15
    }
  }
}
```

---

## 6. 게임 밸런싱

### 6.1 초기 유닛 배치

```csharp
// 각 플레이어당 초기 유닛
private const int INITIAL_UNIT_COUNT = 50;

// 시작 노드
// Player A: Node 1 (좌측)
// Player B: Node 6 (우측)
```

### 6.2 이동 거리와 소요 시간

```
거리 1칸: 1라운드
거리 2칸: 2라운드
거리 N칸: N라운드

최대 거리: 5칸 (약 5라운드 = 2.5분)
```

### 6.3 전투 손실 계산

```
조우에서 양쪽 모두 Advance 선택 시:
├─ 약한 쪽이 패배
├─ 손실: min(unitA, unitB) × 0.3 = 30% 손실
└─ 승자: 나머지 유닛 도착
```

---

## 7. 게임 종료 조건

### 7.1 승리 조건

```
1. 상대 본부 점령
   ├─ 플레이어 A의 유닛이 Node 3 (B의 본부) 점령
   └─ A 승리

2. 남은 시간 내 점령 노드 비율
   ├─ 40라운드 후 점령 노드 > 50%
   └─ 그 플레이어 승리

3. 시간 제한 (개발 중)
   ├─ 일정 시간 경과 후 점령 비율로 판정
   └─ TBD
```

---

## 8. 디버깅 및 테스트

### 8.1 콘솔 로깅

```
Round 5 시작:
  Planning Phase: 30초 대기
  Player A 명령: Move(1→2, 3명)
  Player B 명령: Move(6→5, 2명)
  
Round 5 완료:
  UnitsMoved: 1→2(A:3명), 6→5(B:2명)
  Encounters: None
  Ownership: Node 2 A 점령 (변경!)
  
Round 6 시작...
```

### 8.2 단위 테스트

```csharp
[Test]
public void MoveUnits_SameUnitTwice_ShouldRejectSecond()
{
    // Arrange
    var room = CreateTestGameRoom();
    
    // Act
    room.MoveUnits(PlayerSide.A, 1, 2, 5);
    room.MoveUnits(PlayerSide.A, 1, 3, 5);  // 같은 유닛 2번 이동
    
    // Assert
    Assert.ThrowsException<InvalidOperationException>();
}
```

