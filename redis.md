# Redis를 이용한 분산 환경 이벤트 동기화 및 락 관리 (Redis Concurrency & Event Sync)

본 문서는 **HexWar** 분산 다중 서버 환경에서 Redis를 메시지 브로커 및 분산 락, 상태 저장소로 활용하는 설계 방식과 실시간 패킷 전파 흐름을 설명합니다.

---

## 1. Redis Pub/Sub 기반 분산 이벤트 전파 흐름 (Publish & Subscribe)

특정 게임방(`roomId`) 내 플레이어가 행동을 개시하여 발생한 이벤트는 해당 플레이어가 접속한 서버 노드뿐만 아니라, 상대방 플레이어가 접속해 있는 다른 물리 서버 노드로 실시간 브로드캐스팅되어야 합니다.

```
┌─────────────────────────────────────────────────────────────────┐
│                        Server 1 (GameRoom 소유)                  │
│                                                                 │
│  Player A → MoveUnits 명령                                      │
│       │                                                         │
│       ▼                                                         │
│  GameRoom.MoveUnits() → 이벤트 발생                             │
│       │                                                         │
│       ├──→ CircularBuffer.Add()                                 │
│       │                                                         │
│       ├──→ WebSocket → Player A 직접 전송                       │
│       │                                                         │
│       ├──→ Redis Pub/Sub Publish("game_events:room-123")        │
│       │         │                                               │
│       │         │  (자기 서버 이벤트는 무시됨)                  │
│       │         ▼                                               │
│       │    Server 1 Subscriber: "나 자신이 보낸 것 = 무시"      │
│       │                                                         │
│       └──→ Redis SaveAsync(gameRoom)                            │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
                         │
                         │ Redis Pub/Sub ("game_events:room-123")
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│                        Server 2 (Player B 연결)                  │
│                                                                 │
│  Redis Subscriber: "game_events:room-123"                      │
│       │                                                         │
│       │  message.SourceServerId != Environment.MachineName      │
│       │  → "Server 1이 보낸 외부 이벤트다"                        │
│       │                                                         │
│       ▼                                                         │
│  HandleRemoteEventAsync()                                       │
│       │                                                         │
│       ├──→ CircularBuffer.Add() (재연결 복구용)                 │
│       │                                                         │
│       └──→ WebSocket → Player B 직접 전송                       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### 💡 주요 핵심 매커니즘 분석

1. **무한 루프 방지 (Self-Publishing Bypass)**:
   * [RedisEventPublisher.cs](file:///Users/dhkim/Downloads/C--HaxWar/src/HexWar.Infrastructure/Messaging/RedisEventPublisher.cs)는 이벤트를 발행할 때 현재 서버의 고유 ID인 `ServerIdentity.Id`를 메시지에 함께 담아 보냅니다.
   * 모든 서버 노드가 동일한 Redis 채널(`game_events:{roomId}`)을 구독 중이므로, 내가 발행한 메시지가 다시 나에게 수신됩니다. 
   * 이때 `SourceServerId == ServerIdentity.Id` 검증을 거쳐 **자기 자신 노드에서 발행한 중복 수신 메시지는 즉시 무시**하도록 제어하여 무한 루프를 방지합니다.
2. **동작 분기 및 최적 레이턴시**:
   * **로컬 클라이언트(Player A)**: 데이터베이스 및 외부 네트워크 브로커의 응답을 기다리지 않고, 소켓 쓰기 스레드가 `CircularBuffer`에 기록함과 동시에 소켓으로 직접 즉시 패킷을 보냅니다.
   * **원격 클라이언트(Player B)**: Redis Pub/Sub 메시지가 밀려들어오는 순간 Server 2의 로컬 비동기 콜백인 `HandleRemoteEventAsync`가 깨어나며, 마찬가지로 원격 플레이어 전용 세션의 `CircularBuffer`에 적재하고 소켓에 씁니다.
   * 이로 인해 중간의 무거운 비동기 Worker 프로세스가 배제된 상태로 **로컬 메모리 ➔ Redis Pub/Sub ➔ 로컬 메모리**의 최단 홉으로 전파가 끝납니다.
3. **각 노드별 독립된 재연결 복구 버퍼 완비**:
   * 플레이어 A와 B가 서로 다른 서버에 물려 있어도, Redis Pub/Sub을 통해 양 서버 노드에 동일한 이벤트가 수신되므로 각자 자신의 로컬 메모리 `CircularBuffer`에 완전한 시퀀스 이력을 유지합니다. 
   * 따라서 플레이어가 어느 서버로 튕겨서 재연결(Failover)되더라도 Redis I/O 없이 로컬 버퍼 캐시로 무손실 복구할 수 있습니다.

---

## 2. Redis 명령어 및 상태 확인 진단 가이드 (CLI Commands)

실제 구동 중인 로컬/도커 환경의 Redis 상태와 적재 데이터를 확인하고 모니터링하기 위한 유용한 명령어 리스트입니다.

### A. 컨테이너 및 CLI 접속
```bash
# 1. 레디스 컨테이너 상태 체크 (Ping)
docker exec hexwar-redis redis-cli ping

# 2. 레디스 CLI 접속 
docker exec -it hexwar-redis redis-cli
```

### B. 데이터 스토어 조회 명령어
```redis
# 1. 현재 적재된 모든 키 조회
KEYS *

# 2. 특정 게임방의 JSON 상태 문자열 확인
GET gameroom:<roomId>

# 3. 분산 락(Distributed Lock) 활성화 여부 확인
GET lock:gameroom:<roomId>
```

### C. 실시간 Pub/Sub 이벤트 모니터링
```redis
# 1. 활성화되어 구독 중인 모든 이벤트 채널 확인
PUBSUB CHANNELS game_events:*

# 2. 특정 방에 유입되는 이벤트를 실시간 모니터링 (Ctrl + C로 탈출)
SUBSCRIBE game_events:<roomId>

# 3. 패턴 매칭을 통해 모든 게임방의 실시간 패킷 전송 상황 감시
PSUBSCRIBE game_events:*

# 4. Redis에 인입되는 전체 네트워크 커맨드 스트림 모니터링
MONITOR
```