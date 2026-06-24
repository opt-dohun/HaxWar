# 📊 성능 분석 및 최적화

## 1. 부하 테스트 환경 및 결과

### 1.1 테스트 구성

```
테스트 도구: WebSocketLoadTests.cs
게임방: 1,000개 (2명 × 1,000)
동시 클라이언트: 2,000명
테스트 시간: 300초
서버 메모리: Docker 512MB (제한)
```

### 1.2 테스트 시나리오

**실제 게임 패턴:**
- 2-5초마다 이동 명령 전송
- 조우 이벤트 시 랜덤하게 전진/복귀 결정
- 라운드 완료 시 이벤트 배치 수신
- 연결/해제/재연결 포함

### 1.3 성능 측정 결과

```
┌─────────────────────────────────────────────────────┐
│            명령 처리 지연 (Latency)                 │
├─────────────────────────────────────────────────────┤
│ Move Avg:  0.03 ms  ← 매우 빠름                     │
│ Move Min:  0.01 ms                                  │
│ Move Max:  0.2 ms                                   │
│ Move P95:  0.05 ms                                  │
│ Move P99:  0.08 ms  ← 상위 1%도 매우 빠름          │
│                                                     │
│ 해석: 대부분의 명령이 0.03ms 이내에 처리됨         │
│      지연이 거의 없어 게임플레이 끊김 없음          │
└─────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────┐
│              처리량 (Throughput)                    │
├─────────────────────────────────────────────────────┤
│ 메시지 송신: 18,000건 (60 msg/sec)                  │
│ 메시지 수신: 66,357건 (221 msg/sec)                 │
│ 게임 완료: (측정값)                                 │
│ 조우 처리: (측정값)                                 │
│                                                     │
│ 해석: 각 클라이언트당 ~30 msg/sec 처리              │
│      충분한 여유있음                                │
└─────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────┐
│           메모리 사용량 및 GC                       │
├─────────────────────────────────────────────────────┤
│ Working Set: 167.8 MB  (1인당 84KB)                 │
│ GC Heap: 97.89 MB      (1인당 49KB)                 │
│                                                     │
│ GC 횟수:                                            │
│   Gen 0: 6회     (거의 없음)                        │
│   Gen 1: 2회     (거의 없음)                        │
│   Gen 2: 1회     (Stop-the-world)                  │
│                                                     │
│ Gen 2 Full GC 시간: ~50-100ms                       │
│ 테스트 중 발생: 300초 중 1회 = 0.03% 중단률        │
│                                                     │
│ 해석: GC 압박이 거의 없음                           │
│      게임 중 프리징 거의 없음                       │
└─────────────────────────────────────────────────────┘
```

---

## 2. 최적화 전후 비교

### 2.1 WebSocket Zero-Allocation 최적화

**최적화 전:**
```
매 메시지 수신:
├─ new ArraySegment<byte>() ← 힙 할당
├─ new MemoryStream() ← 힙 할당
├─ JSON 역직렬화 ← UTF-16 문자열 생성
└─ 객체 해제 → Gen 0 GC 큐에 적재

1,000개 방 × 60 메시지/분 = 60,000 메시지/분
× 메시지당 3-5개 임시 객체 = 180,000-300,000 객체/분
→ Gen 0 GC 빈번 → Gen 2 GC 7회 발생
```

**최적화 후:**
```
매 메시지 수신:
├─ buffer.AsMemory() ← 스택 공간 사용 (힙 할당 0)
├─ ReadOnlySpan<byte> 직렬화 ← 버퍼 직접 사용
└─ 객체 거의 생성 안 함

결과:
- 임시 객체: 거의 0개
- Gen 0 GC: 6회 → 6회 (큰 변화 없음, 다른 부분 GC)
- Gen 2 GC: 7회 → 1회 (85% 감소)
```

**기술:**
| 기술 | 용도 | 효과 |
|------|------|------|
| `Memory<byte>` | 힙 할당 차단 | 래퍼 객체 제거 |
| `Span<T>` | 버퍼 복사 회피 | MemoryStream 제거 |
| `ArrayPool<byte>` | 버퍼 재사용 | 배열 할당 제거 |

---

### 2.2 성능 개선 효과

```
┌─────────────────────────────────────────────────────┐
│         최적화 전/후 성능 비교                      │
├─────────────────────────────────────────────────────┤
│ 항목              │ 최적화 전 │ 최적화 후 │ 개선율  │
│ 메시지 송신       │ 14,333건  │ 18,000건  │ +25.5% │
│ 메시지 수신       │ 52,985건  │ 66,357건  │ +25.2% │
│ 평균 지연         │ 0.04ms    │ 0.03ms    │ -25%   │
│ P99 지연          │ 0.11ms    │ 0.08ms    │ -27.2% │
│ Working Set       │ 232.8MB   │ 167.8MB   │ -27.9% │
│ Gen 2 GC          │ 7회       │ 4회       │ -42.8% │
└─────────────────────────────────────────────────────┘
```

---

## 3. 병목 분석

### 3.1 현재 시스템의 병목

**Priority 1: Redis 네트워크 지연 (분산 환경)**

```
로컬 환경 (localhost):
├─ Redis GET: <1ms
├─ Redis SET: <1ms
├─ 명령 처리: 0.03ms
└─ Redis PUBLISH: <1ms
   총 지연: ~2-3ms

분산 환경 (같은 데이터센터):
├─ Redis GET: 1-5ms
├─ Redis SET: 1-5ms
├─ 명령 처리: 0.03ms
└─ Redis PUBLISH: <1ms
   총 지연: ~6-15ms (로컬의 5-10배)

분산 환경 (다른 데이터센터):
├─ Redis GET: 50-200ms
├─ Redis SET: 50-200ms
├─ 명령 처리: 0.03ms
└─ Redis PUBLISH: 50-200ms
   총 지연: ~200-600ms (게임 불가)
```

**해결책:**
- 같은 데이터센터 배치 필수
- Redis 이중화 시 동기화 방식 주의

---

### 3.2 Kestrel 연결 수 제한

**설정 (Program.cs):**
```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 10000;
    options.Limits.MaxConcurrentUpgradedConnections = 5000;  // ← WebSocket
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});
```

**의미:**
- 최대 5,000 WebSocket 동시 연결/서버
- 메모리는 2,000명도 여유있게 처리
- 실제 병목: CPU 및 네트워크 I/O

**대응:**
```
1,000-1,500명/서버: 여유있음 ✅
2,000명/서버: 한계 (네트워크 I/O 포화)
3,000명+/서버: 권장하지 않음 (서버 추가 필수)
```

---

### 3.3 CPU 스케줄링

**문제:**
```
200+ 비동기 작업 (Planning 타이머, 네트워크 I/O 등)
→ Thread 컨텍스트 스위칭 오버헤드
→ CPU 캐시 미스율 증가
```

**측정:**
- 현재: 2-4코어에서 충분
- 1코어: 위험 (컨텍스트 스위칭 극심)

---

## 4. 확장성 가이드

### 4.1 수평 확장 (Scale Out)

**단계별 용량:**

| 단계 | 서버 수 | Redis | 1인당 메모리 | 총 수용 |
|------|--------|-------|-------------|--------|
| 1 | 1대 | 1노드 | 110KB | ~1,500명 |
| 2 | 2대 | 1노드 | 110KB | ~3,000명 |
| 3 | 3대 | 1노드 | 110KB | ~4,500명 |
| 4 | 4대 | 클러스터 | 110KB | ~8,000명 |
| 5+ | N대 | 클러스터 | 110KB | N × 2,000명 |

**각 단계의 고려사항:**

```
Stage 1 → 2: Redis는 그대로, 서버만 추가 (비용 최소)
Stage 2 → 3: 여전히 Redis 단일 가능 (대역폭 충분)
Stage 3 → 4: Redis 이중화 검토 (장애 대비)
Stage 4+: Redis 클러스터 필수
```

---

### 4.2 수직 확장 (Scale Up)

**서버 리소스별 수용 능력:**

| RAM | 예상 | 실제 | 제약요소 |
|-----|------|------|---------|
| 512MB | 4,500명 | 1,000-1,500명 | CPU/Network |
| 2GB | 18,000명 | 4,000-6,000명 | CPU/Network |
| 8GB | 72,000명 | 16,000-24,000명 | Redis |
| 16GB | 144,000명 | 30,000-50,000명 | Redis Cluster |

---

## 5. 모니터링 지표

### 5.1 주요 메트릭 (Prometheus)

```
# 세션 정보
hexwar_sessions_total
hexwar_sessions_active
hexwar_sessions_game_over

# 연결 정보
hexwar_connections_total
hexwar_connections_active

# 메모리
hexwar_memory_heap_mb
hexwar_memory_working_set_mb
hexwar_memory_per_session_kb

# GC
hexwar_gc_gen0_count
hexwar_gc_gen1_count
hexwar_gc_gen2_count
hexwar_gc_pause_ms

# 명령 지연
hexwar_command_latency_ms (histogram)
hexwar_command_latency_p99_ms
hexwar_command_latency_p95_ms

# Redis
hexwar_redis_latency_ms
hexwar_redis_lock_contention_count
hexwar_redis_publish_latency_ms
```

### 5.2 알림 임계값 (Alert)

| 지표 | 임계값 | 액션 |
|------|--------|------|
| Gen 2 GC > 2회/분 | 높음 | 메모리 누수 조사 |
| 명령 지연 P99 > 100ms | 높음 | 서버 리소스 확인 |
| Redis 연결 끊김 | 즉시 | 자동 페일오버 |
| 세션 생성 실패율 > 5% | 높음 | 서버 용량 확인 |

---

## 6. 성능 테스트 재현

### 6.1 로컬 환경에서 테스트

```bash
# 1. Redis 시작
docker compose up redis

# 2. 게임 서버 시작
dotnet run --project src/HexWar.Server/HexWar.Server.csproj

# 3. 부하 테스트 실행
dotnet run --project tests/HexWar.LoadTests/HexWar.LoadTests.csproj \
  http://localhost:5051 \
  1000 \  # 게임방 수
  300     # 테스트 시간(초)

# 결과 분석
# ├─ 콘솔 출력: 실시간 진행 상황
# ├─ 최종 리포트: 지연, 처리량, 메모리, GC 통계
# └─ Diagnostics API: curl http://localhost:5051/api/diagnostics/stats
```

### 6.2 실시간 모니터링

```bash
# 진행 중인 서버 상태 확인
watch curl http://localhost:5051/api/diagnostics/stats

# 또는 Grafana 대시보드 (프로메테우스 연동 시)
# http://localhost:3000/d/hexwar/overview
```

---

## 7. 성능 최적화 체크리스트

### 7.1 배포 전 확인

```
메모리:
  ☐ 1인당 메모리 110-120KB 범위
  ☐ Gen 2 GC < 1회/분
  ☐ Working Set < 500MB (2GB 서버 기준)

지연:
  ☐ 평균 지연 < 5ms
  ☐ P99 지연 < 50ms
  ☐ 명령 응답시간 < 100ms

확장성:
  ☐ 서버 추가 시 선형 성능 증가
  ☐ Redis 대역폭 여유 > 30%
  ☐ 락 경쟁 < 1%

안정성:
  ☐ 서버 다운 시 복구 테스트 완료
  ☐ 재연결 시 유실 이벤트 0개
  ☐ 비활성 세션 정리 동작 확인
```

### 7.2 프로덕션 운영 팁

```
1. 일일 모니터링
   - Gen 2 GC 횟수 추세
   - 메모리 점진적 증가 여부 (누수 감지)
   - 평균 지연 변화

2. 주간 점검
   - Redis 디스크 사용량 (AOF 크기)
   - 비활성 세션 정리 로그
   - 네트워크 I/O 대역폭

3. 월간 리뷰
   - Peak 시간대 메트릭 분석
   - 서버 추가 필요성 판단
   - 하드웨어 업그레이드 검토
```

